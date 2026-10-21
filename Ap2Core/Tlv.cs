namespace Ap2Core;

/// <summary>HomeKit TLV8 编解码</summary>
public static class Tlv
{
    public const byte Method = 0, Identifier = 1, Salt = 2, PublicKey = 3, Proof = 4, EncryptedData = 5,
        State = 6, Error = 7, RetryDelay = 8, Flags = 19;

    /// <summary>超过 255 字节的值拆成相邻的同 tag 片段</summary>
    public static byte[] Encode(params (byte Tag, byte[] Val)[] items)
    {
        var ms = new MemoryStream();
        foreach (var (tag, val) in items)
        {
            int pos = 0;
            do
            {
                int n = Math.Min(255, val.Length - pos);
                ms.WriteByte(tag);
                ms.WriteByte((byte)n);
                ms.Write(val, pos, n);
                pos += n;
            } while (pos < val.Length);
        }
        return ms.ToArray();
    }

    /// <summary>相邻的同 tag 片段自动拼接</summary>
    public static Dictionary<byte, byte[]> Decode(ReadOnlySpan<byte> d)
    {
        var map = new Dictionary<byte, byte[]>();
        int i = 0;
        int lastTag = -1;
        while (i + 2 <= d.Length)
        {
            byte tag = d[i];
            int len = d[i + 1];
            if (i + 2 + len > d.Length) break;
            var chunk = d.Slice(i + 2, len).ToArray();
            if (lastTag == tag && map.TryGetValue(tag, out var prev))
            {
                var joined = new byte[prev.Length + chunk.Length];
                prev.CopyTo(joined, 0);
                chunk.CopyTo(joined, prev.Length);
                map[tag] = joined;
            }
            else map[tag] = chunk;
            lastTag = tag;
            i += 2 + len;
        }
        return map;
    }
}
