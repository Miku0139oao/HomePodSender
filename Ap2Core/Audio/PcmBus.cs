namespace Ap2Core.Audio;

/// <summary>
/// 把一份系统声音分给多路推流。先到的那路向音源要下一块，其余路复制同一块，
/// 这样两台 HomePod 听到的是同一段采样，而不是各读各的、互相把缓冲吃掉。
/// </summary>
public sealed class PcmBus
{
    /// <summary>环形缓冲能记住的块数。落后超过这么多就跳到当前，避免读到被覆盖的旧块</summary>
    public const int Blocks = 64;

    readonly IAudioSource _inner;
    readonly int _frames;
    readonly short[] _ring;
    readonly object _gate = new();
    long _head;

    public PcmBus(IAudioSource inner, int frames = Ap2Session.Frames)
    {
        _inner = inner;
        _frames = frames;
        _ring = new short[Blocks * frames * 2];
    }

    /// <summary>新订阅者从当前最新位置开始，不补放已经推过的声音</summary>
    public long LiveCursor { get { lock (_gate) return _head; } }

    public void Read(ref long cursor, Span<short> dst, int frames)
    {
        if (frames != _frames) throw new ArgumentException("帧数与音源总线不一致");
        int samples = frames * 2;
        lock (_gate)
        {
            if (cursor < _head - Blocks) cursor = _head;
            if (cursor == _head)
            {
                int slot = (int)(_head % Blocks);
                _inner.Read(_ring.AsSpan(slot * samples, samples), frames);
                _head++;
            }
            int at = (int)(cursor % Blocks);
            _ring.AsSpan(at * samples, samples).CopyTo(dst);
            cursor++;
        }
    }
}

/// <summary>一路推流在总线上的读游标。每路一个，互不消耗对方的采样</summary>
public sealed class BusSource : IAudioSource
{
    readonly PcmBus _bus;
    long _cursor;

    public BusSource(PcmBus bus) { _bus = bus; _cursor = bus.LiveCursor; }

    public void Read(Span<short> stereo, int frames) => _bus.Read(ref _cursor, stereo, frames);
}
