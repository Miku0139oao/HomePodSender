namespace Ap2Core.Audio;

/// <summary>
/// 跟随“默认播放设备”的系统声音源：包装 LoopbackSource，每 2 秒检查默认播放设备是否变化（如插拔耳机、切换输出），
/// 变化或抓取意外结束时自动重新打开抓取，推流会话不中断（切换瞬间补静音）。
/// </summary>
public sealed class SystemAudioSource : IAudioSource, IDisposable
{
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly Timer _watch;
    LoopbackSource? _cur;
    volatile bool _disposed;
    int _failures;

    public SystemAudioSource(Action<string> log)
    {
        _log = log;
        Open();
        _watch = new Timer(_ => Check(), null, 2000, 2000);
    }

    public string DeviceId { get { lock (_lock) return _cur?.DeviceId ?? ""; } }
    public float TakePeak() { LoopbackSource? c; lock (_lock) c = _cur; return c?.TakePeak() ?? 0f; }
    public long Underruns { get { lock (_lock) return _cur?.Underruns ?? 0; } }

    void Open()
    {
        try
        {
            var s = new LoopbackSource(_log);
            s.CaptureStopped += why => OnStopped(s, why);
            lock (_lock) _cur = s;
            _failures = 0;
            _log("系统声音抓取已就绪: " + s.Format);
        }
        catch (Exception e)
        {
            lock (_lock) _cur = null;
            if (_failures++ < 3) _log("打开系统声音抓取失败: " + e.Message);
        }
    }

    void OnStopped(LoopbackSource s, string why)
    {
        if (_disposed) return;
        _log("系统声音抓取中断: " + why + "，稍后重新打开");
        lock (_lock) { if (_cur == s) _cur = null; }
        try { s.Dispose(); } catch { }
    }

    void Check()
    {
        if (_disposed) return;
        try
        {
            string? def = LoopbackCapture.GetDefaultRenderId();
            LoopbackSource? c;
            lock (_lock) c = _cur;
            if (c != null && (def == null || def == c.DeviceId)) return;
            if (c != null) _log("默认播放设备已变化，切换系统声音抓取");
            LoopbackSource? old;
            lock (_lock) { old = _cur; _cur = null; }
            try { old?.Dispose(); } catch { }
            if (def != null) Open();
        }
        catch (Exception e) { _log("检查默认播放设备失败: " + e.Message); }
    }

    public void Read(Span<short> stereo, int frames)
    {
        LoopbackSource? c;
        lock (_lock) c = _cur;
        if (c == null) stereo.Slice(0, frames * 2).Clear();
        else c.Read(stereo, frames);
    }

    public void Dispose()
    {
        _disposed = true;
        _watch.Dispose();
        LoopbackSource? c;
        lock (_lock) { c = _cur; _cur = null; }
        try { c?.Dispose(); } catch { }
    }
}
