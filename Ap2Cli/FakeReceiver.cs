using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ap2Core;

/// <summary>
/// 本机“假 HomePod”：在回环地址上实现接收端的 AirPlay 2 协议（瞬时配对服务端、加密 RTSP、会话/流 SETUP、
/// 事件通道、NTP 授时请求、同步包与音频包解码），用于不联网、不出声的端到端测试。
/// </summary>
sealed class FakeReceiver : IDisposable
{
    record Req(string Method, string Uri, Dictionary<string, string> Headers, byte[] Body);

    readonly TcpListener _listener;
    readonly IPAddress _bind;
    readonly Action<string> _log;
    readonly CancellationTokenSource _cts = new();
    readonly List<IDisposable> _res = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    // 配对与密钥
    byte[]? _rxKey, _txKey, _evRxKey, _evTxKey, _audioKey;
    FrameReader? _reader; FrameWriter? _writer;
    Socket? _client; IPAddress _clientIp = IPAddress.Loopback;
    int _clientTimingPort, _clientControlPort;
    UdpClient? _timing, _control, _data;
    TcpListener? _eventListener;

    // 观测结果
    public volatile bool PairOk, SessionSetup, StreamSetup, Recorded, TornDown;
    public double? LastVolumeDb;
    public int FeedbackCount, EventReplies;
    public long AudioPackets, SyncPackets, TimingReplies, DecryptFailures, OrderErrors, RtpStepErrors, RetransRecovered;
    public double MaxTimingEpochErrorSec, MaxSyncEpochErrorSec;
    public bool TimingReferenceOk = true;
    public readonly ConcurrentDictionary<int, short[]> Pcm = new();
    public int FirstSeq = -1;
    public int MaxAbsSample;
    public readonly List<(uint rtpMinusLatency, double ntpSec, uint rtpNow)> Syncs = new();
    /// <summary>每隔多少包“丢”一个并通过重传请求补回（0=不测）</summary>
    public int DropEvery = 50;
    readonly HashSet<int> _dropped = new();
    ushort _lastSeq; uint _lastRtp; bool _haveLast;

    // SRP 服务端状态
    static readonly BigInteger N = SrpClient.PrimeN;
    static readonly BigInteger G = 5;
    byte[] _salt = RandomNumberGenerator.GetBytes(16);
    BigInteger _b, _B, _v;

