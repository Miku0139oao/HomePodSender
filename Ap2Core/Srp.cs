using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Ap2Core;

/// <summary>
/// HomeKit / AirPlay 2 配对用的 SRP-6a 客户端（RFC 5054 3072 位群，SHA-512）。约定与 HAP、pair_ap、pyatv 一致：
///   x = H(salt | H("Pair-Setup:" + PIN))；k = H(N | PAD(g))；u = H(PAD(A) | PAD(B))（PAD 补零到 384 字节）
///   K = H(S)（S 取自然长度）；M1 = H(H(N) xor H(g) | H("Pair-Setup") | salt | A | B | K)；M2 = H(A | M1 | K)
/// </summary>
public sealed class SrpClient
{
    const string NHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    static readonly BigInteger N = BigInteger.Parse("0" + NHex, NumberStyles.HexNumber);
    static readonly BigInteger G = 5;
    /// <summary>仅供测试：公开素数 N</summary>
    public static BigInteger PrimeN => N;
    const int NLen = 384;

    readonly string _password;
    readonly BigInteger _a;
    readonly BigInteger _A;
    byte[] _m1 = Array.Empty<byte>();

    /// <summary>会话密钥 K（64 字节）</summary>
    public byte[] K { get; private set; } = Array.Empty<byte>();
    public byte[] M1 => _m1;

    public SrpClient(string password)
    {
        _password = password;
        // 私钥 a 取 256 位随机；要求 A 恰好 384 字节，避免“自然长度/补零长度”两种实现的歧义
        for (;;)
        {
            _a = FromBytes(RandomNumberGenerator.GetBytes(32));
            _A = BigInteger.ModPow(G, _a, N);
            if (ToBytes(_A).Length == NLen) break;
        }
    }

    /// <summary>仅供测试：注入固定私钥 a，用于与其他语言实现做已知答案比对</summary>
    public SrpClient(string password, byte[] aBytes)
    {
        _password = password;
        _a = FromBytes(aBytes);
        _A = BigInteger.ModPow(G, _a, N);
    }

    public byte[] PublicA => ToBytes(_A);

    static BigInteger FromBytes(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);

    static byte[] ToBytes(BigInteger n, int padTo = 0)
    {
        var b = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (padTo > b.Length)
        {
            var r = new byte[padTo];
            b.CopyTo(r, padTo - b.Length);
            return r;
        }
        return b;
    }

    static byte[] H(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var p in parts) h.AppendData(p);
        return h.GetHashAndReset();
    }

    /// <summary>处理服务端的盐与公钥 B，计算会话密钥 K 与证明 M1；B 异常返回 false</summary>
    public bool Process(byte[] salt, byte[] serverB)
    {
        var B = FromBytes(serverB);
        if (B % N == 0) return false;
        var k = FromBytes(H(ToBytes(N, NLen), ToBytes(G, NLen)));
        var u = FromBytes(H(ToBytes(_A, NLen), ToBytes(B, NLen)));
        var x = FromBytes(H(salt, H(Encoding.UTF8.GetBytes("Pair-Setup:" + _password))));
        var gx = BigInteger.ModPow(G, x, N);
        var bas = (B - (k * gx) % N) % N;
        if (bas.Sign < 0) bas += N;
        var S = BigInteger.ModPow(bas, _a + u * x, N);
        K = H(ToBytes(S));

        var hN = H(ToBytes(N));
        var hg = H(ToBytes(G));
        var hxor = new byte[64];
        for (int i = 0; i < 64; i++) hxor[i] = (byte)(hN[i] ^ hg[i]);
        _m1 = H(hxor, H(Encoding.UTF8.GetBytes("Pair-Setup")), salt, ToBytes(_A), ToBytes(B), K);
        return true;
    }

    /// <summary>校验服务端证明 M2 = H(A | M1 | K)</summary>
    public bool VerifyServerProof(byte[] m2) =>
        CryptographicOperations.FixedTimeEquals(m2, H(ToBytes(_A), _m1, K));
}
