using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ap2Core;

/// <summary>密钥派生与 HAP 随机数</summary>
public static class Crypto
{
    public static byte[] Hkdf(string salt, string info, byte[] ikm, int len = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, len, Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(info));

    /// <summary>HAP 随机数：4 字节 0 + 8 字节小端计数器</summary>
    public static void Nonce12(ulong counter, Span<byte> n12)
    {
        n12.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(n12.Slice(4), counter);
    }
}

/// <summary>HAP 控制通道分帧加密：[2 字节小端长度][密文][16 字节标签]，单帧明文 ≤1024，AAD=长度字节</summary>
public sealed class FrameWriter
{
    readonly ChaCha20Poly1305 _c;
    ulong _ctr;
    public FrameWriter(byte[] key) => _c = new ChaCha20Poly1305(key);

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        var ms = new MemoryStream(data.Length + 64);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> len = stackalloc byte[2];
        for (int off = 0; off < data.Length; off += 1024)
        {
            int n = Math.Min(1024, data.Length - off);
            BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)n);
            Crypto.Nonce12(_ctr++, nonce);
            var ct = new byte[n];
            var tag = new byte[16];
            _c.Encrypt(nonce, data.Slice(off, n), ct, tag, len);
            ms.Write(len);
            ms.Write(ct);
            ms.Write(tag);
        }
        return ms.ToArray();
    }
}

public sealed class FrameReader
{
    readonly ChaCha20Poly1305 _c;
    ulong _ctr;
    byte[] _buf = Array.Empty<byte>();
    public FrameReader(byte[] key) => _c = new ChaCha20Poly1305(key);

    /// <summary>喂入密文，返回已能解出的明文（可能为空）；认证失败抛 CryptographicException</summary>
    public byte[] Push(ReadOnlySpan<byte> data)
    {
        var all = new byte[_buf.Length + data.Length];
        _buf.CopyTo(all, 0);
        data.CopyTo(all.AsSpan(_buf.Length));
        var outMs = new MemoryStream();
        int pos = 0;
        Span<byte> nonce = stackalloc byte[12];
        while (all.Length - pos >= 2)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(all.AsSpan(pos));
            int need = 2 + len + 16;
            if (all.Length - pos < need) break;
            Crypto.Nonce12(_ctr++, nonce);
            var pt = new byte[len];
            _c.Decrypt(nonce, all.AsSpan(pos + 2, len), all.AsSpan(pos + 2 + len, 16), pt, all.AsSpan(pos, 2));
            outMs.Write(pt);
            pos += need;
        }
        _buf = all.AsSpan(pos).ToArray();
        return outMs.ToArray();
    }
}

/// <summary>未压缩 ALAC 帧打包</summary>
public static class Alac
{
    ref struct BitWriter
    {
        public byte[] Buf; public int Pos; uint _acc; int _bits;
        public void Put(uint v, int n)
        {
            _acc = (_acc << n) | v;
            _bits += n;
            while (_bits >= 8) { _bits -= 8; Buf[Pos++] = (byte)(_acc >> _bits); }
            _acc &= (1u << _bits) - 1;
        }
        public void Flush() { if (_bits > 0) Buf[Pos++] = (byte)(_acc << (8 - _bits)); }
    }

    /// <summary>
    /// 按位 MSB 优先：3 位声道元素标签(=1 立体声) | 4 位实例标签(0) | 12 位保留(0) | 1 位 partialFrame(0) |
    /// 2 位 bytesShifted(0) | 1 位 escape(=1 未压缩) | frames×{L16,R16} | 3 位结束标签(=7) | 补零到字节边界
    /// </summary>
    public static int Pack(ReadOnlySpan<short> pcm, int frames, byte[] dst)
    {
        var w = new BitWriter { Buf = dst };
        w.Put((1u << 20) | 1u, 23);
        for (int i = 0; i < frames * 2; i++) w.Put((ushort)pcm[i], 16);
        w.Put(7, 3);
        w.Flush();
        return w.Pos;
    }

    public static int PackedSize(int frames) => (23 + frames * 32 + 3 + 7) / 8;
}

/// <summary>NTP 时间（1900 纪元，高 32 位秒 / 低 32 位小数）：以启动时的系统时间为基准叠加单调时钟增量</summary>
public static class NtpClock
{
    static readonly long BaseUnix100ns = DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks;
    static readonly long BaseSw = Stopwatch.GetTimestamp();

    public static ulong Now()
    {
        long el = (long)((Stopwatch.GetTimestamp() - BaseSw) * (10_000_000.0 / Stopwatch.Frequency));
        long t = BaseUnix100ns + el;
        ulong sec = (ulong)(t / 10_000_000) + 2208988800UL;
        ulong frac = (ulong)((((ulong)(t % 10_000_000)) << 32) / 10_000_000UL);
        return (sec << 32) | frac;
    }

    public static ulong NtpToTs(ulong ntp, int rate) => ((ntp >> 16) * (ulong)rate) >> 16;
    public static ulong TsToNtp(ulong ts, int rate) => ((ts << 16) / (ulong)rate) << 16;
}

/// <summary>RTP 同步包与加密音频包</summary>
public static class Rtp
{
    public static byte[] SyncPacket(bool first, uint rtpMinusLatency, ulong ntp, uint rtpNow)
    {
        var b = new byte[20];
        b[0] = (byte)(first ? 0x90 : 0x80);
        b[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), rtpMinusLatency);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), (uint)(ntp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), (uint)ntp);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), rtpNow);
        return b;
    }

    /// <summary>12 字节 RTP 头 + ChaCha20-Poly1305(密文||标签) + 8 字节随机数；AAD = 头部第 4..12 字节。返回写入长度</summary>
    public static int AudioPacket(ChaCha20Poly1305 c, ulong counter, bool first, ushort seq, uint rtptime, uint ssrc,
        ReadOnlySpan<byte> payload, byte[] dst)
    {
        dst[0] = 0x80;
        dst[1] = (byte)(first ? 0xE0 : 0x60);
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(4), rtptime);
        BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(8), ssrc);
        Span<byte> nonce = stackalloc byte[12];
        Crypto.Nonce12(counter, nonce);
        int n = payload.Length;
        c.Encrypt(nonce, payload, dst.AsSpan(12, n), dst.AsSpan(12 + n, 16), dst.AsSpan(4, 8));
        nonce.Slice(4, 8).CopyTo(dst.AsSpan(12 + n + 16));
        return 12 + n + 16 + 8;
    }
}
