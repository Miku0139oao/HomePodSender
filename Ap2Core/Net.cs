using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ap2Core;

public sealed record RtspResponse(int Status, string StatusText, Dictionary<string, string> Headers, byte[] Body, string Line);

/// <summary>请求-响应式连接（RTSP / HTTP 风格），配对完成后可切换为 HAP 加密分帧；响应与请求严格按序对应</summary>
public sealed class RtspConn : IDisposable
{
    readonly Socket _sock;
    readonly NetworkStream _ns;
    readonly string _name;
    readonly Action<string> _log;
    readonly Queue<TaskCompletionSource<RtspResponse>> _waiters = new();
    readonly object _lock = new();
    FrameReader? _reader;
    FrameWriter? _writer;
    byte[] _rx = Array.Empty<byte>();
    Exception? _closed;

    public bool IsClosed => _closed != null;
    public Exception? CloseReason => _closed;
    public event Action<Exception>? Closed;

    public RtspConn(Socket sock, string name, Action<string> log)
    {
        _sock = sock;
        _ns = new NetworkStream(sock, ownsSocket: false);
        _name = name;
        _log = log;
        _ = Task.Run(ReadLoop);
    }

    const int MaxBodyLength = 16 * 1024 * 1024;
    const int MaxHeaderLength = 64 * 1024;

    static void SendAll(Socket sock, ReadOnlySpan<byte> data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = sock.Send(data.Slice(sent));
            if (n <= 0) throw new IOException("套接字发送中断");
            sent += n;
        }
    }

    public void EnableEncryption(byte[] writeKey, byte[] readKey)
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _writer = new FrameWriter(writeKey);
            _reader = new FrameReader(readKey);
        }
    }

    void Fail(Exception e)
    {
        List<TaskCompletionSource<RtspResponse>> pending;
        lock (_lock)
        {
            if (_closed != null) return;
            _closed = e;
            pending = _waiters.ToList();
            _waiters.Clear();
        }
        foreach (var w in pending) w.TrySetException(e);
        Closed?.Invoke(e);
    }

    async Task ReadLoop()
    {
        var buf = new byte[16384];
        try
        {
            for (;;)
            {
                int n = await _ns.ReadAsync(buf).ConfigureAwait(false);
                if (n == 0) { Fail(new IOException(_name + " 连接已被对端关闭")); return; }
                byte[] plain;
                FrameReader? rd;
                lock (_lock) rd = _reader;
                plain = rd != null ? rd.Push(buf.AsSpan(0, n)) : buf.AsSpan(0, n).ToArray();
                if (plain.Length == 0) continue;
                var joined = new byte[_rx.Length + plain.Length];
                _rx.CopyTo(joined, 0);
                plain.CopyTo(joined, _rx.Length);
                _rx = joined;
                ParseResponses();
            }
        }
        catch (Exception e) { Fail(e); }
    }

    static int IndexOfHeaderEnd(byte[] d)
    {
        for (int i = 0; i + 3 < d.Length; i++)
            if (d[i] == 13 && d[i + 1] == 10 && d[i + 2] == 13 && d[i + 3] == 10) return i;
        return -1;
    }

    void ParseResponses()
    {
        for (;;)
        {
            int i = IndexOfHeaderEnd(_rx);
            if (i < 0)
            {
                if (_rx.Length > MaxHeaderLength) throw new IOException(_name + " 响应头过大");
                return;
            }
            string head = Encoding.Latin1.GetString(_rx, 0, i);
            int clen = 0;
            foreach (var line in head.Split("\r\n").Skip(1))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line.AsSpan(0, c).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line.AsSpan(c + 1).Trim(), out clen) || clen < 0 || clen > MaxBodyLength)
                        throw new IOException(_name + " Content-Length 无效");
                }
            }
            if (_rx.Length < i + 4 + clen) return;
            var body = _rx.AsSpan(i + 4, clen).ToArray();
            _rx = _rx.AsSpan(i + 4 + clen).ToArray();
            var lines = head.Split("\r\n");
            var m = System.Text.RegularExpressions.Regex.Match(lines[0], @"^(\S+)\s+(\d+)\s*(.*)$");
            if (!m.Success) { _log("[" + _name + "] 收到对端发来的请求，已忽略: " + lines[0]); continue; }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
            }
            TaskCompletionSource<RtspResponse>? w;
            lock (_lock) _waiters.TryDequeue(out w);
            if (w == null) { _log("[" + _name + "] 收到无人等待的响应: " + lines[0]); continue; }
            w.TrySetResult(new RtspResponse(int.Parse(m.Groups[2].Value), m.Groups[3].Value, headers, body, lines[0]));
        }
    }

    /// <summary>发送原始请求字节并等待下一个响应</summary>
    public async Task<RtspResponse> ExchangeAsync(byte[] raw, string label, int timeoutMs = 8000)
    {
        var tcs = new TaskCompletionSource<RtspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] wire;
        lock (_lock)
        {
            if (_closed != null) throw _closed;
            _waiters.Enqueue(tcs);
            wire = _writer != null ? _writer.Encrypt(raw) : raw;
        }
        try { SendAll(_sock, wire); }
        catch (Exception e) { Fail(e); throw; }

        using var cts = new CancellationTokenSource(timeoutMs);
        var delay = Task.Delay(Timeout.Infinite, cts.Token);
        var done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(false);
        if (done != tcs.Task)
        {
            // RTSP 请求/响应严格按序，单次超时后必须断开连接避免后续响应永久错位
            var err = new TimeoutException(label + " 等待响应超时 (" + timeoutMs + "ms)");
            Fail(err);
            Dispose();
            throw err;
        }
        cts.Cancel();
        return await tcs.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _sock.Shutdown(SocketShutdown.Both); } catch { }
        try { _ns.Dispose(); } catch { }
        try { _sock.Close(); } catch { }
        lock (_lock)
        {
            _writer?.Dispose(); _writer = null;
            _reader?.Dispose(); _reader = null;
        }
    }
}

