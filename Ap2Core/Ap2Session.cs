using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ap2Core;

/// <summary>音频源：由推流线程每 352 帧调用一次，必须快速且不阻塞</summary>
public interface IAudioSource
{
    /// <summary>填充 frames 帧交错的 16 位立体声采样（44.1kHz）</summary>
    void Read(Span<short> stereo, int frames);
}

public sealed class SilenceSource : IAudioSource
{
    public void Read(Span<short> stereo, int frames) => stereo.Slice(0, frames * 2).Clear();
}

/// <summary>每秒响 0.6 秒的纯音，首尾 5ms 淡入淡出避免爆音</summary>
public sealed class ToneSource : IAudioSource
{
    readonly double _freq, _amp;
    long _n;
    public ToneSource(double freq = 440, double amp = 0.25) { _freq = freq; _amp = amp; }

    public void Read(Span<short> stereo, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            double t = _n++ / 44100.0;
            double ph = t % 1;
            double env = ph < 0.005 ? ph / 0.005 : ph < 0.595 ? 1 : ph < 0.6 ? (0.6 - ph) / 0.005 : 0;
            short v = (short)Math.Round(Math.Sin(2 * Math.PI * _freq * t) * _amp * env * 32767);
            stereo[i * 2] = v;
            stereo[i * 2 + 1] = v;
        }
    }
}

public sealed class Ap2Options
{
    public string Ip = "";
    public int Port = 7000;
    /// <summary>会话 SETUP 字典：rich（pyatv 风格）或 min（airplay-cli 风格）</summary>
    public string Dict = "rich";
    /// <summary>RECORD 时机：before（流 SETUP 之前）/ after / none</summary>
    public string Record = "before";
    public bool AdvertiseDataPort;
    public bool Verbose;
    /// <summary>播放提前量（采样帧），必须落在接收端 11025..88200 的缓冲窗口内</summary>
    public int LatencyFrames = 66150;
    public string DeviceName = "Windows PC";
    /// <summary>本机标识 MAC 形式，用于 deviceID/macAddress；空则每次随机</summary>
    public string DeviceId = "";
}

/// <summary>
/// AirPlay 2 实时音频发送会话（面向 HomePod）：瞬时配对 → 加密 RTSP → 会话 SETUP(NTP 授时) → 事件通道 →
/// RECORD → 流 SETUP(ALAC 实时流) → UDP 推流(未压缩 ALAC + ChaCha20-Poly1305) + 同步包 + 授时应答
/// </summary>
public sealed class Ap2Session : IDisposable
{
    public const int Frames = 352;
    public const int Rate = 44100;
    const string UA = "AirPlay/670.6.2";
    const int Backlog = 1024;

    readonly Ap2Options _o;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly SemaphoreSlim _rtspLock = new(1, 1);
    readonly IPAddress _ip;

    IPAddress _localIp = IPAddress.Any;
    RtspConn? _conn;
    EventChannel? _events;
    UdpPort? _timing, _ctrl, _audio;
    ChaCha20Poly1305? _audioCipher;

    byte[] _ctrlWrite = Array.Empty<byte>(), _ctrlRead = Array.Empty<byte>();
    byte[] _eventIn = Array.Empty<byte>(), _eventOut = Array.Empty<byte>();
    byte[] _audioKey = Array.Empty<byte>();

    readonly uint _sessionId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    readonly string _dacpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    readonly uint _activeRemote = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    readonly string _deviceId;
    int _cseq;
    string _sessionUrl = "";

    int _remoteDataPort, _remoteControlPort;
    IPEndPoint? _dataEp, _ctrlEp;

    // 推流状态
    IAudioSource? _source;
    Thread? _thread;
    volatile bool _stop;
    CancellationTokenSource? _feedbackCts;
    long _framesSent;
    ulong _startNtpTs;
    ulong _audioCtr;
    ushort _seq;
    bool _first = true;
    readonly short[] _pcm = new short[Frames * 2];
    readonly byte[] _alac = new byte[Alac.PackedSize(Frames)];
    readonly byte[] _pkt = new byte[12 + Alac.PackedSize(Frames) + 16 + 8];
    readonly byte[][] _backlog = new byte[Backlog][];
    readonly int[] _backlogSeq = new int[Backlog];
    readonly object _backlogLock = new();

