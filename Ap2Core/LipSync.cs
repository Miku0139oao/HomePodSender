using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Ap2Core;

/// <summary>
/// Chrome 画面对齐。
/// 音频进程禁止加载未签名模块，Secure Boot 也装不了自签声卡驱动，所以不改浏览器时钟。
/// 扩展只把网页画面推迟 LatencyMs，视频元素的声音仍按原时刻进回环。
/// </summary>
public static class LipSyncPack
{
    public const int Port = 17321;

    public static byte[] BuildCrx(RSA key)
    {
        byte[] zip = ZipExtension();
        byte[] publicKey = key.ExportSubjectPublicKeyInfo();
        byte[] crxId = SHA256.HashData(publicKey).AsSpan(0, 16).ToArray();
        byte[] signedHeader = ProtoBytes(1, crxId);
        byte[] toSign = new byte[4 + signedHeader.Length + zip.Length];
        BitConverter.GetBytes(signedHeader.Length).CopyTo(toSign, 0); // 小端长度，只参与签名
        signedHeader.CopyTo(toSign, 4);
        zip.CopyTo(toSign, 4 + signedHeader.Length);
        byte[] signature = key.SignData(toSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        byte[] proof = Concat(ProtoBytes(1, publicKey), ProtoBytes(2, signature));
        byte[] header = Concat(ProtoBytes(2, proof), ProtoBytes(10000, signedHeader));
        byte[] file = new byte[12 + header.Length + zip.Length];
        file[0] = (byte)'C'; file[1] = (byte)'r'; file[2] = (byte)'2'; file[3] = (byte)'4';
        BitConverter.GetBytes(3).CopyTo(file, 4);
        BitConverter.GetBytes(header.Length).CopyTo(file, 8);
        header.CopyTo(file, 12);
        zip.CopyTo(file, 12 + header.Length);
        return file;
    }

    /// <summary>扩展 ID：公钥 SHA-256 前 16 字节，每个半字节映射成 a–p</summary>
    public static string ExtensionId(RSA key)
    {
        byte[] hash = SHA256.HashData(key.ExportSubjectPublicKeyInfo());
        var id = new char[32];
        for (int i = 0; i < 16; i++)
        {
            id[i * 2] = (char)('a' + (hash[i] >> 4));
            id[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }
        return new string(id);
    }

    public static bool Verify(byte[] crx)
    {
        if (crx.Length < 12 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4') return false;
        if (BitConverter.ToInt32(crx, 4) != 3) return false;
        int headerLen = BitConverter.ToInt32(crx, 8);
        if (headerLen <= 0 || 12 + headerLen > crx.Length) return false;
        var header = ReadFields(crx.AsSpan(12, headerLen));
        byte[]? proof = Field(header, 2);
        byte[]? signedHeader = Field(header, 10000);
        if (proof == null || signedHeader == null) return false;
        var proofFields = ReadFields(proof);
        byte[]? publicKey = Field(proofFields, 1);
        byte[]? signature = Field(proofFields, 2);
        if (publicKey == null || signature == null) return false;
        int zipAt = 12 + headerLen;
        byte[] toSign = new byte[4 + signedHeader.Length + (crx.Length - zipAt)];
        BitConverter.GetBytes(signedHeader.Length).CopyTo(toSign, 0);
        signedHeader.CopyTo(toSign, 4);
        crx.AsSpan(zipAt).CopyTo(toSign.AsSpan(4 + signedHeader.Length));
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(toSign, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    public static RSA LoadOrCreateKey()
    {
        string path = Path.Combine(AppConfig.Dir, "lipsync-key.pem");
        var rsa = RSA.Create();
        try
        {
            if (File.Exists(path))
            {
                rsa.ImportFromPem(File.ReadAllText(path));
                return rsa;
            }
        }
        catch { /* 密钥坏了就换一把，扩展 ID 会变，策略会重写 */ }
        rsa.Dispose();
        rsa = RSA.Create(2048);
        Directory.CreateDirectory(AppConfig.Dir);
        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        return rsa;
    }

    static readonly string[] Files = { "manifest.json", "bridge.js", "main.js", "sw.js" };

    /// <summary>清单里的 key：公钥的 SPKI，Base64。解压加载和 CRX 因此是同一个扩展 ID。</summary>
    public static string PublicKeyBase64(RSA key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>写出可在 chrome://extensions 里「加载已解压的扩展程序」的目录。</summary>
    public static string ExportUnpacked(RSA key, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (string name in Files)
        {
            if (name == "manifest.json") continue;
            File.WriteAllBytes(Path.Combine(dir, name), Resource(name));
        }
        string json = Encoding.UTF8.GetString(Resource("manifest.json")).TrimEnd();
        if (!json.EndsWith('}')) throw new InvalidDataException("扩展清单格式不对");
        string withKey = json[..^1] + ",\n  \"key\": \"" + PublicKeyBase64(key) + "\"\n}\n";
        File.WriteAllText(Path.Combine(dir, "manifest.json"), withKey);
        return dir;
    }

    static byte[] ZipExtension()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in Files) Add(zip, name);
        }
        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(Resource(name));
    }

    static byte[] Resource(string name)
    {
        var asm = typeof(LipSyncPack).Assembly;
        using var stream = asm.GetManifestResourceStream("lipsync." + name)
            ?? throw new InvalidOperationException("缺少扩展文件 " + name);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static byte[] ProtoBytes(int field, byte[] data)
    {
        var o = new List<byte>(data.Length + 8);
        WriteVarint(o, (ulong)((field << 3) | 2));
        WriteVarint(o, (ulong)data.Length);
        o.AddRange(data);
        return o.ToArray();
    }

    static void WriteVarint(List<byte> o, ulong v)
    {
        while (v >= 0x80)
        {
            o.Add((byte)(v | 0x80));
            v >>= 7;
        }
        o.Add((byte)v);
    }

    static List<(int Field, byte[] Data)> ReadFields(ReadOnlySpan<byte> msg)
    {
        var list = new List<(int, byte[])>();
        int i = 0;
        while (i < msg.Length)
        {
            ulong key = ReadVarint(msg, ref i);
            if ((key & 7) != 2) throw new InvalidDataException("CRX 头里有非长度字段");
            int len = (int)ReadVarint(msg, ref i);
            list.Add(((int)(key >> 3), msg.Slice(i, len).ToArray()));
            i += len;
        }
        return list;
    }

    static ulong ReadVarint(ReadOnlySpan<byte> msg, ref int i)
    {
        ulong v = 0;
        int shift = 0;
        while (i < msg.Length)
        {
            byte b = msg[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
            shift += 7;
        }
        throw new InvalidDataException("CRX 变长整数被截断");
    }

    static byte[]? Field(List<(int Field, byte[] Data)> fields, int number)
    {
        foreach (var f in fields)
            if (f.Field == number) return f.Data;
        return null;
    }

    static byte[] Concat(byte[] a, byte[] b)
    {
        var o = new byte[a.Length + b.Length];
        a.CopyTo(o, 0);
        b.CopyTo(o, a.Length);
        return o;
    }
}

/// <summary>只监听 127.0.0.1。扩展来问延迟，Chrome 来拉安装包。</summary>
public sealed class LipSyncServer : IDisposable
{
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly RSA _key;
    readonly bool _ownKey;
    readonly byte[] _crx;
    readonly string _extensionId;
    readonly Func<(bool Enabled, int Ms)> _query;
    public string ExtensionId => _extensionId;
    public int Port { get; }

    /// <summary>插件目录放在配置旁边，方便手动加载。每次覆盖，和当前程序里的脚本一致。</summary>
    public string ExportUnpacked()
    {
        string dir = Path.Combine(AppConfig.Dir, "lipsync-ext");
        return LipSyncPack.ExportUnpacked(_key, dir);
    }

    public LipSyncServer(Func<(bool Enabled, int Ms)> query, int port = LipSyncPack.Port, RSA? key = null)
    {
        _query = query;
        _ownKey = key == null;
        _key = key ?? LipSyncPack.LoadOrCreateKey();
        _extensionId = LipSyncPack.ExtensionId(_key);
        _crx = LipSyncPack.BuildCrx(_key);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    void Serve(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;
            try
            {
                var buf = new byte[2048];
                int n = stream.Read(buf, 0, buf.Length);
                string head = Encoding.ASCII.GetString(buf, 0, Math.Max(0, n));
                int line = head.IndexOf('\n');
                string first = line >= 0 ? head[..line] : head;
                string[] parts = first.Split(' ');
                string path = parts.Length >= 2 ? parts[1] : "/";
                int q = path.IndexOf('?');
                if (q >= 0) path = path[..q];
                byte[] body;
                string type;
                if (path == "/lipsync")
                {
                    var (on, ms) = _query();
                    body = Encoding.UTF8.GetBytes("{\"enabled\":" + (on ? "true" : "false") + ",\"ms\":" + ms + "}");
                    type = "application/json";
                }
                else if (path == "/update.xml")
                {
                    body = Encoding.UTF8.GetBytes(UpdateXml());
                    type = "text/xml";
                }
                else if (path == "/lipsync.crx")
                {
                    body = _crx;
                    type = "application/x-chrome-extension";
                }
                else
                {
                    body = Encoding.ASCII.GetBytes("not found");
                    type = "text/plain";
                    WriteResponse(stream, 404, type, body);
                    return;
                }
                WriteResponse(stream, 200, type, body);
            }
            catch { /* 对端提前断开 */ }
        }
    }

    string UpdateXml() =>
        "<?xml version='1.0' encoding='UTF-8'?>" +
        "<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>" +
        "<app appid='" + _extensionId + "'><updatecheck codebase='http://127.0.0.1:" + Port + "/lipsync.crx' version='1.0.0' /></app>" +
        "</gupdate>";

    static void WriteResponse(NetworkStream stream, int status, string type, byte[] body)
    {
        string reason = status == 200 ? "OK" : "Not Found";
        string head = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                      "Content-Type: " + type + "\r\n" +
                      "Content-Length: " + body.Length + "\r\n" +
                      "Access-Control-Allow-Origin: *\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "Connection: close\r\n\r\n";
        byte[] prefix = Encoding.ASCII.GetBytes(head);
        stream.Write(prefix);
        stream.Write(body);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
        if (_ownKey) _key.Dispose();
    }
}

/// <summary>
/// 把扩展写进当前用户的 Chrome 策略。不写 Edge。
/// 这三项都是列表策略，Windows 上只认名为 1、2、3 的值。自定义名字浏览器会忽略，所以扩展页里看不到插件。
/// </summary>
public static class LipSyncPolicy
{
    public static string ForceListValue(string extensionId, int port) =>
        extensionId + ";http://127.0.0.1:" + port + "/update.xml";

    public static string SourceValue(int port) => "http://127.0.0.1:" + port + "/*";

    public static void Install(RegistryKey browser, string extensionId, int port)
    {
        Put(browser, "ExtensionInstallForcelist", ForceListValue(extensionId, port),
            s => s.StartsWith(extensionId + ";", StringComparison.Ordinal));
        Put(browser, "ExtensionInstallSources", SourceValue(port),
            s => s == SourceValue(port));
        Put(browser, "ExtensionInstallAllowlist", extensionId,
            s => s == extensionId);
    }

    public static void Remove(RegistryKey browser, string extensionId, int port)
    {
        Drop(browser, "ExtensionInstallForcelist", s => s.StartsWith(extensionId + ";", StringComparison.Ordinal));
        Drop(browser, "ExtensionInstallSources", s => s == SourceValue(port));
        Drop(browser, "ExtensionInstallAllowlist", s => s == extensionId);
    }

    /// <summary>列表项用下一个空着的数字当名字。已有同一扩展的项就改原位，不碰别人的 1、2、3。</summary>
    static void Put(RegistryKey browser, string sub, string value, Func<string, bool> isOurs)
    {
        using var key = browser.CreateSubKey(sub);
        if (key == null) return;
        try { key.DeleteValue("hps", throwOnMissingValue: false); } catch { /* 旧版本写过这个名字，浏览器不认 */ }
        string? slot = null;
        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is string s && isOurs(s)) { slot = name; break; }
        }
        slot ??= NextSlot(key);
        key.SetValue(slot, value, RegistryValueKind.String);
    }

    static void Drop(RegistryKey browser, string sub, Func<string, bool> isOurs)
    {
        using (var key = browser.OpenSubKey(sub, writable: true))
        {
            if (key == null) return;
            try { key.DeleteValue("hps", throwOnMissingValue: false); } catch { }
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is string s && isOurs(s))
                    try { key.DeleteValue(name, throwOnMissingValue: false); } catch { }
            }
        }
        using (var key = browser.OpenSubKey(sub))
        {
            if (key != null && key.ValueCount == 0 && key.SubKeyCount == 0)
                browser.DeleteSubKey(sub, throwOnMissingSubKey: false);
        }
    }

    static string NextSlot(RegistryKey key)
    {
        var names = new HashSet<string>(key.GetValueNames());
        int n = 1;
        while (names.Contains(n.ToString())) n++;
        return n.ToString();
    }

    public static void InstallForUser(string extensionId, int port)
    {
        InstallAt(@"Software\Policies\Google\Chrome", extensionId, port);
        ClearEdge(extensionId, port);
    }

    public static void RemoveForUser(string extensionId, int port)
    {
        RemoveAt(@"Software\Policies\Google\Chrome", extensionId, port);
        ClearEdge(extensionId, port);
    }

    /// <summary>清掉以前写进 Edge 的项。Edge 键如果因此空了，就删掉，避免 Edge 继续显示由机构管理。</summary>
    static void ClearEdge(string extensionId, int port)
    {
        const string edge = @"Software\Policies\Microsoft\Edge";
        RemoveAt(edge, extensionId, port);
        using (var key = Registry.CurrentUser.OpenSubKey(edge))
        {
            if (key == null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        Registry.CurrentUser.DeleteSubKey(edge, throwOnMissingSubKey: false);
    }

    public static bool InstalledForUser(string extensionId, int port)
    {
        string expect = ForceListValue(extensionId, port);
        return HasValue(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist", expect);
    }

    public static bool AnyPolicyPresent(string extensionId) =>
        HasPrefix(@"Software\Policies\Google\Chrome\ExtensionInstallForcelist", extensionId + ";");

    static bool HasValue(string path, string expect) => Find(path, s => s == expect) != null;

    static bool HasPrefix(string path, string prefix) => Find(path, s => s.StartsWith(prefix, StringComparison.Ordinal)) != null;

    static string? Find(string path, Func<string, bool> match)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key == null) return null;
        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is string s && match(s)) return s;
        }
        return null;
    }

    static void InstallAt(string path, string extensionId, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (key != null) Install(key, extensionId, port);
    }

    static void RemoveAt(string path, string extensionId, int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key != null) Remove(key, extensionId, port);
    }
}
