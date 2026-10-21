namespace Ap2Core.Audio;

/// <summary>
/// 系统声音音频源：WASAPI 回环抓取 → 重采样到 44.1kHz → 交给推流线程。
/// 内置漂移控制：根据缓冲余量微调重采样速率，补偿声卡时钟与系统时钟的差异；
/// 系统无声时（回环无数据）自动补静音，恢复发声时先预缓冲再输出，避免开头爆音。
/// </summary>
public sealed class LoopbackSource : IAudioSource, IDisposable
{
    readonly LoopbackCapture _cap;
    readonly Action<string> _log;
    readonly object _sync = new();
    readonly float[] _tmp = new float[Ap2Session.Frames * 2];
    StreamResampler? _rs;
    int _target;                  // 目标缓冲（输入帧数，约 40ms）
    double _avg;                  // 缓冲余量的平滑值
    bool _primed;                 // 是否已完成预缓冲
    long _lastDataTicks;
    float _peak;

    public long Underruns, Overruns;
    public string DeviceId => _cap.DeviceId;
    public MixFormat? Format => _cap.Format;
    /// <summary>抓取意外结束（设备变化等）</summary>
    public event Action<string>? CaptureStopped;

    public LoopbackSource(Action<string> log, string? deviceId = null)
    {
        _log = log;
        _cap = new LoopbackCapture(log, deviceId);
        _cap.DataAvailable += OnData;
        _cap.Stopped += why => CaptureStopped?.Invoke(why);
        _cap.Start();
    }

    /// <summary>当前缓冲余量（毫秒，按设备采样率换算），供诊断</summary>
    public double BufferedMs { get { lock (_sync) { var f = _cap.Format; return _rs == null || f == null ? 0 : _rs.BufferedFrames * 1000.0 / f.SampleRate; } } }

    /// <summary>最近一段时间的峰值电平（0..1），读取后清零，供界面显示电平条</summary>
    public float TakePeak() { lock (_sync) { var p = _peak; _peak = 0; return p; } }

    void OnData(float[] stereo, int frames)
    {
        lock (_sync)
        {
            if (_rs == null)
            {
                var f = _cap.Format!;
                _rs = new StreamResampler(f.SampleRate, Ap2Session.Rate);
                _target = f.SampleRate * 40 / 1000;
            }
            _rs.Push(stereo, frames);
            _lastDataTicks = Environment.TickCount64;
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

            _avg = _avg * 0.99 + buffered * 0.01;
            double err = (_avg - _target) / _target;
            double adj = Math.Clamp(err * 0.002, -0.002, 0.002);
            if (buffered > _target * 6) { _rs.TrimTo(_target); Overruns++; }

            int got = _rs.Pull(_tmp, frames, adj);
            float peak = _peak;
            for (int i = 0; i < got * 2; i++)
            {
                float v = _tmp[i];
                float av = Math.Abs(v);
                if (av > peak) peak = av;
                stereo[i] = (short)Math.Clamp(v * 32767f, -32768f, 32767f);
            }
            _peak = peak;
            if (got < frames)
            {
                stereo.Slice(got * 2, (frames - got) * 2).Clear();
                _primed = false;   // 数据断档：重新预缓冲
                if (Environment.TickCount64 - _lastDataTicks < 200) Underruns++;
            }
        }
    }

    public void Dispose() => _cap.Dispose();
}
