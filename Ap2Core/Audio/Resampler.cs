namespace Ap2Core.Audio;

/// <summary>
/// 流式加窗 sinc 重采样（立体声交错浮点），任意采样率比；支持微调速率以补偿采集时钟与系统时钟的漂移。
/// 核：96 抽头 Kaiser(β=7.5) 窗，256 相位表 + 相邻相位线性插值，每相位归一化使直流增益为 1。
/// 截止频率取目标奈奎斯特频率的 95%，48k→44.1k 时通带约到 20kHz，22.05kHz 以上衰减 &gt;60dB。
/// </summary>
public sealed class StreamResampler
{
    const int Half = 48;          // 半宽（输入采样数），总抽头 96
    const int Phases = 256;       // 相位表分辨率
    const double Beta = 7.5;      // Kaiser 窗参数

    readonly double _baseStep;    // 输入采样率 / 输出采样率
    readonly float[][] _table;    // [Phases+1][2*Half]：输出位置小数部分对应的各抽头系数
    float[] _buf;                 // 交错立体声输入缓冲
    int _count;                   // 缓冲内的帧数
    double _pos;                  // 下一个输出采样在缓冲内的（小数）输入位置

    public StreamResampler(int inRate, int outRate)
    {
        _baseStep = (double)inRate / outRate;
        _table = BuildTable(Math.Min(1.0, (double)outRate / inRate) * 0.95);
        _buf = new float[(Half * 2 + 4096) * 2];
        // 预置 Half-1 帧零作为历史，使第一个输出采样的所有抽头都有定义
        _count = Half - 1;
        _pos = Half - 1;
    }

    static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 60; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
            if (term < 1e-14 * sum) break;
        }
        return sum;
    }

    static float[][] BuildTable(double fc)
    {
        double i0b = BesselI0(Beta);
        var t = new float[Phases + 1][];
        for (int q = 0; q <= Phases; q++)
        {
            double frac = (double)q / Phases;
            var row = new double[2 * Half];
            double sum = 0;
            for (int k = -(Half - 1); k <= Half; k++)
            {
                double d = k - frac;                 // 输入采样 i0+k 到输出位置的距离
                double u = d / Half;
                double w = Math.Abs(u) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - u * u)) / i0b;
                double x = fc * d;
                double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double v = fc * sinc * w;
                row[k + Half - 1] = v;
                sum += v;
            }
            t[q] = row.Select(v => (float)(v / sum)).ToArray();
        }
        return t;
    }

    /// <summary>缓冲内尚未消费的输入帧数（用于漂移控制）</summary>
    public int BufferedFrames => Math.Max(0, _count - (int)_pos);

    /// <summary>追加输入（交错立体声浮点）</summary>
    public void Push(ReadOnlySpan<float> stereo, int frames)
    {
        int need = (_count + frames) * 2;
        if (need > _buf.Length)
        {
            var nb = new float[Math.Max(need, _buf.Length * 2)];
            Array.Copy(_buf, nb, _count * 2);
            _buf = nb;
        }
        stereo.Slice(0, frames * 2).CopyTo(_buf.AsSpan(_count * 2));
        _count += frames;
    }

    /// <summary>丢弃缓冲中最旧的输入，只保留 keepFrames 帧（用于积压过多时追平）</summary>
    public void TrimTo(int keepFrames)
    {
        int avail = BufferedFrames;
        if (avail <= keepFrames) return;
        _pos += avail - keepFrames;   // 直接把读位置前移
        Compact();
    }

    void Compact()
    {
        int drop = (int)_pos - (Half - 1);
        if (drop <= 0) return;
        Array.Copy(_buf, drop * 2, _buf, 0, (_count - drop) * 2);
        _count -= drop;
        _pos -= drop;
    }

    /// <summary>清空缓冲并重置到初始状态（切换音频设备等场景）</summary>
    public void Reset()
    {
        _count = Half - 1;
        _pos = Half - 1;
        Array.Clear(_buf, 0, _buf.Length);
    }

    /// <summary>
    /// 产出最多 frames 帧输出（交错立体声浮点）；输入不足时返回实际产出的帧数。
    /// rateAdjust：相对速率微调（正数 = 更快地消耗输入）。
    /// </summary>
    public int Pull(Span<float> output, int frames, double rateAdjust = 0)
    {
        double step = _baseStep * (1.0 + rateAdjust);
        int produced = 0;
        while (produced < frames)
        {
            int i0 = (int)_pos;
            if (i0 + Half >= _count) break;   // 需要 i0+Half 号输入采样，输入不足
            double fq = (_pos - i0) * Phases;
            int q = (int)fq;
            float a = (float)(fq - q);
            var r0 = _table[q];
            var r1 = _table[q + 1];
            float l = 0, r = 0;
            int bi = (i0 - (Half - 1)) * 2;
            for (int k = 0; k < 2 * Half; k++)
            {
                float c = r0[k] + (r1[k] - r0[k]) * a;
                l += _buf[bi] * c;
                r += _buf[bi + 1] * c;
                bi += 2;
            }
            output[produced * 2] = l;
            output[produced * 2 + 1] = r;
            produced++;
            _pos += step;
        }
        Compact();
        return produced;
    }
}