/// <summary>事件通道：接收端主动推送加密请求，必须回最小的 200 OK，否则会中途断开</summary>
public sealed class EventChannel : IDisposable
{
    const int MaxBodyLength = 4 * 1024 * 1024;
    const int MaxHeaderLength = 64 * 1024;

    readonly Socket _sock;
    readonly FrameReader _reader;
    readonly FrameWriter _writer;
    readonly Action<string> _log;
    byte[] _rx = Array.Empty<byte>();
    int _count;

    public EventChannel(Socket sock, byte[] inKey, byte[] outKey, Action<string> log)
    {
        _sock = sock;
        _reader = new FrameReader(inKey);
        _writer = new FrameWriter(outKey);
        _log = log;
        _ = Task.Run(ReadLoop);
    }

    static void SendAll(Socket sock, ReadOnlySpan<byte> data)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int n = sock.Send(data.Slice(sent));
            if (n <= 0) throw new IOException("事件通道发送中断");
            sent += n;
        }
    }

    async Task ReadLoop()
    {
        var buf = new byte[8192];
        using var ns = new NetworkStream(_sock, ownsSocket: false);
        try
        {
            for (;;)
            {
                int n = await ns.ReadAsync(buf).ConfigureAwait(false);
                if (n == 0) break;
                var plain = _reader.Push(buf.AsSpan(0, n));
                var joined = new byte[_rx.Length + plain.Length];
                _rx.CopyTo(joined, 0);
                plain.CopyTo(joined, _rx.Length);
                _rx = joined;
                Parse();
            }
        }
        catch (Exception e) { _log("[事件通道] " + e.Message); }
    }

    void Parse()
    {
        for (;;)
        {
            int i = -1;
            for (int k = 0; k + 3 < _rx.Length; k++)
                if (_rx[k] == 13 && _rx[k + 1] == 10 && _rx[k + 2] == 13 && _rx[k + 3] == 10) { i = k; break; }
            if (i < 0)
            {
                if (_rx.Length > MaxHeaderLength) throw new IOException("事件通道请求头过大");
                return;
            }
            string head = Encoding.Latin1.GetString(_rx, 0, i);
            var cl = System.Text.RegularExpressions.Regex.Match(head, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            int clen = 0;
            if (cl.Success && (!int.TryParse(cl.Groups[1].Value, out clen) || clen < 0 || clen > MaxBodyLength))
                throw new IOException("事件通道 Content-Length 无效");
            if (_rx.Length < i + 4 + clen) return;
            _rx = _rx.AsSpan(i + 4 + clen).ToArray();
            _count++;
            if (_count <= 3) _log("[事件通道] 收到: " + head.Split("\r\n")[0]);
            var cs = System.Text.RegularExpressions.Regex.Match(head, @"CSeq:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string resp = "RTSP/1.0 200 OK\r\nServer: AirTunes/670.6.2\r\n" + (cs.Success ? "CSeq: " + cs.Groups[1].Value + "\r\n" : "") + "\r\n";
            try { SendAll(_sock, _writer.Encrypt(Encoding.Latin1.GetBytes(resp))); } catch { }
        }
    }

    public void Dispose()
    {
        try { _sock.Close(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
    }
}

/// <summary>UDP 端口：绑定到指定本机地址的随机端口，后台异步收包</summary>
public sealed class UdpPort : IDisposable
{
    public Socket Sock { get; }
    public int Port { get; }
    volatile bool _disposed;

    public UdpPort(IPAddress local, Action<ReadOnlyMemory<byte>, IPEndPoint>? onPacket)
    {
        Sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // 关闭 Windows 的“ICMP 端口不可达导致下次收包报错”行为（SIO_UDP_CONNRESET）
        try { Sock.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null); } catch { }
        Sock.Bind(new IPEndPoint(local, 0));
        Port = ((IPEndPoint)Sock.LocalEndPoint!).Port;
        if (onPacket != null)
        {
            _ = Task.Run(async () =>
            {
                var buf = new byte[4096];
                EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                while (!_disposed)
                {
                    try
                    {
                        var r = await Sock.ReceiveFromAsync(buf, SocketFlags.None, any).ConfigureAwait(false);
                        onPacket(buf.AsMemory(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
                    }
                    catch (Exception) { if (_disposed) break; await Task.Delay(5).ConfigureAwait(false); }
                }
            });
        }
    }

    public void SendTo(ReadOnlySpan<byte> data, IPEndPoint ep)
    {
        try { Sock.SendTo(data, SocketFlags.None, ep); } catch (SocketException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        try { Sock.Close(); } catch { }
    }
}
