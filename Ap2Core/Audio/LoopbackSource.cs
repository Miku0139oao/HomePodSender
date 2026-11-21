namespace Ap2Core.Audio;

/// <summary>
/// 系统声音音频源：WASAPI 回环抓取 → 重采样到 44.1kHz → 交给推流线程。
/// 内置漂移控制：根据缓冲余量微调重采样速率，补偿声卡时钟与系统时钟的差异；
/// 系统无声时（回环无数据）自动补静音，恢复发声时先预缓冲再输出，避免开头爆音。
/// </summary>
public sealed class LoopbackSource : IAudioSource, IDisposable
{
    readonly LoopbackCapture _cap;
    readonly object _sync = new();
    float[] _tmp = new float[Ap2Session.Frames * 2];
    StreamResampler? _rs;
    int _target;                  // 目标缓冲（输入帧数，约 40ms）
    double _avg;                  // 缓冲余量的平滑值
    bool _primed;                 // 是否已完成预缓冲
    long _lastDataTicks;
    float _peak;

    long _underruns, _overruns;
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overruns => Interlocked.Read(ref _overruns);
    public string DeviceId => _cap.DeviceId;
    public MixFormat? Format => _cap.Format;
    /// <summary>抓取意外结束（设备变化等）</summary>
    public event Action<string>? CaptureStopped;

    public LoopbackSource(Action<string> log, string? deviceId = null, Action<LoopbackSource, string>? onStopped = null)
    {
        _cap = new LoopbackCapture(log, deviceId);
        _cap.DataAvailable += OnData;
        _cap.Stopped += why =>
        {
            onStopped?.Invoke(this, why);
            CaptureStopped?.Invoke(why);
        };
        try
        {
            _cap.Start();
        }
        catch
        {
            _cap.Dispose();
            throw;
        }
    }

    /// <summary>当前缓冲余量（毫秒，按设备采样率换算），供诊断</summary>
    public double BufferedMs { get { lock (_sync) { var f = _cap.Format; return _rs == null || f == null ? 0 : _rs.BufferedFrames * 1000.0 / f.SampleRate; } } }

    /// <summary>最近一段时间的峰值电平（0..1），读取后清零，供界面显示电平条</summary>
    public float TakePeak() { lock (_sync) { var p = _peak; _peak = 0; return float.IsFinite(p) ? p : 0f; } }

    void OnData(float[] stereo, int frames)
    {
        lock (_sync)
        {
            if (_rs == null)
            {
                var f = _cap.Format!;
                _rs = new StreamResampler(f.SampleRate, Ap2Session.Rate);
                _target = Math.Max(1, f.SampleRate * 40 / 1000);
            }
            long now = Environment.TickCount64;
            if (!_primed && _lastDataTicks > 0 && now - _lastDataTicks > 250)
                _rs.Reset();
            _rs.Push(stereo, frames);
            // 防止连接等待期间或 Read 未调用时缓冲无限增长
            int maxCap = Math.Max(_target * 10, 4096);
            if (_rs.BufferedFrames > maxCap) _rs.TrimTo(_target * 2);
            _lastDataTicks = now;
        }
    }

    public void Read(Span<short> stereo, int frames)
    {
        lock (_sync)
        {
            if (_rs == null) { stereo.Slice(0, frames * 2).Clear(); return; }
            int buffered = _rs.BufferedFrames;

            if (!_primed)
            {
                if (buffered < _target) { stereo.Slice(0, frames * 2).Clear(); return; }
                _primed = true;
                _avg = buffered;
            }

            if (buffered > _target * 6)
            {
                _rs.TrimTo(_target);
                buffered = _rs.BufferedFrames;
                _avg = buffered;
                Interlocked.Increment(ref _overruns);
            }
            _avg = _avg * 0.99 + buffered * 0.01;
            double err = (_avg - _target) / _target;
            double adj = Math.Clamp(err * 0.002, -0.002, 0.002);

            if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
            int got = _rs.Pull(_tmp, frames, adj);
            float peak = _peak;
            for (int i = 0; i < got * 2; i++)
            {
                float v = _tmp[i];
                if (!float.IsFinite(v)) v = 0f;
                float av = Math.Abs(v);
                if (av > peak) peak = Math.Min(av, 1f);
                stereo[i] = (short)Math.Clamp(MathF.Round(v * 32767f), -32768f, 32767f);
            }
            _peak = peak;
            if (got < frames)
            {
                stereo.Slice(got * 2, (frames - got) * 2).Clear();
                _primed = false;   // 数据断档：重新预缓冲
                if (Environment.TickCount64 - _lastDataTicks < 200) Interlocked.Increment(ref _underruns);
            }
        }
    }

    public void Dispose() => _cap.Dispose();
}
