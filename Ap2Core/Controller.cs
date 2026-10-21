using System.Net.Sockets;
using Ap2Core.Audio;

namespace Ap2Core;

public enum StreamState { Disconnected, Connecting, Streaming, Reconnecting }

public sealed class ControllerSettings
{
    public string Ip { get; set; } = "";
    /// <summary>同时推流的地址。有内容时以它为准；空则只用 <see cref="Ip"/>。两台都用同一个端口，除非 <see cref="Ports"/> 另有指定</summary>
    public List<string> Ips { get; set; } = new();
    public int Port { get; set; } = 7000;
    /// <summary>个别地址的端口（测试用）。没写的地址用 <see cref="Port"/></summary>
    public Dictionary<string, int> Ports { get; set; } = new();
    /// <summary>播放提前量（毫秒），范围 250..2000；越大越抗网络抖动，越小越接近实时</summary>
    public int LatencyMs { get; set; } = 1500;
    public int VolumePercent { get; set; } = 20;
    public bool AutoReconnect { get; set; } = true;
    public string DeviceId { get; set; } = "";

    /// <summary>这次要连接的地址。多台时保持调用方给的顺序</summary>
    public List<string> Targets()
    {
        var list = new List<string>();
        foreach (var s in Ips.ToArray())
            if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s.Trim())) list.Add(s.Trim());
        if (list.Count == 0 && !string.IsNullOrWhiteSpace(Ip)) list.Add(Ip.Trim());
        return list;
    }

    public int PortOf(string ip) => Ports.TryGetValue(ip, out var p) && p > 0 ? p : Port;
}

/// <summary>
/// 与界面无关的连接控制器：连接 / 断开 / 断线自动重连 / 音量（防抖）/ 电平。
/// 事件可能在任意线程触发，界面层需自行切回 UI 线程。
/// </summary>
public sealed class HomePodController : IDisposable
{
    sealed class Live
    {
        public required string Ip { get; init; }
        public required Ap2Session Session { get; init; }
    }

    readonly SemaphoreSlim _op = new(1, 1);
    readonly object _stateLock = new();
    readonly List<Live> _live = new();
    readonly HashSet<string> _reconnecting = new();
    IAudioSource? _source;
    PcmBus? _bus;
    CancellationTokenSource? _reconnectCts;
    bool _wantConnected;
    Timer? _volumeTimer;
    bool _disposed;

    public ControllerSettings Settings { get; }
    public StreamState State { get; private set; } = StreamState.Disconnected;
    public string Status { get; private set; } = "未连接";
    public event Action? Changed;
    public event Action<string>? LogMessage;

    /// <summary>音频源工厂；默认抓取系统声音，测试时可替换</summary>
    public Func<Action<string>, IAudioSource> SourceFactory { get; set; } = log => new SystemAudioSource(log);

    public HomePodController(ControllerSettings settings) => Settings = settings;

    void Log(string m) => LogMessage?.Invoke(m);

    void SetState(StreamState s, string status)
    {
        lock (_stateLock) { State = s; Status = status; }
        Changed?.Invoke();
    }