    public FakeReceiver(Action<string> log, int port = 0, IPAddress? bind = null)
    {
        _bind = bind ?? IPAddress.Loopback;
        _listener = new TcpListener(_bind, port);
        _log = log;
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    static byte[] ToB(BigInteger n, int pad = 0)
    {
        var b = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (pad > b.Length) { var r = new byte[pad]; b.CopyTo(r, pad - b.Length); return r; }
        return b;
    }
    static BigInteger FromB(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
    static byte[] H(params byte[][] parts) { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); foreach (var p in parts) h.AppendData(p); return h.GetHashAndReset(); }

    /// <summary>已接受的连接数（每次重连加一）</summary>
    public int Connections;

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var c = await _listener.AcceptSocketAsync(_cts.Token);
                ResetSession();
                _client = c;
                Connections++;
                _clientIp = ((IPEndPoint)c.RemoteEndPoint!).Address;
                await HandleClient(c);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { return; }
            catch (Exception e) when (e is SocketException or IOException) { }
            catch (Exception e) { _log("[假接收端] 异常: " + e); }
        }
    }

    /// <summary>新连接到来时清掉上一次会话的状态与资源</summary>
    void ResetSession()
    {
        try { _client?.Close(); } catch { }
        foreach (var r in _res) { try { r.Dispose(); } catch { } }
        _res.Clear();
        try { _eventListener?.Stop(); } catch { }
        _reader = null; _writer = null; _rx = Array.Empty<byte>();
        _salt = RandomNumberGenerator.GetBytes(16);
        _haveLast = false;
        _dropped.Clear();
    }

    /// <summary>模拟 HomePod 突然断开：直接关闭客户端的 TCP 连接</summary>
    public void DropClient() { try { _client?.Close(); } catch { } }

    // ---------- 请求解析 ----------
    byte[] _rx = Array.Empty<byte>();
    bool TryGetReq(out Req req)
    {
        req = null!;
        int i = -1;
        for (int k = 0; k + 3 < _rx.Length; k++) if (_rx[k] == 13 && _rx[k + 1] == 10 && _rx[k + 2] == 13 && _rx[k + 3] == 10) { i = k; break; }
        if (i < 0) return false;
        var lines = Encoding.Latin1.GetString(_rx, 0, i).Split("\r\n");
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1)) { int c = l.IndexOf(':'); if (c > 0) h[l[..c].Trim()] = l[(c + 1)..].Trim(); }
        int clen = h.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
        if (_rx.Length < i + 4 + clen) return false;
        var body = _rx.AsSpan(i + 4, clen).ToArray();
        _rx = _rx.AsSpan(i + 4 + clen).ToArray();
        var parts = lines[0].Split(' ');
        req = new Req(parts[0], parts[1], h, body);
        return true;
    }

    void SendRaw(byte[] data)
    {
        var wire = _writer != null ? _writer.Encrypt(data) : data;
        _client!.Send(wire);
    }

    void Reply(Req r, int status, byte[]? body = null, string? ct = null)
    {
        var sb = new StringBuilder();
        sb.Append("RTSP/1.0 ").Append(status).Append(status == 200 ? " OK" : " Error").Append("\r\n");
        sb.Append("Server: AirTunes/980.77.2\r\n");
        if (r.Headers.TryGetValue("CSeq", out var cs)) sb.Append("CSeq: ").Append(cs).Append("\r\n");
        if (ct != null) sb.Append("Content-Type: ").Append(ct).Append("\r\n");
        sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        var all = body == null ? head : head.Concat(body).ToArray();
        SendRaw(all);
    }

    const string Plist = "application/x-apple-binary-plist";

    async Task HandleClient(Socket s)
    {
        var buf = new byte[16384];
        using var ns = new NetworkStream(s, ownsSocket: false);
        while (!_cts.IsCancellationRequested)
        {
            int n = await ns.ReadAsync(buf, _cts.Token);
            if (n == 0) return;
            var plain = _reader != null ? _reader.Push(buf.AsSpan(0, n)) : buf.AsSpan(0, n).ToArray();
            _rx = _rx.Concat(plain).ToArray();
            while (TryGetReq(out var req)) Handle(req);
        }
    }

    PDict InfoDict() => new()
    {
        { "name", "假 HomePod" }, { "model", "AudioAccessory5,1" }, { "sourceVersion", "980.77.2" }, { "osBuildVersion", "24J361" },
        { "deviceID", "AA:BB:CC:DD:EE:FF" }, { "features", 4338457174016510500L },
    };

    void Handle(Req r)
    {
        switch (r.Method + " " + r.Uri.Split('?')[0])
        {
            case "GET /info":
                Reply(r, 200, Bplist.Encode(InfoDict()), Plist);
                break;
            case "POST /pair-setup":
                HandlePair(r);
                break;
            case "POST /feedback":
                FeedbackCount++;
                Reply(r, 200, Bplist.Encode(new PDict { { "streams", new List<object>() } }), Plist);
                break;
            default:
                if (r.Method == "SETUP") HandleSetup(r);
                else if (r.Method == "RECORD") { Recorded = true; Reply(r, 200); }
                else if (r.Method == "SET_PARAMETER")
                {
                    var t = Encoding.Latin1.GetString(r.Body);
                    if (t.StartsWith("volume:") && double.TryParse(t[7..].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var db)) LastVolumeDb = db;
                    Reply(r, 200);
                }
                else if (r.Method == "TEARDOWN") { if (r.Body.Length == 0) TornDown = true; Reply(r, 200); }
                else Reply(r, 404);
                break;
        }
    }

    // ---------- 瞬时配对（服务端） ----------
    BigInteger _A; byte[] _K = Array.Empty<byte>();
    void HandlePair(Req r)
    {
        var t = Tlv.Decode(r.Body);
        byte state = t[Tlv.State][0];
        if (state == 1)
        {
            var x = FromB(H(_salt, H(Encoding.UTF8.GetBytes("Pair-Setup:3939"))));
            _v = BigInteger.ModPow(G, x, N);
            var k = FromB(H(ToB(N, 384), ToB(G, 384)));
            _b = FromB(RandomNumberGenerator.GetBytes(32));
            _B = (k * _v + BigInteger.ModPow(G, _b, N)) % N;
            Reply(r, 200, Tlv.Encode((Tlv.State, new byte[] { 2 }), (Tlv.Salt, _salt), (Tlv.PublicKey, ToB(_B))), "application/octet-stream");
        }
        else if (state == 3)
        {
            var aBytes = t[Tlv.PublicKey];
            var m1 = t[Tlv.Proof];
            _A = FromB(aBytes);
            var u = FromB(H(ToB(_A, 384), ToB(_B, 384)));
            var S = BigInteger.ModPow(_A * BigInteger.ModPow(_v, u, N) % N, _b, N);
            _K = H(ToB(S));
            var hN = H(ToB(N)); var hg = H(ToB(G));
            var hx = new byte[64]; for (int i = 0; i < 64; i++) hx[i] = (byte)(hN[i] ^ hg[i]);
            var expect = H(hx, H(Encoding.UTF8.GetBytes("Pair-Setup")), _salt, aBytes, ToB(_B), _K);
            if (!CryptographicOperations.FixedTimeEquals(expect, m1))
            {
                Reply(r, 200, Tlv.Encode((Tlv.State, new byte[] { 4 }), (Tlv.Error, new byte[] { 2 })), "application/octet-stream");
                return;
            }
            var m2 = H(aBytes, m1, _K);
            Reply(r, 200, Tlv.Encode((Tlv.State, new byte[] { 4 }), (Tlv.Proof, m2)), "application/octet-stream");
            // 之后全部加密：接收端解密用客户端的“写”密钥，加密用“读”密钥
            _rxKey = Crypto.Hkdf("Control-Salt", "Control-Write-Encryption-Key", _K);
            _txKey = Crypto.Hkdf("Control-Salt", "Control-Read-Encryption-Key", _K);
            _evTxKey = Crypto.Hkdf("Events-Salt", "Events-Write-Encryption-Key", _K);
            _evRxKey = Crypto.Hkdf("Events-Salt", "Events-Read-Encryption-Key", _K);
            _reader = new FrameReader(_rxKey);
            _writer = new FrameWriter(_txKey);
            PairOk = true;
        }
    }

    // ---------- SETUP ----------
    void HandleSetup(Req r)
    {
        var d = Bplist.Decode(r.Body) as PDict ?? new PDict();
        if (d.Get("streams") is List<object> { Count: > 0 } streams && streams[0] is PDict s0)
        {
            _audioKey = (s0.Get("shk") as byte[])!;
            _clientControlPort = (int)s0.GetInt("controlPort");
            _control = new UdpClient(new IPEndPoint(_bind, 0));
            _data = new UdpClient(new IPEndPoint(_bind, 0));
            _res.Add(_control); _res.Add(_data);
            _ = Task.Run(() => ControlLoop());
            _ = Task.Run(() => DataLoop());
            StreamSetup = true;
            var resp = new PDict { { "streams", new List<object> { new PDict {
                { "type", 96 }, { "dataPort", ((IPEndPoint)_data.Client.LocalEndPoint!).Port }, { "controlPort", ((IPEndPoint)_control.Client.LocalEndPoint!).Port }, { "arrivalToRenderLatencyMs", 70 } } } } };
            Reply(r, 200, Bplist.Encode(resp), Plist);
        }
        else
        {
            _clientTimingPort = (int)d.GetInt("timingPort");
            _eventListener = new TcpListener(_bind, 0);
            _eventListener.Start();
            _ = Task.Run(EventLoop);
            _timing = new UdpClient(new IPEndPoint(_bind, 0));
            _res.Add(_timing);
            _ = Task.Run(TimingLoop);
            SessionSetup = true;
            Reply(r, 200, Bplist.Encode(new PDict { { "skipRecord", true }, { "eventPort", ((IPEndPoint)_eventListener.LocalEndpoint).Port } }), Plist);
        }
    }

    static double NtpSecNow() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + 2208988800.0;
    static double NtpToSec(ulong ntp) => (double)(ntp >> 32) + (double)(ntp & 0xFFFFFFFF) / 4294967296.0;

    // ---------- 事件通道：接收端主动发一个加密请求，客户端必须回 200 ----------
    async Task EventLoop()
    {
        try
        {
            using var s = await _eventListener!.AcceptSocketAsync(_cts.Token);
            var wr = new FrameWriter(_evTxKey!);
            var rd = new FrameReader(_evRxKey!);
            await Task.Delay(200);
            s.Send(wr.Encrypt(Encoding.Latin1.GetBytes("POST /command RTSP/1.0\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n")));
            var buf = new byte[4096];
            using var ns = new NetworkStream(s, ownsSocket: false);
            int n = await ns.ReadAsync(buf, _cts.Token);
            var txt = Encoding.Latin1.GetString(rd.Push(buf.AsSpan(0, n)));
            if (txt.StartsWith("RTSP/1.0 200")) EventReplies++;
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch { }
    }

    // ---------- NTP 授时：每 500ms 主动请求一次，并校验应答 ----------
    async Task TimingLoop()
    {
        var ep = new IPEndPoint(_clientIp, _clientTimingPort);
        try
        {
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var r = await _timing!.ReceiveAsync(_cts.Token);
                    var m = r.Buffer;
                    if (m.Length != 32 || (m[1] & 0x7F) != 0x53) continue;
                    double recv = NtpToSec(BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(16)));
                    double send = NtpToSec(BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(24)));
                    double now = NtpSecNow();
                    MaxTimingEpochErrorSec = Math.Max(MaxTimingEpochErrorSec, Math.Max(Math.Abs(recv - now), Math.Abs(send - now)));
                    ulong refTime = BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(8));
                    if (refTime != _lastTimingSent) TimingReferenceOk = false;
                    TimingReplies++;
                }
            });
            for (;;)
            {
                await Task.Delay(500, _cts.Token);
                var req = new byte[32];
                req[0] = 0x80; req[1] = 0xD2; req[3] = 7;
                double sec = NtpSecNow();
                ulong whole = (ulong)Math.Floor(sec);
                ulong frac = (ulong)((sec - Math.Floor(sec)) * 4294967296.0);
                _lastTimingSent = (whole << 32) | frac;
                BinaryPrimitives.WriteUInt64BigEndian(req.AsSpan(24), _lastTimingSent);
                await _timing!.SendAsync(req, ep, _cts.Token);
            }
        }
        catch { }
    }
    ulong _lastTimingSent;

    // ---------- 控制端口：接收同步包；对“丢掉”的包发重传请求 ----------
    async Task ControlLoop()
    {
        try
        {
            for (;;)
            {
                var r = await _control!.ReceiveAsync(_cts.Token);
                var m = r.Buffer;
                int type = m[1] & 0x7F;
                if (type == 0x54 && m.Length >= 20)
                {
                    SyncPackets++;
                    uint a = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(4));
                    ulong ntp = BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(8));
                    uint now = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(16));
                    double sec = NtpToSec(ntp);
                    MaxSyncEpochErrorSec = Math.Max(MaxSyncEpochErrorSec, Math.Abs(sec - NtpSecNow()));
                    lock (Syncs) Syncs.Add((a, sec, now));
                }
                else if (type == 0x56 && m.Length > 4 + 12)
                {
                    // 重传应答：[0x80,0xD6,seq(2)] + 原始包
                    ProcessAudio(m.AsSpan(4).ToArray(), retransmitted: true);
                }
            }
        }
        catch { }
    }

    // ---------- 数据端口：解密并解码音频 ----------
    async Task DataLoop()
    {
        try
        {
            for (;;)
            {
                var r = await _data!.ReceiveAsync(_cts.Token);
                ProcessAudio(r.Buffer, retransmitted: false);
            }
        }
        catch { }
    }

    void ProcessAudio(byte[] p, bool retransmitted)
    {
        if (p.Length < 12 + 16 + 8) return;
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
        uint rtp = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4));
        if (!retransmitted)
        {
            if (DropEvery > 0 && seq % DropEvery == 7 && !_dropped.Contains(seq))
            {
                // 模拟丢包：丢弃本包，向客户端发重传请求
                _dropped.Add(seq);
                var rq = new byte[8];
                rq[0] = 0x80; rq[1] = 0xD5; rq[3] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(rq.AsSpan(4), seq);
                BinaryPrimitives.WriteUInt16BigEndian(rq.AsSpan(6), 1);
                _ = _control!.SendAsync(rq, new IPEndPoint(_clientIp, _clientControlPort));
                return;
            }
            if (_haveLast)
            {
                // 相邻包的序号差应为 1；刚丢过一个包时为 2。RTP 时间戳每包增加 352。
                int gap = (ushort)(seq - _lastSeq);
                bool okGap = gap == 1 || (gap == 2 && _dropped.Contains((ushort)(_lastSeq + 1)));
                if (!okGap) OrderErrors++;
                if (rtp - _lastRtp != 352u * (uint)gap) RtpStepErrors++;
            }
            _lastSeq = seq; _lastRtp = rtp; _haveLast = true;
        }
        else RetransRecovered++;

        // 解密：nonce = 4 个 0 + 尾部 8 字节；AAD = 头部第 4..12 字节
        int n = p.Length - 12 - 16 - 8;
        var nonce = new byte[12];
        p.AsSpan(p.Length - 8, 8).CopyTo(nonce.AsSpan(4));
        var pt = new byte[n];
        try
        {
            using var c = new ChaCha20Poly1305(_audioKey!.AsSpan(0, 32).ToArray());
            c.Decrypt(nonce, p.AsSpan(12, n), p.AsSpan(12 + n, 16), pt, p.AsSpan(4, 8));
        }
        catch (CryptographicException) { DecryptFailures++; return; }
        if (FirstSeq < 0) FirstSeq = seq;
        var decoded = DecodeAlac(pt);
        foreach (short v in decoded) { int av = Math.Abs((int)v); if (av > MaxAbsSample) MaxAbsSample = av; }
        Pcm[seq] = decoded;
        AudioPackets++;
    }

    static short[] DecodeAlac(byte[] d)
    {
        int bit = 0;
        int Get(int k) { int v = 0; for (int i = 0; i < k; i++) { v = (v << 1) | ((d[bit >> 3] >> (7 - (bit & 7))) & 1); bit++; } return v; }
        if (Get(3) != 1 || Get(4) != 0 || Get(12) != 0 || Get(1) != 0 || Get(2) != 0 || Get(1) != 1) return Array.Empty<short>();
        var pcm = new short[352 * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Get(16);
        return pcm;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _eventListener?.Stop(); } catch { }
        try { _client?.Close(); } catch { }
        foreach (var r in _res) { try { r.Dispose(); } catch { } }
    }
}