    public long AudioPackets, SyncPackets, TimingReplies, RetransRequests, RetransSent;
    public PDict? DeviceInfo { get; private set; }
    public bool IsStreaming => _thread != null && !_stop;
    public bool IsConnected => _conn != null && !_conn.IsClosed;

    public event Action<string>? LogMessage;
    /// <summary>对端断开或心跳连续失败时触发（只触发一次）</summary>
    public event Action<string>? Disconnected;
    int _disconnectedRaised;

    public Ap2Session(Ap2Options o)
    {
        _o = o;
        _ip = IPAddress.Parse(o.Ip);
        _deviceId = string.IsNullOrEmpty(o.DeviceId)
            ? "02:" + string.Join(":", RandomNumberGenerator.GetBytes(5).Select(b => b.ToString("X2")))
            : o.DeviceId;
        Array.Fill(_backlogSeq, -1);
    }

    void Log(string m) => LogMessage?.Invoke("[" + (_clock.ElapsedMilliseconds / 1000.0).ToString("0.00").PadLeft(6) + "s] " + m);

    // ---------- RTSP 请求 ----------
    byte[] BuildRequest(string method, string uri, string? ct, byte[]? body, string[]? extra, bool minimal)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(Interlocked.Increment(ref _cseq)).Append("\r\n");
        sb.Append("User-Agent: ").Append(UA).Append("\r\n");
        if (!minimal)
        {
            sb.Append("DACP-ID: ").Append(_dacpId).Append("\r\n");
            sb.Append("Active-Remote: ").Append(_activeRemote).Append("\r\n");
        }
        if (ct != null) sb.Append("Content-Type: ").Append(ct).Append("\r\n");
        if (extra != null) foreach (var e in extra) sb.Append(e).Append("\r\n");
        sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        if (body == null || body.Length == 0) return head;
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all, head.Length);
        return all;
    }

    async Task<RtspResponse> CallAsync(string method, string uri, string label, string? ct = null, byte[]? body = null,
        string[]? extra = null, bool minimal = false, int timeoutMs = 8000)
    {
        await _rtspLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_conn == null) throw new InvalidOperationException("未连接");
            var raw = BuildRequest(method, uri, ct, body, extra, minimal);
            var r = await _conn.ExchangeAsync(raw, label, timeoutMs).ConfigureAwait(false);
            if (_o.Verbose) Log("  " + label + " -> " + r.Line + (r.Body.Length > 0 ? "  body=" + r.Body.Length + "B" : ""));
            return r;
        }
        finally { _rtspLock.Release(); }
    }

    static PDict? AsPlist(RtspResponse r) =>
        r.Body.Length > 0 && Bplist.IsBplist(r.Body) ? Bplist.Decode(r.Body) as PDict : null;

    // ---------- 连接与握手 ----------
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(5000);
            try { await sock.ConnectAsync(new IPEndPoint(_ip, _o.Port), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { sock.Dispose(); throw new TimeoutException("连接 " + _o.Ip + ":" + _o.Port + " 超时"); }
        }
        _localIp = ((IPEndPoint)sock.LocalEndPoint!).Address;
        Log("TCP 已连接 " + _o.Ip + ":" + _o.Port + "，本机地址 " + _localIp);
        _conn = new RtspConn(sock, "控制通道", Log);
        _conn.Closed += e => RaiseDisconnected("控制连接已断开: " + e.Message);

        // 1. 明文 GET /info（顺便确认 User-Agent 被接受）
        var info = await CallAsync("GET", "/info", "GET /info(明文)").ConfigureAwait(false);
        DeviceInfo = AsPlist(info);
        Log("GET /info -> " + info.Status + (DeviceInfo != null ? "  设备=" + DeviceInfo.GetStr("name") + " 型号=" + DeviceInfo.GetStr("model") + " 版本=" + DeviceInfo.GetStr("sourceVersion") : ""));
        if (info.Status != 200) throw new IOException("GET /info 失败: " + info.Line);

        // 2. 瞬时配对（固定 PIN 3939，只走 M1-M4）
        await PairTransientAsync().ConfigureAwait(false);

        // 3. 配对完成后的控制通道全部加密
        _conn.EnableEncryption(_ctrlWrite, _ctrlRead);
        Log("控制通道已切换为加密");
        var info2 = await CallAsync("GET", "/info", "GET /info(加密)").ConfigureAwait(false);
        Log("GET /info(加密) -> " + info2.Status);

        // 4. 本机 UDP 端口：授时、控制、音频
        _timing = new UdpPort(_localIp, OnTiming);
        _ctrl = new UdpPort(_localIp, OnControl);
        _audio = new UdpPort(_localIp, null);
        Log("本机 UDP 端口: 授时=" + _timing.Port + " 控制=" + _ctrl.Port + " 音频=" + _audio.Port);

        // 5. 会话 SETUP
        _sessionUrl = "rtsp://" + _localIp + "/" + _sessionId;
        PDict sd = new()
        {
            { "deviceID", _deviceId }, { "sessionUUID", _sessionUuid }, { "timingPort", _timing.Port }, { "timingProtocol", "NTP" },
        };
        if (_o.Dict != "min")
        {
            sd.Add("isMultiSelectAirPlay", true);
            sd.Add("groupContainsGroupLeader", false);
            sd.Add("macAddress", _deviceId);
            sd.Add("model", "iPhone14,3");
            sd.Add("name", _o.DeviceName);
            sd.Add("osBuildVersion", "20F66");
            sd.Add("osName", "iPhone OS");
            sd.Add("osVersion", "16.5");
            sd.Add("senderSupportsRelay", false);
            sd.Add("sourceVersion", "690.7.1");
            sd.Add("statsCollectionEnabled", false);
        }
        var s1 = await CallAsync("SETUP", _sessionUrl, "会话 SETUP", "application/x-apple-binary-plist", Bplist.Encode(sd)).ConfigureAwait(false);
        var s1p = AsPlist(s1);
        Log("会话 SETUP -> " + s1.Status + "  " + (s1p != null ? Bplist.Pretty(s1p) : ""));
        if (s1.Status != 200 || s1p == null) throw new IOException("会话 SETUP 失败: " + s1.Line);

        // 6. 事件通道
        int eventPort = (int)s1p.GetInt("eventPort");
        if (eventPort != 0) await OpenEventsAsync(eventPort).ConfigureAwait(false);

        // 7. RECORD / 流 SETUP（顺序可选）
        if (_o.Record == "before") await RecordAsync().ConfigureAwait(false);
        await SetupStreamAsync().ConfigureAwait(false);
        if (_o.Record == "after") await RecordAsync().ConfigureAwait(false);
    }

    async Task PairTransientAsync()
    {
        Task<RtspResponse> Post(byte[] body) => CallAsync("POST", "/pair-setup", "pair-setup", "application/octet-stream", body, new[] { "X-Apple-HKP: 4" }, minimal: true);

        var m1 = Tlv.Encode((Tlv.State, new byte[] { 1 }), (Tlv.Method, new byte[] { 0 }), (Tlv.Flags, new byte[] { 0x10 }));
        var r1 = await Post(m1).ConfigureAwait(false);
        if (r1.Status != 200) throw new IOException("pair-setup M1 被拒绝: " + r1.Line);
        var t2 = Tlv.Decode(r1.Body);
        if (t2.TryGetValue(Tlv.Error, out var e2)) throw new IOException("pair-setup M2 返回错误码 " + e2[0]);
        if (!t2.TryGetValue(Tlv.Salt, out var salt) || !t2.TryGetValue(Tlv.PublicKey, out var B) || salt.Length != 16)
            throw new IOException("pair-setup M2 内容异常");
        Log("配对 M2 收到: 盐 16B，公钥 B " + B.Length + "B");

        var srp = new SrpClient("3939");
        if (!srp.Process(salt, B)) throw new IOException("SRP 参数无效");
        var m3 = Tlv.Encode((Tlv.State, new byte[] { 3 }), (Tlv.PublicKey, srp.PublicA), (Tlv.Proof, srp.M1));
        var r3 = await Post(m3).ConfigureAwait(false);
        if (r3.Status != 200) throw new IOException("pair-setup M3 被拒绝: " + r3.Line);
        var t4 = Tlv.Decode(r3.Body);
        if (t4.TryGetValue(Tlv.Error, out var e4)) throw new IOException("pair-setup M4 返回错误码 " + e4[0] + "（2=认证失败）");
        if (!t4.TryGetValue(Tlv.Proof, out var proof) || !srp.VerifyServerProof(proof)) throw new IOException("服务端证明 M2 校验失败");
        Log("配对完成：服务端证明校验通过");

        // 派生密钥（输入为完整的 64 字节 SRP 会话密钥）
        var K = srp.K;
        _ctrlWrite = Crypto.Hkdf("Control-Salt", "Control-Write-Encryption-Key", K);
        _ctrlRead = Crypto.Hkdf("Control-Salt", "Control-Read-Encryption-Key", K);
        _eventIn = Crypto.Hkdf("Events-Salt", "Events-Write-Encryption-Key", K);
        _eventOut = Crypto.Hkdf("Events-Salt", "Events-Read-Encryption-Key", K);
        _audioKey = K.AsSpan(0, 32).ToArray();
        _audioCipher = new ChaCha20Poly1305(_audioKey);
    }

    async Task OpenEventsAsync(int port)
    {
        for (int i = 0; i < 5; i++)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var cts = new CancellationTokenSource(3000);
                await s.ConnectAsync(new IPEndPoint(_ip, port), cts.Token).ConfigureAwait(false);
                _events = new EventChannel(s, _eventIn, _eventOut, Log);
                Log("事件通道已连接 " + _o.Ip + ":" + port);
                return;
            }
            catch (Exception e) { s.Dispose(); Log("事件通道连接失败，重试: " + e.Message); await Task.Delay(500).ConfigureAwait(false); }
        }
        Log("事件通道无法建立（继续，但接收端可能中途断开）");
    }

    async Task RecordAsync()
    {
        var r = await CallAsync("RECORD", _sessionUrl, "RECORD").ConfigureAwait(false);
        Log("RECORD -> " + r.Status);
    }

    async Task SetupStreamAsync()
    {
        var stream = new PDict
        {
            { "audioFormat", 0x40000 }, { "audioMode", "default" }, { "controlPort", _ctrl!.Port }, { "ct", 2 }, { "isMedia", true },
            { "latencyMax", 88200 }, { "latencyMin", 11025 }, { "shk", _audioKey }, { "spf", Frames }, { "sr", Rate }, { "type", 0x60 },
            { "supportsDynamicStreamID", false }, { "streamConnectionID", (long)_sessionId },
        };
        if (_o.AdvertiseDataPort) stream.Add("dataPort", _audio!.Port);
        var req = new PDict { { "streams", new List<object> { stream } } };
        var r = await CallAsync("SETUP", _sessionUrl, "流 SETUP", "application/x-apple-binary-plist", Bplist.Encode(req)).ConfigureAwait(false);
        var p = AsPlist(r);
        Log("流 SETUP -> " + r.Status + "  " + (p != null ? Bplist.Pretty(p) : ""));
        if (r.Status != 200 || p?.Get("streams") is not List<object> { Count: > 0 } streams || streams[0] is not PDict s0)
            throw new IOException("流 SETUP 失败: " + r.Line);
        _remoteDataPort = (int)s0.GetInt("dataPort");
        _remoteControlPort = (int)s0.GetInt("controlPort");
        if (_remoteControlPort == 0) _remoteControlPort = _remoteDataPort;
        if (_remoteDataPort == 0) throw new IOException("流 SETUP 未返回 dataPort");
        _dataEp = new IPEndPoint(_ip, _remoteDataPort);
        _ctrlEp = new IPEndPoint(_ip, _remoteControlPort);
    }

    // ---------- UDP 处理 ----------
    void OnTiming(ReadOnlyMemory<byte> mem, IPEndPoint from)
    {
        var msg = mem.Span;
        if (msg.Length < 32 || (msg[1] & 0x7F) != 0x52) return;
        ulong rx = NtpClock.Now();
        Span<byte> o = stackalloc byte[32];
        o[0] = msg[0]; o[1] = 0xD3; o[2] = 0; o[3] = 7;
        msg.Slice(24, 8).CopyTo(o.Slice(8, 8));    // 参考时间 = 请求的发送时间
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(o.Slice(16, 8), rx);   // 接收时间
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(o.Slice(24, 8), NtpClock.Now());   // 发送时间
        _timing?.SendTo(o, from);
        if (Interlocked.Increment(ref TimingReplies) == 1) Log("收到第一个授时请求（来自 " + from + "），已应答");
    }

    void OnControl(ReadOnlyMemory<byte> mem, IPEndPoint from)
    {
        var msg = mem.Span;
        if (msg.Length < 8 || (msg[1] & 0x7F) != 0x55) return;
        ushort seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(4));
        int n = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(msg.Slice(6));
        Interlocked.Add(ref RetransRequests, n);
        for (int i = 0; i < n; i++)
        {
            ushort s = (ushort)(seq + i);
            byte[]? copy = null;
            lock (_backlogLock)
            {
                int slot = s & (Backlog - 1);
                if (_backlogSeq[slot] == s) copy = _backlog[slot];
            }
            if (copy == null) continue;
            var resp = new byte[4 + copy.Length];
            resp[0] = 0x80; resp[1] = 0xD6; resp[2] = (byte)(s >> 8); resp[3] = (byte)s;
            copy.CopyTo(resp, 4);
            _ctrl?.SendTo(resp, from);
            Interlocked.Increment(ref RetransSent);
        }
    }

    // ---------- 推流 ----------
    void SendSync(bool first)
    {
        ulong curNtp = NtpClock.TsToNtp(_startNtpTs + (ulong)_framesSent, Rate);
        uint now = unchecked((uint)(_o.LatencyFrames + _framesSent));
        var pkt = Rtp.SyncPacket(first, unchecked(now - (uint)_o.LatencyFrames), curNtp, now);
        _ctrl!.SendTo(pkt, _ctrlEp!);
        SyncPackets++;
    }

    void SendAudio()
    {
        _source!.Read(_pcm, Frames);
        Alac.Pack(_pcm, Frames, _alac);
        uint rtptime = unchecked((uint)(_o.LatencyFrames + _framesSent));
        int len = Rtp.AudioPacket(_audioCipher!, _audioCtr++, _first, _seq, rtptime, _sessionId, _alac, _pkt);
        _audio!.SendTo(_pkt.AsSpan(0, len), _dataEp!);
        lock (_backlogLock)
        {
            int slot = _seq & (Backlog - 1);
            var buf = _backlog[slot];
            if (buf == null || buf.Length != len) buf = _backlog[slot] = new byte[len];
            Buffer.BlockCopy(_pkt, 0, buf, 0, len);
            _backlogSeq[slot] = _seq;
        }
        _first = false;
        _seq++;
        _framesSent += Frames;
        AudioPackets++;
    }

    /// <summary>设置 HomePod 音量（AirPlay 的 dB 值：-30..0，-144 为静音）</summary>
    public async Task SetVolumeDbAsync(double db)
    {
        var body = Encoding.Latin1.GetBytes("volume: " + db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
        var r = await CallAsync("SET_PARAMETER", _sessionUrl, "设置音量", "text/parameters", body).ConfigureAwait(false);
        Log("音量 " + db.ToString("0.0") + " dB -> " + r.Status);
    }

    /// <summary>百分比（0..100）换算为 dB：0 为静音，其余线性映射到 -30..0 dB</summary>
    public static double PercentToDb(double pct) => pct < 0.5 ? -144.0 : -30.0 + 0.3 * Math.Clamp(pct, 0, 100);

    /// <summary>开始推流（连接成功后调用）；音频源由推流线程拉取</summary>
    public void StartStreaming(IAudioSource source)
    {
        if (_thread != null) throw new InvalidOperationException("已在推流");
        _source = source;
        _framesSent = 0;
        _first = true;
        _audioCtr = 0;
        _seq = BitConverter.ToUInt16(RandomNumberGenerator.GetBytes(2));
        _startNtpTs = NtpClock.NtpToTs(NtpClock.Now(), Rate);
        _stop = false;
        _thread = new Thread(StreamLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Ap2Stream" };
        _thread.Start();
        _feedbackCts = new CancellationTokenSource();
        _ = Task.Run(() => FeedbackLoop(_feedbackCts.Token));
        Log("开始推流");
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    void StreamLoop()
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            long nextSyncMs = 0;
            SendSync(true);
            nextSyncMs = 1000;
            while (!_stop)
            {
                long target = sw.ElapsedTicks * Rate / Stopwatch.Frequency;
                // 追赶上限：单次循环最多补 16 包，避免瞬时堵塞后洪泛
                for (int n = 0; _framesSent + Frames <= target && n < 16; n++) SendAudio();
                if (sw.ElapsedMilliseconds >= nextSyncMs) { SendSync(false); nextSyncMs += 1000; }
                Thread.Sleep(1);
            }
        }
        catch (Exception e) { Log("推流线程异常: " + e.Message); RaiseDisconnected("推流线程异常: " + e.Message); }
        finally { timeEndPeriod(1); }
    }

    /// <summary>每 2 秒一次 POST /feedback 作为心跳；连续 3 次失败视为断开</summary>
    async Task FeedbackLoop(CancellationToken ct)
    {
        int fails = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            try
            {
                await CallAsync("POST", "/feedback", "feedback", timeoutMs: 4000).ConfigureAwait(false);
                fails = 0;
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                Log("feedback 失败(" + (fails + 1) + "): " + e.Message);
                if (++fails >= 3) { RaiseDisconnected("心跳连续失败"); return; }
            }
        }
    }

    void RaiseDisconnected(string why)
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0) return;
        Log("会话断开: " + why);
        Disconnected?.Invoke(why);
    }

    /// <summary>停止推流并礼貌地拆除会话</summary>
    public async Task StopAsync()
    {
        _stop = true;
        _feedbackCts?.Cancel();
        _thread?.Join(1500);
        _thread = null;
        try
        {
            if (_conn is { IsClosed: false } && _sessionUrl != "")
            {
                var body = Bplist.Encode(new PDict { { "streams", new List<object> { new PDict { { "type", 0x60 } } } } });
                await CallAsync("TEARDOWN", _sessionUrl, "TEARDOWN(流)", "application/x-apple-binary-plist", body, timeoutMs: 1500).ConfigureAwait(false);
                await CallAsync("TEARDOWN", _sessionUrl, "TEARDOWN(会话)", timeoutMs: 1500).ConfigureAwait(false);
            }
        }
        catch (Exception e) { Log("TEARDOWN: " + e.Message); }
        Dispose();
    }

    public void Dispose()
    {
        _stop = true;
        _feedbackCts?.Cancel();
        Interlocked.Exchange(ref _disconnectedRaised, 1);   // 主动关闭不再触发断开事件
        _timing?.Dispose(); _ctrl?.Dispose(); _audio?.Dispose();
        _events?.Dispose();
        _conn?.Dispose();
        _timing = _ctrl = _audio = null;
    }
}