    static string Friendly(Exception e) => e switch
    {
        TimeoutException => "HomePod 无响应，请检查 IP 与网络",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "对方拒绝连接（该地址不是 AirPlay 设备？）",
        SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.TimedOut } => "找不到该地址，请检查 IP",
        SocketException => "网络错误",
        IOException ioe when ioe.Message.Contains("返回错误码") => "配对被拒绝，请在家庭 App 里允许同一网络的任何人访问",
        _ => e.Message,
    };

    // ---------- 连接 ----------
    /// <summary>用户点击“连接”。一台都连不上时抛出异常（状态已回到未连接并带原因）</summary>
    public async Task ConnectAsync()
    {
        if (Settings.Targets().Count == 0) throw new InvalidOperationException("请先填写 HomePod 的 IP 地址");
        _wantConnected = true;
        CancelReconnect();
        try { await ConnectCoreAsync().ConfigureAwait(false); }
        catch { _wantConnected = false; throw; }
    }

    async Task ConnectCoreAsync()
    {
        await _op.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var targets = Settings.Targets();
            if (targets.Count == 0) throw new InvalidOperationException("请先填写 HomePod 的 IP 地址");
            await StopAllUnlocked().ConfigureAwait(false);
            SetState(StreamState.Connecting, targets.Count == 1 ? "正在连接 " + targets[0] + " …" : "正在连接 " + targets.Count + " 台…");
            EnsureSource();

            var opened = await Task.WhenAll(targets.Select(OpenOneAsync)).ConfigureAwait(false);
            var ok = new List<(string Ip, Ap2Session Session)>();
            var bad = new List<(string Ip, Exception Error)>();
            foreach (var r in opened)
            {
                if (r.Session != null) ok.Add((r.Ip, r.Session));
                else bad.Add((r.Ip, r.Error ?? new IOException("连接失败")));
            }

            // 音量设好、游标对齐之后再一起开推，两路听到的是同一段采样
            var ready = new List<Live>();
            foreach (var (ip, session) in ok)
            {
                try
                {
                    await session.SetVolumeDbAsync(Ap2Session.PercentToDb(Settings.VolumePercent)).ConfigureAwait(false);
                    ready.Add(new Live { Ip = ip, Session = session });
                }
                catch (Exception e)
                {
                    Log(ip + " 设置音量失败: " + e.Message);
                    try { session.Dispose(); } catch { }
                    bad.Add((ip, e));
                }
            }
            var branches = ready.Select(_ => new BusSource(_bus!)).ToList();
            for (int i = 0; i < ready.Count; i++) ready[i].Session.StartStreaming(branches[i]);
            lock (_stateLock) _live.AddRange(ready);

            if (ready.Count == 0)
            {
                DropSource();
                var e = bad[0].Error;
                Log("连接失败: " + e.Message);
                SetState(StreamState.Disconnected, "连接失败：" + Friendly(e));
                throw e;
            }

            foreach (var (ip, e) in bad) Log(ip + " 连接失败: " + e.Message);
            SetState(StreamState.Streaming, PlayingStatus(targets.Count));
            if (bad.Count > 0 && Settings.AutoReconnect)
                foreach (var (ip, _) in bad) StartReconnectOne(ip);
        }
        finally { _op.Release(); }
    }

    async Task<(string Ip, Ap2Session? Session, Exception? Error)> OpenOneAsync(string ip)
    {
        Ap2Session? s = null;
        try
        {
            s = new Ap2Session(new Ap2Options
            {
                Ip = ip,
                Port = Settings.PortOf(ip),
                LatencyFrames = Math.Clamp(Settings.LatencyMs, 250, 2000) * Ap2Session.Rate / 1000,
                DeviceId = Settings.DeviceId,
            });
            s.LogMessage += line => Log(ip + " " + line);
            var captured = s;
            s.Disconnected += why => OnSessionDisconnected(captured, why);
            await s.ConnectAsync().ConfigureAwait(false);
            return (ip, s, null);
        }
        catch (Exception e)
        {
            try { s?.Dispose(); } catch { }
            return (ip, null, e);
        }
    }

    string PlayingStatus(int wanted)
    {
        int n;
        lock (_stateLock) n = _live.Count;
        if (wanted <= 1 && n <= 1) return "已连接，正在播放系统声音";
        return n == wanted ? $"已连接 {n} 台，正在播放系统声音" : $"已连接 {n}/{wanted} 台，正在播放系统声音";
    }

    void EnsureSource()
    {
        lock (_stateLock)
        {
            if (_source != null && _bus != null) return;
            _source = SourceFactory(Log);
            _bus = new PcmBus(_source);
        }
    }

    void DropSource()
    {
        IAudioSource? src;
        lock (_stateLock) { src = _source; _source = null; _bus = null; }
        try { (src as IDisposable)?.Dispose(); } catch { }
    }

    // ---------- 断开 ----------
    public async Task DisconnectAsync()
    {
        _wantConnected = false;
        CancelReconnect();
        await _op.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAllUnlocked().ConfigureAwait(false);
            SetState(StreamState.Disconnected, "已断开");
        }
        finally { _op.Release(); }
    }

    async Task StopAllUnlocked()
    {
        List<Live> copy;
        lock (_stateLock) { copy = _live.ToList(); _live.Clear(); }
        foreach (var l in copy)
        {
            try { await l.Session.StopAsync().ConfigureAwait(false); }
            catch (Exception e) { Log("断开时出错: " + e.Message); }
        }
        DropSource();
    }

    void CancelReconnect()
    {
        var c = Interlocked.Exchange(ref _reconnectCts, null);
        try { c?.Cancel(); c?.Dispose(); } catch { }
        lock (_stateLock) _reconnecting.Clear();
    }

    CancellationToken ArmReconnect()
    {
        while (true)
        {
            var cur = _reconnectCts;
            if (cur != null && !cur.IsCancellationRequested) return cur.Token;
            var cts = new CancellationTokenSource();
            if (Interlocked.CompareExchange(ref _reconnectCts, cts, cur) == cur) return cts.Token;
            cts.Dispose();
        }
    }

    void OnSessionDisconnected(Ap2Session s, string why)
    {
        if (_disposed) return;
        string? ip = null;
        bool anyLeft;
        lock (_stateLock)
        {
            var live = _live.FirstOrDefault(x => x.Session == s);
            if (live == null) return;   // 已被主动断开或替换
            _live.Remove(live);
            ip = live.Ip;
            anyLeft = _live.Count > 0;
        }
        Log(ip + " 会话断开: " + why);
        try { s.Dispose(); } catch { }
        if (_wantConnected && Settings.AutoReconnect)
        {
            if (!anyLeft)
            {
                DropSource();
                var cts = new CancellationTokenSource();
                var old = Interlocked.Exchange(ref _reconnectCts, cts);
                try { old?.Cancel(); old?.Dispose(); } catch { }
                lock (_stateLock) _reconnecting.Clear();
                _ = Task.Run(() => ReconnectLoop(cts.Token));
            }
            else StartReconnectOne(ip!);
        }
        else if (!anyLeft) SetState(StreamState.Disconnected, "连接已断开：" + why);
        else SetState(StreamState.Streaming, PlayingStatus(Settings.Targets().Count));
    }

    void StartReconnectOne(string ip)
    {
        lock (_stateLock) { if (!_reconnecting.Add(ip)) return; }
        var token = ArmReconnect();
        _ = Task.Run(async () =>
        {
            try { await ReconnectOne(ip, token).ConfigureAwait(false); }
            finally { lock (_stateLock) _reconnecting.Remove(ip); }
        });
    }

    async Task ReconnectLoop(CancellationToken ct)
    {
        for (int i = 1; i <= 15 && !ct.IsCancellationRequested; i++)
        {
            SetState(StreamState.Reconnecting, $"连接中断，正在重连（第 {i} 次）…");
            try { await Task.Delay(i <= 3 ? 2000 : 5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested || !_wantConnected) return;
            try { await ConnectCoreAsync().ConfigureAwait(false); return; }
            catch (OperationCanceledException) { return; }
            catch { /* 继续重试 */ }
        }
        if (!ct.IsCancellationRequested) { _wantConnected = false; SetState(StreamState.Disconnected, "重连失败，已放弃（请检查 HomePod 与网络后手动连接）"); }
    }

    /// <summary>只补回掉线的那一台，另一台继续播</summary>
    async Task ReconnectOne(string ip, CancellationToken ct)
    {
        for (int i = 1; i <= 15 && !ct.IsCancellationRequested; i++)
        {
            try { await Task.Delay(i <= 3 ? 2000 : 5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested || !_wantConnected) return;
            try { await _op.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Ap2Session? born = null;
            try
            {
                if (_disposed || !_wantConnected || ct.IsCancellationRequested) return;
                PcmBus? bus;
                lock (_stateLock)
                {
                    if (_live.Any(x => x.Ip == ip) || _bus == null) return;
                    bus = _bus;
                }
                var opened = await OpenOneAsync(ip).ConfigureAwait(false);
                if (opened.Session == null || ct.IsCancellationRequested || _bus != bus)
                {
                    try { opened.Session?.Dispose(); } catch { }
                    if (_bus != bus || ct.IsCancellationRequested) return;
                    continue;
                }
                born = opened.Session;
                await born.SetVolumeDbAsync(Ap2Session.PercentToDb(Settings.VolumePercent)).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _bus != bus) { born.Dispose(); return; }
                var branch = new BusSource(bus);
                born.StartStreaming(branch);
                lock (_stateLock) _live.Add(new Live { Ip = ip, Session = born });
                born = null;
                SetState(StreamState.Streaming, PlayingStatus(Settings.Targets().Count));
                return;
            }
            catch (OperationCanceledException) { try { born?.Dispose(); } catch { } return; }
            catch (Exception e) { Log(ip + " 重连失败: " + e.Message); try { born?.Dispose(); } catch { } }
            finally { _op.Release(); }
        }
        if (!ct.IsCancellationRequested) Log(ip + " 重连失败，已放弃");
    }

    // ---------- 音量（防抖） ----------
    public void SetVolumePercent(int pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        Settings.VolumePercent = pct;
        if (_disposed) return;
        var t = _volumeTimer ??= new Timer(async _ => await ApplyVolumeAsync().ConfigureAwait(false));
        t.Change(120, Timeout.Infinite);
    }

    async Task ApplyVolumeAsync()
    {
        List<Ap2Session> sessions;
        lock (_stateLock) sessions = _live.Select(x => x.Session).ToList();
        if (sessions.Count == 0) return;
        double db = Ap2Session.PercentToDb(Settings.VolumePercent);
        foreach (var s in sessions)
        {
            try { await s.SetVolumeDbAsync(db).ConfigureAwait(false); }
            catch (Exception e) { Log("设置音量失败: " + e.Message); }
        }
    }

    /// <summary>最近一段时间系统声音的峰值（0..1）；未连接时为 0</summary>
    public float TakePeak()
    {
        IAudioSource? src;
        lock (_stateLock) src = _source;
        return src is SystemAudioSource sas ? sas.TakePeak() : 0f;
    }

    public void Dispose()
    {
        _disposed = true;
        _wantConnected = false;
        CancelReconnect();
        _volumeTimer?.Dispose();
        List<Live> copy;
        lock (_stateLock) { copy = _live.ToList(); _live.Clear(); }
        foreach (var l in copy) { try { l.Session.StopAsync().Wait(3000); } catch { } }
        DropSource();
    }
}
