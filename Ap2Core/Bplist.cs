using System.Buffers.Binary;
using System.Text;

namespace Ap2Core;

/// <summary>有序字典：bplist 编码时保持键的插入顺序</summary>
public sealed class PDict : List<KeyValuePair<string, object>>
{
    public void Add(string key, object value) => Add(new KeyValuePair<string, object>(key, value));

    public object? Get(string key)
    {
        foreach (var kv in this) if (kv.Key == key) return kv.Value;
        return null;
    }

    public long GetInt(string key, long def = 0) => Get(key) is long l ? l : def;
    public string? GetStr(string key) => Get(key) as string;
}

/// <summary>显式实数（普通整数类型默认按整数编码）</summary>
public sealed record PReal(double Value);

/// <summary>最小二进制 plist（bplist00）编解码，只覆盖 AirPlay 2 握手用到的类型</summary>
public static class Bplist
{
    // ---------- 编码 ----------
    static byte[] Cat(params byte[][] parts)
    {
        int n = 0;
        foreach (var p in parts) n += p.Length;
        var r = new byte[n];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    static byte[] EncInt(long n)
    {
        if (n >= 0 && n <= 0xFF) return new byte[] { 0x10, (byte)n };
        if (n >= 0 && n <= 0xFFFF) { var b = new byte[3]; b[0] = 0x11; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(1), (ushort)n); return b; }
        if (n >= 0 && n <= 0xFFFFFFFFL) { var b = new byte[5]; b[0] = 0x12; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1), (uint)n); return b; }
        var l = new byte[9]; l[0] = 0x13; BinaryPrimitives.WriteInt64BigEndian(l.AsSpan(1), n); return l;
    }

    // 长度 <15 直接放进低 4 位，否则 0xF 后跟一个整数对象
    static byte[] Marker(int baseByte, int len) =>
        len < 15 ? new[] { (byte)(baseByte | len) } : Cat(new[] { (byte)(baseByte | 0x0F) }, EncInt(len));

    static byte[] EncStr(string s)
    {
        foreach (char c in s)
        {
            if (c < 0x80) continue;
            var u16 = Encoding.BigEndianUnicode.GetBytes(s);
            return Cat(Marker(0x60, u16.Length / 2), u16);
        }
        return Cat(Marker(0x50, s.Length), Encoding.ASCII.GetBytes(s));
    }

    static byte[] Ref4(int i) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)i); return b; }

    static int Add(List<byte[]?> objs, object v)
    {
        int idx = objs.Count;
        objs.Add(null);   // 先占位，保证根对象是 0 号
        byte[] o;
        switch (v)
        {
            case bool b: o = new[] { (byte)(b ? 0x09 : 0x08) }; break;
            case int or long or uint or short or ushort or byte: o = EncInt(Convert.ToInt64(v)); break;
            case PReal r: o = new byte[9]; o[0] = 0x23; BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(1), r.Value); break;
            case string s: o = EncStr(s); break;
            case byte[] bytes: o = Cat(Marker(0x40, bytes.Length), bytes); break;
            case PDict d:
            {
                var kr = new List<byte[]>(); var vr = new List<byte[]>();
                foreach (var kv in d) { kr.Add(Ref4(Add(objs, kv.Key))); vr.Add(Ref4(Add(objs, kv.Value))); }
                var parts = new List<byte[]> { Marker(0xD0, d.Count) };
                parts.AddRange(kr); parts.AddRange(vr);
                o = Cat(parts.ToArray());
                break;
            }
            case IEnumerable<object> list:
            {
                var refs = new List<byte[]>();
                int count = 0;
                foreach (var item in list) { refs.Add(Ref4(Add(objs, item))); count++; }
                var parts = new List<byte[]> { Marker(0xA0, count) };
                parts.AddRange(refs);
                o = Cat(parts.ToArray());
                break;
            }
            default: throw new NotSupportedException("bplist 不支持的类型: " + v.GetType());
        }
        objs[idx] = o;
        return idx;
    }

    public static byte[] Encode(object root)
    {
        var objs = new List<byte[]?>();
        Add(objs, root);
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("bplist00"));
        var offsets = new List<int>();
        foreach (var o in objs) { offsets.Add((int)ms.Length); ms.Write(o!); }
        int offTable = (int)ms.Length;
        foreach (int off in offsets) ms.Write(Ref4(off));
        var trailer = new byte[32];
        trailer[6] = 4;   // 偏移表项宽度
        trailer[7] = 4;   // 对象引用宽度
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objs.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offTable);
        ms.Write(trailer);
        return ms.ToArray();
    }

    // ---------- 解码 ----------
    public static bool IsBplist(ReadOnlySpan<byte> d) => d.Length >= 40 && d[0] == 'b' && d[1] == 'p' && d[2] == 'l' && d[3] == 'i' && d[4] == 's' && d[5] == 't';

    public static object? Decode(byte[] buf)
    {
        if (!IsBplist(buf)) throw new FormatException("不是 bplist");
        const int MaxObjects = 65536;
        int bodyEnd = buf.Length - 32;
        var t = buf.AsSpan(bodyEnd);
        int offSize = t[6], refSize = t[7];
        if (offSize is < 1 or > 8 || refSize is < 1 or > 8)
            throw new FormatException("bplist 宽度无效");
        ulong numObjsU = BinaryPrimitives.ReadUInt64BigEndian(t.Slice(8));
        ulong topU = BinaryPrimitives.ReadUInt64BigEndian(t.Slice(16));
        ulong offTableU = BinaryPrimitives.ReadUInt64BigEndian(t.Slice(24));
        if (numObjsU == 0 || numObjsU > MaxObjects || topU >= numObjsU)
            throw new FormatException("bplist 对象数无效");
        long numObjs = (long)numObjsU;
        long top = (long)topU;
        if (offTableU < 8 || offTableU > (ulong)bodyEnd || (ulong)numObjs * (ulong)offSize > (ulong)bodyEnd - offTableU)
            throw new FormatException("bplist 偏移表越界");
        long offTable = (long)offTableU;

        ulong Rd(long off, int n)
        {
            if (n < 1 || n > 8 || off < 0 || off > buf.Length - n)
                throw new FormatException("bplist 读取越界");
            ulong v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | buf[off + i];
            return v;
        }
        var offsets = new long[numObjs];
        for (long i = 0; i < numObjs; i++)
        {
            ulong o = Rd(offTable + i * offSize, offSize);
            if (o < 8 || o >= (ulong)offTable) throw new FormatException("bplist 对象偏移越界");
            offsets[i] = (long)o;
        }

        object? ReadObj(long idx, int depth)
        {
            if (depth > 32) throw new FormatException("bplist 嵌套过深");
            if (idx < 0 || idx >= numObjs) throw new FormatException("bplist 对象索引越界");
            long p = offsets[idx];
            if (p >= offTable) throw new FormatException("bplist 对象越界");
            byte m = buf[p++];
            int hi = m >> 4, lo = m & 15;
            (int n, int q) Len()
            {
                if (lo != 15) return (lo, (int)p);
                if (p >= offTable) throw new FormatException("bplist 长度标记越界");
                byte lm = buf[p];
                if ((lm >> 4) != 1 || (lm & 15) > 3) throw new FormatException("bplist 长度类型无效");
                int w = 1 << (lm & 15);
                ulong lenU = Rd(p + 1, w);
                if (lenU > (ulong)offTable) throw new FormatException("bplist 长度越界");
                return ((int)lenU, (int)(p + 1 + w));
            }
            switch (hi)
            {
                case 0x0: return lo == 8 ? false : lo == 9 ? true : null;
                case 0x1:
                {
                    if (lo > 3) throw new FormatException("bplist 整数宽度不支持");
                    int n = 1 << lo;
                    return (long)Rd(p, n);
                }
                case 0x2:
                {
                    int n = lo == 2 ? 4 : lo == 3 ? 8 : throw new FormatException("bplist 浮点宽度不支持");
                    if (p + n > offTable) throw new FormatException("bplist 浮点越界");
                    return lo == 2 ? BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan((int)p, 4)) : BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan((int)p, 8));
                }
                case 0x3:
                {
                    if (p + 8 > offTable) throw new FormatException("bplist 日期越界");
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan((int)p, 8)));
                }
                case 0x4:
                {
                    var (n, q) = Len();
                    if (q > offTable - n) throw new FormatException("bplist 数据越界");
                    return buf.AsSpan(q, n).ToArray();
                }
                case 0x5:
                {
                    var (n, q) = Len();
                    if (q > offTable - n) throw new FormatException("bplist ASCII 越界");
                    return Encoding.Latin1.GetString(buf, q, n);
                }
                case 0x6:
                {
                    var (n, q) = Len();
                    if (n > (offTable - q) / 2) throw new FormatException("bplist UTF-16 越界");
                    return Encoding.BigEndianUnicode.GetString(buf, q, n * 2);
                }
                case 0xA:
                {
                    var (n, q) = Len();
                    if ((long)n * refSize > offTable - q) throw new FormatException("bplist 数组越界");
                    var a = new List<object>(n);
                    for (int i = 0; i < n; i++) a.Add(ReadObj((long)Rd(q + (long)i * refSize, refSize), depth + 1)!);
                    return a;
                }
                case 0xD:
                {
                    var (n, q) = Len();
                    if ((long)n * 2 * refSize > offTable - q) throw new FormatException("bplist 字典越界");
                    var d = new PDict();
                    for (int i = 0; i < n; i++)
                    {
                        var k = ReadObj((long)Rd(q + (long)i * refSize, refSize), depth + 1) as string ?? "";
                        var v = ReadObj((long)Rd(q + (long)(n + i) * refSize, refSize), depth + 1);
                        d.Add(k, v!);
                    }
                    return d;
                }
                default: return "(未支持类型 0x" + hi.ToString("x") + ")";
            }
        }
        return ReadObj(top, 0);
    }

    /// <summary>调试输出：长二进制数据截断显示</summary>
    public static string Pretty(object? v)
    {
        switch (v)
        {
            case null: return "null";
            case byte[] b: return "<" + b.Length + "B " + Convert.ToHexString(b, 0, Math.Min(b.Length, 12)).ToLowerInvariant() + (b.Length > 12 ? "…" : "") + ">";
            case string s: return "\"" + s + "\"";
            case bool bo: return bo ? "true" : "false";
            case PDict d: return "{" + string.Join(", ", d.Select(kv => kv.Key + ": " + Pretty(kv.Value))) + "}";
            case List<object> l: return "[" + string.Join(", ", l.Select(Pretty)) + "]";
            default: return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
    }
}
