using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Ap2Core;

Console.OutputEncoding = Encoding.UTF8;
string cmd = args.Length > 0 ? args[0] : "help";
switch (cmd)
{
    case "selftest": SelfTest(); break;
    case "kat": Kat(); break;
    case "resample-test": ResampleTest(); break;
    case "capture-test": CaptureTest(args); break;
    case "source-test": SourceTest(args); break;
    case "run": return await Run(args);
    case "e2e-test": return await E2E();
    case "controller-test": return await ControllerTest();
    case "fake-receiver": return await FakeRx(args);
    case "probe": return await Probe(args);
    default:
        Console.WriteLine("用法:");
        Console.WriteLine("  Ap2Cli selftest                       离线自测（不联网，不出声）");
        Console.WriteLine("  Ap2Cli resample-test                  重采样器质量测试（纯计算，不出声）");
        Console.WriteLine("  Ap2Cli capture-test [--seconds N]     回环抓取冒烟测试（只读取默认播放设备混音，不出声）");
        Console.WriteLine("  Ap2Cli source-test [--seconds N]      模拟推流线程拉取系统声音，统计欠载/积压（不联网，不出声）");
        Console.WriteLine("  Ap2Cli e2e-test                       与本机假 HomePod 做端到端测试（回环地址，不出声）");
        Console.WriteLine("  Ap2Cli controller-test                控制器测试：连接/音量/断线自动重连/断开（假 HomePod，不出声）");
        Console.WriteLine("  Ap2Cli fake-receiver [--port 7000] [--seconds N]  在回环地址上运行假 HomePod（供界面端到端测试，不出声）");
        Console.WriteLine("  Ap2Cli probe <IP> [IP...]            只读探测：读取 /info，显示名称、型号与立体声对角色（不配对、不出声）");
        Console.WriteLine("  Ap2Cli kat                            打印已知答案向量，与 node ap2/kat.js 逐行比对");
        Console.WriteLine("  Ap2Cli run --ip <IP> [--seconds N] [--sound] [--volume dB] [--freq Hz] [--verbose]");
        Console.WriteLine("                                        连接 HomePod 推流；默认静音，必须显式 --sound 才发声");
        break;
}
return 0;

static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
static string Sha(ReadOnlySpan<byte> b) => Hex(SHA256.HashData(b));
static byte[] Fill(int n, Func<int, int> f) => Enumerable.Range(0, n).Select(i => (byte)(f(i) & 0xFF)).ToArray();

static void Kat()
{
    var d = new PDict
    {
        { "a", 1 }, { "b", "x" }, { "c", new List<object> { true, 70000 } }, { "d", new byte[] { 1, 2, 3 } }, { "e", "起居室" }, { "f", 4294967295L },
        { "g", new PDict { { "h", new string('y', 20) }, { "i", -7 } } }, { "j", new PReal(1.5) },
    };
    Console.WriteLine("bplist " + Hex(Bplist.Encode(d)));

    var pcm = new short[704];
    for (int i = 0; i < 704; i++) pcm[i] = unchecked((short)(i * 37 - 5000));
    var alac = new byte[Alac.PackedSize(352)];
    Alac.Pack(pcm, 352, alac);
    Console.WriteLine("alac " + Sha(alac));

    var a = Fill(32, i => i + 1);
    var salt = Fill(16, i => 0xA0 + i);
    var B = Fill(384, i => i * 7 + 3);
    B[0] = 0x55;
    var c = new SrpClient("3939", a);
    c.Process(salt, B);
    Console.WriteLine("srp.A " + Sha(c.PublicA));
    Console.WriteLine("srp.K " + Hex(c.K));
    Console.WriteLine("srp.M1 " + Hex(c.M1));

    var ikm = Fill(64, i => 0x42 + i);
    Console.WriteLine("hkdf " + Hex(Crypto.Hkdf("Control-Salt", "Control-Write-Encryption-Key", ikm)));
    Console.WriteLine("hkdf.events " + Hex(Crypto.Hkdf("Events-Salt", "Events-Read-Encryption-Key", ikm)));

    var w = new FrameWriter(Fill(32, _ => 7));
    Console.WriteLine("frames " + Sha(w.Encrypt(Fill(1500, i => i * 3))));

    var payload = Fill(100, i => i);
    var pkt = new byte[12 + 100 + 16 + 8];
    using (var cc = new ChaCha20Poly1305(Fill(32, _ => 9)))
    {
        int n = Rtp.AudioPacket(cc, 5, true, 0x1234, 66150, 0xDEADBEEF, payload, pkt);
        Console.WriteLine("audio " + Hex(pkt.AsSpan(0, n)));
    }

    var t = Tlv.Encode((Tlv.State, new byte[] { 3 }), (Tlv.PublicKey, Fill(300, i => i)), (Tlv.Proof, Fill(5, i => i + 1)));
    Console.WriteLine("tlv " + Sha(t));

    Console.WriteLine("sync " + Hex(Rtp.SyncPacket(true, 1000, 0xE8F1A2B3C4D5E6F7UL, 67150)));
}

static void SelfTest()
{
    int pass = 0;
    void Ok(string name) { pass++; Console.WriteLine("通过: " + name); }
    void Check(bool cond, string msg) { if (!cond) throw new Exception("失败: " + msg); }

    // 1. SRP 素数：按 RFC 3526 的定义式用 π 重新推算
    {
        BigInteger ArctanInv(int x, BigInteger one)
        {
            BigInteger bx = x, x2 = bx * bx, term = one / bx, sum = term, n = 1, sign = -1;
            while (term > 0) { term /= x2; n += 2; sum += sign * (term / n); sign = -sign; }
            return sum;
        }
        BigInteger PiBits(int bits)
        {
            var one = BigInteger.One << (bits + 64);
            return (4 * (4 * ArctanInv(5, one) - ArctanInv(239, one))) >> 64;
        }
        var p = (BigInteger.One << 3072) - (BigInteger.One << 3008) - 1 + (BigInteger.One << 64) * (PiBits(2942) + 1690314);
        Check(p == SrpClient.PrimeN, "N 与 RFC 3526 第 15 组素数不一致");
        Ok("SRP 素数 N 与 RFC 3526 定义式一致");
    }

    // 2. SRP 客户端 / 自写服务端 代数一致
    {
        var N = SrpClient.PrimeN;
        BigInteger G = 5;
        byte[] ToB(BigInteger n, int pad = 0) { var b = n.ToByteArray(isUnsigned: true, isBigEndian: true); if (pad > b.Length) { var r = new byte[pad]; b.CopyTo(r, pad - b.Length); return r; } return b; }
        BigInteger FromB(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
        byte[] H(params byte[][] parts) { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512); foreach (var q in parts) h.AppendData(q); return h.GetHashAndReset(); }

        var salt = RandomNumberGenerator.GetBytes(16);
        var x = FromB(H(salt, H(Encoding.UTF8.GetBytes("Pair-Setup:3939"))));
        var v = BigInteger.ModPow(G, x, N);
        var k = FromB(H(ToB(N, 384), ToB(G, 384)));
        var b = FromB(RandomNumberGenerator.GetBytes(32));
        var Bv = (k * v + BigInteger.ModPow(G, b, N)) % N;
        var cl = new SrpClient("3939");
        Check(cl.Process(salt, ToB(Bv)), "Process 返回 false");
        var A = FromB(cl.PublicA);
        var u = FromB(H(ToB(A, 384), ToB(Bv, 384)));
        var S = BigInteger.ModPow(A * BigInteger.ModPow(v, u, N) % N, b, N);
        var K = H(ToB(S));
        Check(K.AsSpan().SequenceEqual(cl.K), "会话密钥不一致");
        var hN = H(ToB(N)); var hg = H(ToB(G));
        var hx = new byte[64]; for (int i = 0; i < 64; i++) hx[i] = (byte)(hN[i] ^ hg[i]);
        var m1 = H(hx, H(Encoding.UTF8.GetBytes("Pair-Setup")), salt, ToB(A), ToB(Bv), K);
        Check(m1.AsSpan().SequenceEqual(cl.M1), "M1 不一致");
        Check(cl.VerifyServerProof(H(ToB(A), m1, K)), "M2 校验失败");
        Check(!cl.VerifyServerProof(new byte[64]), "错误的 M2 不应通过");
        Ok("SRP 客户端与独立服务端算出相同 K / M1 / M2");
    }

    // 3. TLV8 往返
    {
        var big = RandomNumberGenerator.GetBytes(384);
        var enc = Tlv.Encode((Tlv.State, new byte[] { 3 }), (Tlv.PublicKey, big), (Tlv.Proof, RandomNumberGenerator.GetBytes(64)));
        var m = Tlv.Decode(enc);
        Check(m[Tlv.PublicKey].AsSpan().SequenceEqual(big) && m[Tlv.State][0] == 3, "TLV 往返不一致");
        Ok("TLV8 编解码（含分片拼接）");
    }

    // 4. HAP 分帧：多帧、碎片到达、篡改检测
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var w = new FrameWriter(key); var r = new FrameReader(key);
        var data = RandomNumberGenerator.GetBytes(3000);
        var wire = w.Encrypt(data);
        Check(wire.Length == 3000 + 3 * 18, "密文长度应为 3000+3*18");
        var ms = new MemoryStream();
        for (int i = 0; i < wire.Length; i += 7) ms.Write(r.Push(wire.AsSpan(i, Math.Min(7, wire.Length - i))));
        Check(ms.ToArray().AsSpan().SequenceEqual(data), "分帧往返不一致");
        var f = new FrameWriter(key).Encrypt(Encoding.ASCII.GetBytes("hello")); f[3] ^= 1;
        bool threw = false;
        try { new FrameReader(key).Push(f); } catch (CryptographicException) { threw = true; }
        Check(threw, "篡改应被检测");
        Ok("ChaCha20-Poly1305 分帧（3 帧、7 字节碎片到达、篡改检测）");
    }

    // 5. bplist 往返
    {
        var obj = new PDict
        {
            { "deviceID", "02:AA:BB:CC:DD:EE" }, { "timingPort", 54321 }, { "big", 4294967295L }, { "neg", -5 }, { "flag", true }, { "no", false },
            { "nested", new PDict { { "list", new List<object> { 1, "two", new byte[] { 97, 98, 99 }, new PDict { { "z", 0 } } } }, { "s", new string('x', 40) } } },
            { "streams", new List<object> { new PDict { { "shk", RandomNumberGenerator.GetBytes(32) }, { "type", 96 }, { "r", new PReal(1.5) } } } },
            { "uni", "起居室" },
        };
        var back = (PDict)Bplist.Decode(Bplist.Encode(obj))!;
        Check(back.GetInt("timingPort") == 54321 && back.GetInt("big") == 4294967295L && back.GetInt("neg") == -5, "整数");
        Check((bool)back.Get("flag")! && !(bool)back.Get("no")!, "布尔");
        var nested = (PDict)back.Get("nested")!;
        Check((string)nested.Get("s")! == new string('x', 40), "长字符串");
        var list = (List<object>)nested.Get("list")!;
        Check(((byte[])list[2]).AsSpan().SequenceEqual(new byte[] { 97, 98, 99 }), "二进制数据");
        var st = (PDict)((List<object>)back.Get("streams")!)[0];
        Check((double)st.Get("r")! == 1.5, "实数");
        Check(back.GetStr("uni") == "起居室", "中文");
        Ok("bplist 编解码往返（整数/负数/实数/长字符串/数据/中文）");
    }

    // 6. ALAC 未压缩帧：按位解析还原
    {
        var pcm = new short[704];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = unchecked((short)((i * 2654435761L >> 16) - 32768));
        var outb = new byte[Alac.PackedSize(352)];
        int len = Alac.Pack(pcm, 352, outb);
        Check(len == 1412 && outb.Length == 1412, "ALAC 长度应为 1412");
        int bitPos = 0;
        int Get(int n) { int v = 0; for (int i = 0; i < n; i++) { v = (v << 1) | ((outb[bitPos >> 3] >> (7 - (bitPos & 7))) & 1); bitPos++; } return v; }
        Check(Get(3) == 1 && Get(4) == 0 && Get(12) == 0 && Get(1) == 0 && Get(2) == 0 && Get(1) == 1, "ALAC 头部位");
        for (int i = 0; i < pcm.Length; i++) Check((short)Get(16) == pcm[i], "样本 " + i);
        Check(Get(3) == 7, "结束标签");
        Ok("未压缩 ALAC 帧（1412 字节，位布局可还原）");
    }

    // 7. NTP / RTP 换算
    {
        ulong ntp = NtpClock.Now();
        ulong back = NtpClock.TsToNtp(NtpClock.NtpToTs(ntp, 44100), 44100);
        ulong diff = ntp > back ? ntp - back : back - ntp;
        Check(diff < (1UL << 32) / 44100 * 2, "NTP<->采样换算误差过大");
        var sp = Rtp.SyncPacket(true, 1000, ntp, 67150);
        Check(sp.Length == 20 && sp[0] == 0x90 && sp[1] == 0xD4, "同步包格式");
        Ok("NTP/RTP 时间换算与同步包格式");
    }

    // 8. 音频包封装可被对端按约定解密
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = RandomNumberGenerator.GetBytes(100);
        var pkt = new byte[12 + 100 + 16 + 8];
        using var cc = new ChaCha20Poly1305(key);
        int n = Rtp.AudioPacket(cc, 5, true, 0x1234, 66150, 0xDEADBEEF, payload, pkt);
        Check(n == pkt.Length && pkt[1] == 0xE0, "音频包长度/标记位");
        var n12 = new byte[12]; pkt.AsSpan(n - 8, 8).CopyTo(n12.AsSpan(4));
        var dec = new byte[100];
        cc.Decrypt(n12, pkt.AsSpan(12, 100), pkt.AsSpan(112, 16), dec, pkt.AsSpan(4, 8));
        Check(dec.AsSpan().SequenceEqual(payload), "音频包解密不一致");
        Ok("音频包：RTP 头 + 密文 + 标签 + 8 字节随机数，可解密还原");
    }

    // 立体声对裁决：配成对时给出组长 / 成员；没有配成对（例如两台都自称组长）时保留各自的报告并标为未成对
    {
        const string T = "60854346-1693-5801-9B42-0D42507F17F5";
        Ap2Core.FoundDevice Dev(string ip, bool igl, bool pgid, string pair = T) =>
            new(ip, ip, "AudioAccessory5,1", "", igl && !pgid ? "组长" : "成员", pair) { ClaimsLeader = igl, HasParentGroup = pgid };
        Ap2Core.FoundDevice At(IEnumerable<Ap2Core.FoundDevice> r, string ip) => r.First(f => f.Ip == ip);
        var a = "192.168.50.71"; var b = "192.168.50.23";

        var ok1 = Ap2Core.Discovery.ResolvePairs(new[] { Dev(a, true, false), Dev(b, false, true) });
        Check(At(ok1, a).Role == "组长" && At(ok1, b).Role == "成员" && !At(ok1, a).Unpaired && !At(ok1, b).Unpaired, "正常的一对：组长 / 成员");
        var ok2 = Ap2Core.Discovery.ResolvePairs(new[] { Dev(b, false, true), Dev(a, true, false) });
        Check(At(ok2, a).Role == "组长" && At(ok2, b).Role == "成员", "结果与顺序无关");
        var both = Ap2Core.Discovery.ResolvePairs(new[] { Dev(a, true, false), Dev(b, true, false) });
        Check(At(both, a).Role == "组长" && At(both, b).Role == "组长" && At(both, a).Unpaired && At(both, b).Unpaired, "两台都自称组长：如实显示组长，并标为未成对");
        var mixed = Ap2Core.Discovery.ResolvePairs(new[] { Dev(a, true, false), Dev(b, true, true) });
        Check(At(mixed, a).Unpaired && At(mixed, b).Unpaired, "自称组长的一台带 pgid：未成对");
        var none = Ap2Core.Discovery.ResolvePairs(new[] { Dev(a, false, true), Dev(b, false, true) });
        Check(At(none, a).Unpaired && At(none, b).Unpaired && At(none, a).Role == "成员", "都不自称组长：未成对，角色保持各自的报告");
        var solo = Ap2Core.Discovery.ResolvePairs(new[] { Dev(a, true, false) });
        Check(At(solo, a).Role == "组长" && !At(solo, a).Unpaired, "只探测到一台：保持它自己的判断");
        var other = Ap2Core.Discovery.ResolvePairs(new[] { Dev("192.168.50.9", true, false, "") });
        Check(other.Single().Role == "组长" && !other.Single().Unpaired, "非立体声对设备不受影响");
        Ok("立体声对裁决：成对给出组长 / 成员；两台都自称组长时如实显示并标为未成对");
    }

    // 连接目标：成对时选成员改连组长；两台都自称组长时沿用上次确认的组长，绝不直连成员
    {
        const string T = "60854346-1693-5801-9B42-0D42507F17F5";
        var a = "192.168.50.71"; var b = "192.168.50.23";
        List<DeviceEntry> Devices(string roleA, bool unpairedA, string roleB, bool unpairedB) => new()
        {
            new DeviceEntry { Ip = a, PairId = T, Role = roleA, Unpaired = unpairedA },
            new DeviceEntry { Ip = b, PairId = T, Role = roleB, Unpaired = unpairedB },
        };
        var leaders = new Dictionary<string, string> { [T] = a };

        var paired = Devices("组长", false, "成员", false);
        var t1 = Discovery.ChooseTarget(b, paired, leaders);
        Check(t1.Ip == a && t1.Reason == "member", "成对时选成员应改连组长");
        var t2 = Discovery.ChooseTarget(b, paired, new Dictionary<string, string>());
        Check(t2.Ip == a && t2.Reason == "member", "成对时选成员应改连组长（即使没有记忆）");

        var broken = Devices("组长", true, "组长", true);
        var t3 = Discovery.ChooseTarget(b, broken, leaders);
        Check(t3.Ip == b && t3.Reason == "", "两台都自称组长时按所选设备连接（可以切到另一台）");
        var t4 = Discovery.ChooseTarget(a, broken, leaders);
        Check(t4.Ip == a && t4.Reason == "", "两台都自称组长时选组长：保持所选");
        var t7 = Discovery.ChooseTarget(b, broken, new Dictionary<string, string> { [T + "-old"] = a });
        Check(t7.Ip == b, "记忆属于别的配对时不影响选择");

        var single = new List<DeviceEntry> { new() { Ip = b, PairId = "", Role = "组长" } };
        var t6 = Discovery.ChooseTarget(b, single, leaders);
        Check(t6.Ip == b && t6.Reason == "", "非立体声对设备不受影响");
        Ok("连接目标：成对时成员改连组长；两台都自称组长时按所选设备连接");
    }

    // 推流目标：成对只连组长；两台都未成对时两路一起推
    {
        const string T = "60854346-1693-5801-9B42-0D42507F17F5";
        var a = "192.168.50.71"; var b = "192.168.50.23";
        var paired = new List<DeviceEntry>
        {
            new() { Ip = a, PairId = T, Role = "组长" },
            new() { Ip = b, PairId = T, Role = "成员" },
        };
        var both = Discovery.PlaybackTargets(b, paired);
        Check(both.Count == 1 && both[0] == a, "成对时无论选谁都只连组长");
        var broken = new List<DeviceEntry>
        {
            new() { Ip = a, PairId = T, Role = "组长", Unpaired = true },
            new() { Ip = b, PairId = T, Role = "组长", Unpaired = true },
        };
        var two = Discovery.PlaybackTargets(b, broken);
        Check(two.Count == 2 && two[0] == b && two[1] == a, "两台都未成对时同时连接两台，所选的排在前面");
        var solo = Discovery.PlaybackTargets(a, new List<DeviceEntry> { new() { Ip = a, Role = "组长" } });
        Check(solo.Count == 1 && solo[0] == a, "单独一台不受影响");
        Ok("推流目标：成对只连组长；未成对的两台各开一路");
    }

    // 同一段采样分给两路，不能你读一块我读下一块
    {
        var src = new CountSource();
        var bus = new Ap2Core.Audio.PcmBus(src, 4);
        var x = new Ap2Core.Audio.BusSource(bus);
        var y = new Ap2Core.Audio.BusSource(bus);
        var bx = new short[8];
        var by = new short[8];
        x.Read(bx, 4);
        x.Read(bx, 4);   // 这一路先走两块
        y.Read(by, 4);   // 另一路仍应拿到第一块
        Check(by[0] == 0 && by[7] == 7, "落后的一路补到同一块采样，首样本 " + by[0]);
        y.Read(by, 4);
        Check(by[0] == 8 && by[7] == 15, "下一块也一致");
        Ok("两路推流共用采样，不会把缓冲拆开");
    }

    // 画面对齐：扩展包能被验签，本地端口能回答延迟，策略只写测试键
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        byte[] crx = LipSyncPack.BuildCrx(key);
        string id = LipSyncPack.ExtensionId(key);
        Check(id.Length == 32 && id.All(c => c >= 'a' && c <= 'p'), "扩展 ID 应为 32 位 a–p");
        Check(LipSyncPack.Verify(crx), "CRX 验签失败");
        int headerLen = BitConverter.ToInt32(crx, 8);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(crx, 12 + headerLen, crx.Length - 12 - headerLen, writable: false)))
            Check(zip.GetEntry("sw.js") != null && zip.GetEntry("bridge.js") != null && zip.GetEntry("main.js") != null, "扩展包应带三个脚本");
        string unpacked = Path.Combine(Path.GetTempPath(), "hps-lip-unpacked-" + Guid.NewGuid().ToString("N"));
        try
        {
            LipSyncPack.ExportUnpacked(key, unpacked);
            string manifest = File.ReadAllText(Path.Combine(unpacked, "manifest.json"));
            Check(manifest.Contains(LipSyncPack.PublicKeyBase64(key)), "解压目录的清单应带同一把公钥");
            Check(File.ReadAllText(Path.Combine(unpacked, "sw.js")).Contains("hps-lip-ask"), "解压目录应有后台脚本");
        }
        finally
        {
            try { Directory.Delete(unpacked, true); } catch { /* 临时目录删不掉不影响结论 */ }
        }
        Check(LipSyncPolicy.ForceListValue(id, 17321) == id + ";http://127.0.0.1:17321/update.xml", "策略串应带扩展 ID 和更新地址");
        using var server = new LipSyncServer(() => (true, 500), 0, key);
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string body = http.GetStringAsync("http://127.0.0.1:" + server.Port + "/lipsync").GetAwaiter().GetResult();
        Check(body.Contains("\"enabled\":true") && body.Contains("\"ms\":500"), "延迟接口应返回打开和 500，实际 " + body);
        string xml = http.GetStringAsync("http://127.0.0.1:" + server.Port + "/update.xml").GetAwaiter().GetResult();
        Check(xml.Contains(id) && xml.Contains("/lipsync.crx"), "更新清单应带扩展 ID");
        byte[] got = http.GetByteArrayAsync("http://127.0.0.1:" + server.Port + "/lipsync.crx").GetAwaiter().GetResult();
        Check(got.AsSpan().SequenceEqual(crx), "下载的扩展包应与签名的那份一致");
        const string testKey = @"Software\HomePodSender\LipSyncTest";
        try
        {
            using var hive = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKey)!;
            using (var preset = hive.CreateSubKey("ExtensionInstallForcelist"))
                preset?.SetValue("1", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;https://example.invalid/update.xml");
            LipSyncPolicy.Install(hive, id, server.Port);
            using (var list = hive.OpenSubKey("ExtensionInstallForcelist"))
            {
                Check(list?.GetValue("1") as string == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;https://example.invalid/update.xml", "已有的列表项应留在 1");
                Check(list?.GetValue("2") as string == LipSyncPolicy.ForceListValue(id, server.Port), "自己的强制安装应写在下一个数字");
                Check(list?.GetValue("hps") == null, "浏览器不认名为 hps 的列表项");
            }
            LipSyncPolicy.Remove(hive, id, server.Port);
            using (var list = hive.OpenSubKey("ExtensionInstallForcelist"))
                Check(list?.GetValue("1") as string == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;https://example.invalid/update.xml" && list?.GetValue("2") == null, "只删自己的列表项");
            using (var preset = hive.OpenSubKey("ExtensionInstallForcelist", writable: true))
                preset?.DeleteValue("1");
            LipSyncPolicy.Install(hive, id, server.Port);
            LipSyncPolicy.Remove(hive, id, server.Port);
            using var left = hive.OpenSubKey("ExtensionInstallForcelist");
            Check(left == null, "没有其他项时子键应消失");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
        }
        Ok("画面对齐：扩展包、本地端口、策略写入");
    }

    Console.WriteLine("\n全部 " + pass + " 项自测通过");
}

static async Task<int> Run(string[] args)
{
    string Arg(string name, string def) { int i = Array.IndexOf(args, "--" + name); return i < 0 ? def : (i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "true"); }
    var o = new Ap2Options
    {
        Ip = Arg("ip", "192.168.50.71"),
        Dict = Arg("dict", "rich"),
        Record = Arg("record", "before"),
        AdvertiseDataPort = Arg("dataport", "false") == "true",
        Verbose = Arg("verbose", "false") == "true",
    };
    double seconds = double.Parse(Arg("seconds", "6"));
    bool sound = Arg("sound", "false") == "true";   // 安全默认：不加 --sound 一律推静音
    double freq = double.Parse(Arg("freq", "440"));
    string vol = Arg("volume", "");

    using var s = new Ap2Session(o);
    s.LogMessage += Console.WriteLine;
    try
    {
        await s.ConnectAsync();
        if (vol != "") await s.SetVolumeDbAsync(double.Parse(vol));
        s.StartStreaming(sound ? new ToneSource(freq) : new SilenceSource());
        Console.WriteLine("推流 " + seconds + " 秒（" + (sound ? "测试音" : "静音") + "）");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"统计: 音频包 {s.AudioPackets} 同步包 {s.SyncPackets} 授时应答 {s.TimingReplies} 重传请求 {s.RetransRequests}(已补发 {s.RetransSent})");
        await s.StopAsync();
        return 0;
    }
    catch (Exception e)
    {
        Console.WriteLine("失败: " + e.Message);
        try { await s.StopAsync(); } catch { }
        return 1;
    }
}

static void ResampleTest()
{
    // 返回 (输出/输入 幅度比 dB, 信噪比 dB)
    (double gainDb, double snrDb) Measure(double inFreq, double outFreq, int inRate = 48000, double rateAdj = 0)
    {
        var rs = new Ap2Core.Audio.StreamResampler(inRate, 44100);
        int total = inRate * 3;
        var input = new float[total * 2];
        for (int i = 0; i < total; i++) { float v = (float)(0.5 * Math.Sin(2 * Math.PI * inFreq * i / inRate)); input[i * 2] = v; input[i * 2 + 1] = v; }
        var outList = new List<float>();
        var tmp = new float[352 * 2];
        int fed = 0, chunk = inRate / 100;
        while (fed < total)
        {
            int n = Math.Min(chunk, total - fed);
            rs.Push(input.AsSpan(fed * 2, n * 2), n);
            fed += n;
            for (;;) { int got = rs.Pull(tmp, 352, rateAdj); for (int i = 0; i < got; i++) outList.Add(tmp[i * 2]); if (got < 352) break; }
        }
        int skip = 44100 / 5, N = outList.Count - skip - 2000;
        double w = 2 * Math.PI * outFreq / 44100, a = 0, b = 0;
        for (int i = 0; i < N; i++) { double y = outList[skip + i]; a += y * Math.Cos(w * (skip + i)); b += y * Math.Sin(w * (skip + i)); }
        a *= 2.0 / N; b *= 2.0 / N;
        double amp = Math.Sqrt(a * a + b * b), sig = 0, res = 0;
        for (int i = 0; i < N; i++) { double y = outList[skip + i]; double f = a * Math.Cos(w * (skip + i)) + b * Math.Sin(w * (skip + i)); sig += f * f; res += (y - f) * (y - f); }
        return (20 * Math.Log10(amp / 0.5), 10 * Math.Log10(sig / Math.Max(res, 1e-30)));
    }
    foreach (double f in new[] { 100.0, 1000, 5000, 10000, 15000, 18000, 20000 })
    {
        var (g, snr) = Measure(f, f);
        Console.WriteLine($"48k→44.1k  {f,6:0} Hz: 增益 {g,7:0.000} dB  信噪比 {snr,6:0.0} dB");
    }
    // 混叠抑制：23kHz 输入会折叠到 44100-23000=21100Hz
    {
        var (g, _) = Measure(23000, 21100);
        Console.WriteLine($"48k→44.1k  23000 Hz 输入（折叠到 21100 Hz）: 相对衰减 {g,7:0.0} dB");
    }
    // 44.1k→44.1k（设备已是 44.1kHz）与 96k→44.1k
    { var (g, snr) = Measure(1000, 1000, 44100); Console.WriteLine($"44.1k→44.1k   1000 Hz: 增益 {g,7:0.000} dB  信噪比 {snr,6:0.0} dB"); }
    { var (g, snr) = Measure(1000, 1000, 96000); Console.WriteLine($"96k→44.1k    1000 Hz: 增益 {g,7:0.000} dB  信噪比 {snr,6:0.0} dB"); }
    // 速率精度：消耗 10 秒输入应产出 441000 帧（±2）
    {
        var rs = new Ap2Core.Audio.StreamResampler(48000, 44100);
        var buf = new float[960 * 2]; var tmp = new float[352 * 2]; long outFrames = 0;
        for (int s = 0; s < 1000; s++) { rs.Push(buf, 480); for (;;) { int g = rs.Pull(tmp, 352); outFrames += g; if (g < 352) break; } }
        Console.WriteLine($"速率精度：输入 480000 帧 → 输出 {outFrames} 帧（理想 441000，缓冲内残留约 {rs.BufferedFrames} 帧）");
    }
}

static void CaptureTest(string[] args)
{
    int i = Array.IndexOf(args, "--seconds");
    double seconds = i >= 0 ? double.Parse(args[i + 1]) : 3;
    using var cap = new Ap2Core.Audio.LoopbackCapture(Console.WriteLine);
    float peak = 0; long chunks = 0;
    cap.DataAvailable += (buf, frames) => { chunks++; for (int k = 0; k < frames * 2; k++) peak = Math.Max(peak, Math.Abs(buf[k])); };
    cap.Stopped += why => Console.WriteLine("抓取停止: " + why);
    cap.Start();
    Console.WriteLine("设备: " + cap.DeviceId);
    Console.WriteLine("开始抓取 " + seconds + " 秒（本程序不发声；只在系统正在播放时才会有数据）…");
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    Console.WriteLine($"收到数据块 {chunks} 个，共 {cap.FramesCaptured} 帧，峰值 {peak:0.0000}");
}

static void SourceTest(string[] args)
{
    int i = Array.IndexOf(args, "--seconds");
    double seconds = i >= 0 ? double.Parse(args[i + 1]) : 20;
    using var src = new Ap2Core.Audio.LoopbackSource(Console.WriteLine);
    var pcm = new short[352 * 2];
    var sw = System.Diagnostics.Stopwatch.StartNew();
    long frames = 0, nonSilent = 0; double lastReport = 0; int peak = 0;
    Console.WriteLine("模拟推流线程拉取 " + seconds + " 秒（每 352 帧一次）…");
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        long target = sw.ElapsedTicks * 44100 / System.Diagnostics.Stopwatch.Frequency;
        while (frames + 352 <= target)
        {
            src.Read(pcm, 352);
            frames += 352;
            bool any = false;
            foreach (short v in pcm) { int av = Math.Abs((int)v); if (av > peak) peak = av; if (av > 0) any = true; }
            if (any) nonSilent++;
        }
        if (sw.Elapsed.TotalSeconds - lastReport >= 2)
        {
            lastReport = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  {lastReport,5:0.0}s 缓冲 {src.BufferedMs,5:0.0} ms  欠载 {src.Underruns}  积压丢弃 {src.Overruns}  峰值 {peak / 32768.0:0.000}");
        }
        Thread.Sleep(1);
    }
    Console.WriteLine($"共输出 {frames} 帧，其中有声的块 {nonSilent}/{frames / 352}，欠载 {src.Underruns} 次，积压丢弃 {src.Overruns} 次");
}

static async Task<int> E2E()
{
    int fails = 0;
    void Check(bool ok, string what, string detail = "") { Console.WriteLine((ok ? "  通过  " : "  失败  ") + what + (detail != "" ? "  (" + detail + ")" : "")); if (!ok) fails++; }

    using var rx = new FakeReceiver(_ => { });
    var o = new Ap2Options { Ip = "127.0.0.1", Port = rx.Port };
    using var s = new Ap2Session(o);
    var logs = new List<string>();
    s.LogMessage += m => { lock (logs) logs.Add(m); };
    const double Seconds = 8;
    try
    {
        await s.ConnectAsync();
        await s.SetVolumeDbAsync(-20);
        s.StartStreaming(new ToneSource(1000, 0.3));
        await Task.Delay(TimeSpan.FromSeconds(Seconds));
    }
    catch (Exception e) { Console.WriteLine("握手/推流异常: " + e); fails++; }
    long sentPackets = s.AudioPackets, sentSync = s.SyncPackets;
    await s.StopAsync();
    await Task.Delay(500);

    Console.WriteLine("--- 握手 ---");
    Check(rx.PairOk, "瞬时配对（服务端校验客户端证明）");
    Check(rx.SessionSetup, "会话 SETUP");
    Check(rx.Recorded, "RECORD");
    Check(rx.StreamSetup, "流 SETUP");
    Check(rx.EventReplies == 1, "事件通道：客户端回复了接收端推送的请求", "回复数 " + rx.EventReplies);
    Check(rx.LastVolumeDb.HasValue && Math.Abs(rx.LastVolumeDb.Value + 20) < 1e-6, "音量 SET_PARAMETER", "收到 " + rx.LastVolumeDb);
    Check(rx.FeedbackCount >= 2, "心跳 /feedback", "次数 " + rx.FeedbackCount);
    Check(rx.TornDown, "TEARDOWN");

    Console.WriteLine("--- 音频 ---");
    double expectPackets = Seconds * 44100 / 352;
    Check(Math.Abs(sentPackets - expectPackets) <= expectPackets * 0.02, "推流速率", $"发送 {sentPackets} 包，理想 {expectPackets:0.0}");
    Check(rx.DecryptFailures == 0, "全部音频包可解密", "失败 " + rx.DecryptFailures);
    Check(rx.OrderErrors == 0 && rx.RtpStepErrors == 0, "序号连续、RTP 时间戳步长 352", $"乱序 {rx.OrderErrors} 步长错误 {rx.RtpStepErrors}");
    int drops = (int)(sentPackets / 50);
    Check(rx.RetransRecovered >= drops - 2 && rx.RetransRecovered > 0, "丢包重传：补发的包被接收端收下", $"补回 {rx.RetransRecovered}，模拟丢包约 {drops}");
    Check(rx.Pcm.Count >= sentPackets - 3, "补回后音频包基本完整", $"收到 {rx.Pcm.Count} / 发送 {sentPackets}");

    // 逐样本比对解码结果与期望的音源输出
    var expect = new ToneSource(1000, 0.3);
    var tmp = new short[352 * 2];
    int compared = 0, bad = 0;
    for (int i = 0; i < rx.Pcm.Count && rx.FirstSeq >= 0; i++)
    {
        expect.Read(tmp, 352);
        if (!rx.Pcm.TryGetValue((ushort)(rx.FirstSeq + i), out var got)) continue;
        compared++;
        if (!got.AsSpan().SequenceEqual(tmp)) bad++;
    }
    Check(compared > 100 && bad == 0, "解码出的 PCM 与发送的采样逐样本一致", $"比对 {compared} 包，不一致 {bad}");

    Console.WriteLine("--- 授时与同步 ---");
    Check(rx.TimingReplies >= 8, "NTP 授时应答", "收到 " + rx.TimingReplies);
    Check(rx.TimingReferenceOk, "授时应答的参考时间等于请求的发送时间");
    Check(rx.MaxTimingEpochErrorSec < 0.5, "授时应答的 NTP 时间与系统时间一致（纪元/小数换算正确）", $"最大偏差 {rx.MaxTimingEpochErrorSec * 1000:0.0} ms");
    List<(uint rtpMinusLatency, double ntpSec, uint rtpNow)> syncs;
    lock (rx.Syncs) syncs = rx.Syncs.ToList();
    Check(syncs.Count >= (int)Seconds - 1, "同步包每秒一个", "收到 " + syncs.Count + "，发送 " + sentSync);
    if (syncs.Count >= 3)
    {
        Check(syncs.All(x => x.rtpNow - x.rtpMinusLatency == 66150), "同步包：rtpNow - (rtpNow-latency) = 66150");
        double maxDev = 0; long maxRtpDev = 0;
        for (int k = 1; k < syncs.Count; k++)
        {
            maxDev = Math.Max(maxDev, Math.Abs((syncs[k].ntpSec - syncs[0].ntpSec) - k));
            maxRtpDev = Math.Max(maxRtpDev, Math.Abs((long)(syncs[k].rtpNow - syncs[0].rtpNow) - 44100L * k));
        }
        Check(maxDev < 0.05, "同步包 NTP 时间随墙钟均匀前进", $"最大偏差 {maxDev * 1000:0.0} ms");
        Check(maxRtpDev <= 700, "同步包 RTP 时间戳随采样前进（44100/秒）", $"最大偏差 {maxRtpDev} 帧");
        Check(rx.MaxSyncEpochErrorSec < 0.5, "同步包 NTP 时间与系统时间一致", $"最大偏差 {rx.MaxSyncEpochErrorSec * 1000:0.0} ms");
    }

    Console.WriteLine(fails == 0 ? "\n端到端测试全部通过" : "\n端到端测试有 " + fails + " 项失败");
    if (fails != 0) { lock (logs) foreach (var l in logs) Console.WriteLine("  日志: " + l); }
    return fails == 0 ? 0 : 1;
}

static async Task<int> ControllerTest()
{
    int fails = 0;
    void Check(bool ok, string what, string detail = "") { Console.WriteLine((ok ? "  通过  " : "  失败  ") + what + (detail != "" ? "  (" + detail + ")" : "")); if (!ok) fails++; }
    async Task<bool> WaitFor(Func<bool> cond, int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); } return cond(); }

    using var rx = new FakeReceiver(_ => { }) { DropEvery = 0 };
    var settings = new ControllerSettings { Ip = "127.0.0.1", Port = rx.Port, VolumePercent = 20, LatencyMs = 1000, AutoReconnect = true };
    using var c = new HomePodController(settings) { SourceFactory = _ => new ToneSource(440, 0.2) };
    var states = new List<StreamState>();
    c.Changed += () => { lock (states) states.Add(c.State); };
    c.LogMessage += m => Console.WriteLine("    · " + m);

    Console.WriteLine("--- 连接 ---");
    await c.ConnectAsync();
    Check(c.State == StreamState.Streaming, "连接后进入推流状态", c.Status);
    Check(rx.LastVolumeDb.HasValue && Math.Abs(rx.LastVolumeDb.Value - Ap2Session.PercentToDb(20)) < 1e-6, "连接时先设置保存的音量(20%)", "收到 " + rx.LastVolumeDb + " dB");

    Console.WriteLine("--- 音量（防抖） ---");
    for (int p = 30; p <= 60; p += 5) { c.SetVolumePercent(p); await Task.Delay(20); }
    Check(await WaitFor(() => rx.LastVolumeDb.HasValue && Math.Abs(rx.LastVolumeDb.Value - Ap2Session.PercentToDb(60)) < 1e-6, 2000), "连续拖动后最终音量=60%", "收到 " + rx.LastVolumeDb + " dB");
    c.SetVolumePercent(0);
    Check(await WaitFor(() => rx.LastVolumeDb == -144.0, 2000), "0% = 静音(-144 dB)", "收到 " + rx.LastVolumeDb);

    Console.WriteLine("--- 断线自动重连 ---");
    int before = rx.Connections;
    rx.DropClient();
    Check(await WaitFor(() => c.State == StreamState.Reconnecting, 5000), "对端断开后进入重连状态", c.Status);
    Check(await WaitFor(() => c.State == StreamState.Streaming && rx.Connections == before + 1, 15000), "自动重连成功并恢复推流", c.Status + "，连接数 " + rx.Connections);
    Check(rx.LastVolumeDb == -144.0, "重连后沿用当前音量", "收到 " + rx.LastVolumeDb);

    Console.WriteLine("--- 断开 ---");
    rx.TornDown = false;
    await c.DisconnectAsync();
    Check(c.State == StreamState.Disconnected, "主动断开后为未连接", c.Status);
    Check(await WaitFor(() => rx.TornDown, 2000), "接收端收到 TEARDOWN");
    await Task.Delay(3000);
    Check(c.State == StreamState.Disconnected && rx.Connections == before + 1, "主动断开后不会再自动重连", "连接数 " + rx.Connections);

    Console.WriteLine("--- 连接失败 ---");
    var bad = new ControllerSettings { Ip = "127.0.0.1", Port = 1, AutoReconnect = true };
    using var c2 = new HomePodController(bad) { SourceFactory = _ => new SilenceSource() };
    bool threw = false;
    try { await c2.ConnectAsync(); } catch { threw = true; }
    Check(threw && c2.State == StreamState.Disconnected && c2.Status.StartsWith("连接失败"), "端口不通时抛出异常并回到未连接", c2.Status);

    Console.WriteLine("--- 两台同时 ---");
    using var rxA = new FakeReceiver(_ => { });
    using var rxB = new FakeReceiver(_ => { }, bind: IPAddress.Parse("127.0.0.2"));
    var dual = new ControllerSettings
    {
        Ip = "127.0.0.1",
        Ips = new List<string> { "127.0.0.1", "127.0.0.2" },
        Ports = new Dictionary<string, int> { ["127.0.0.1"] = rxA.Port, ["127.0.0.2"] = rxB.Port },
        VolumePercent = 40, LatencyMs = 1000, AutoReconnect = false,
    };
    using var cd = new HomePodController(dual) { SourceFactory = _ => new ToneSource(440, 0.2) };
    cd.LogMessage += m => Console.WriteLine("    · " + m);
    await cd.ConnectAsync();
    Check(cd.State == StreamState.Streaming, "两台都连上后进入推流", cd.Status);
    Check(cd.Status.Contains("2 台"), "状态写明两台", cd.Status);
    Check(await WaitFor(() => rxA.AudioPackets > 5 && rxB.AudioPackets > 5, 4000), "两台都收到音频", $"A {rxA.AudioPackets}  B {rxB.AudioPackets}");
    double expect = Ap2Session.PercentToDb(40);
    Check(rxA.LastVolumeDb.HasValue && Math.Abs(rxA.LastVolumeDb.Value - expect) < 1e-6 && rxB.LastVolumeDb.HasValue && Math.Abs(rxB.LastVolumeDb.Value - expect) < 1e-6, "两台音量一致", $"{rxA.LastVolumeDb} / {rxB.LastVolumeDb}");
    cd.SetVolumePercent(15);
    Check(await WaitFor(() => rxA.LastVolumeDb == Ap2Session.PercentToDb(15) && rxB.LastVolumeDb == Ap2Session.PercentToDb(15), 2000), "音量同时作用在两台");
    rxA.TornDown = rxB.TornDown = false;
    await cd.DisconnectAsync();
    Check(cd.State == StreamState.Disconnected, "两台一起断开");
    Check(await WaitFor(() => rxA.TornDown && rxB.TornDown, 2000), "两台都收到 TEARDOWN");

    Console.WriteLine("--- 一台失败、另一台继续 ---");
    using var rxOk = new FakeReceiver(_ => { });
    var partial = new ControllerSettings
    {
        Ips = new List<string> { "127.0.0.1", "127.0.0.9" },
        Ports = new Dictionary<string, int> { ["127.0.0.1"] = rxOk.Port, ["127.0.0.9"] = 1 },
        VolumePercent = 20, LatencyMs = 1000, AutoReconnect = false,
    };
    using var cp = new HomePodController(partial) { SourceFactory = _ => new SilenceSource() };
    cp.LogMessage += m => Console.WriteLine("    · " + m);
    bool partialThrew = false;
    try { await cp.ConnectAsync(); } catch { partialThrew = true; }
    Check(!partialThrew && cp.State == StreamState.Streaming, "一台连不上时另一台仍然推流", cp.Status);
    Check(cp.Status.Contains("1/2"), "状态写明只连上一部分", cp.Status);
    await cp.DisconnectAsync();

    Console.WriteLine(fails == 0 ? "\n控制器测试全部通过" : "\n控制器测试有 " + fails + " 项失败");
    return fails == 0 ? 0 : 1;
}

static async Task<int> FakeRx(string[] args)
{
    string Arg(string n, string d) { int i = Array.IndexOf(args, "--" + n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : d; }
    int port = int.Parse(Arg("port", "7000"));
    double seconds = double.Parse(Arg("seconds", "60"));
    using var rx = new FakeReceiver(Console.WriteLine, port) { DropEvery = 0 };
    Console.WriteLine("假 HomePod 已在 127.0.0.1:" + rx.Port + " 监听 " + seconds + " 秒");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int lastConn = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        await Task.Delay(1000);
        if (rx.Connections != lastConn || sw.ElapsedMilliseconds % 5000 < 1000)
        {
            lastConn = rx.Connections;
            Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:0}s 连接数 {rx.Connections}  音频包 {rx.AudioPackets}  同步包 {rx.SyncPackets}  授时应答 {rx.TimingReplies}  音量 {rx.LastVolumeDb} dB  解码峰值 {rx.MaxAbsSample / 32768.0:0.000}  心跳 {rx.FeedbackCount}  TEARDOWN {rx.TornDown}");
        }
    }
    Console.WriteLine($"结束: 连接数 {rx.Connections}，音频包 {rx.AudioPackets}，解码峰值 {rx.MaxAbsSample / 32768.0:0.000}，TEARDOWN={rx.TornDown}，解密失败 {rx.DecryptFailures}");
    return 0;
}

static async Task<int> Probe(string[] args)
{
    // --txt：额外打印 /info 里与分组 / 立体声对有关的 TXT 字段（只读，不配对、不发声）
    bool txt = args.Contains("--txt");
    var ips = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
    var probed = await Task.WhenAll(ips.Select(ip => Ap2Core.Discovery.ProbeAsync(ip)));
    // 同一立体声对的设备一起裁决角色（与程序里的逻辑一致）
    var resolved = Ap2Core.Discovery.ResolvePairs(probed.OfType<Ap2Core.FoundDevice>());
    foreach (var ip in ips)
    {
        var f = resolved.FirstOrDefault(x => x.Ip == ip);
        Console.WriteLine(f == null ? ip + ": 无响应或不是 AirPlay 设备" : $"{ip}: 名称={f.Name} 型号={f.Model} 角色={(f.Role == "" ? "（单独设备）" : f.Role)}{(f.Unpaired ? "（未成对）" : "")} 立体声对ID={(f.PairId == "" ? "-" : f.PairId[..8] + "…")}");
        if (!txt || f == null) continue;
        if (await Ap2Core.Discovery.FetchInfoAsync(ip) is not { } info) continue;
        var t = Ap2Core.Discovery.TxtOf(info);
        string[] keys = { "gid", "igl", "gcgl", "pgid", "pgcgl", "tsid", "gpn", "isGroupLeader" };
        Console.WriteLine("    TXT: " + string.Join("  ", keys.Where(t.ContainsKey).Select(k => $"{k}={t[k]}")));
        Console.WriteLine("    其余键: " + string.Join(" ", t.Keys.Where(k => !keys.Contains(k)).OrderBy(k => k)));
        Console.WriteLine("    /info 顶层键: " + string.Join(" ", info.Select(kv => kv.Key).OrderBy(k => k)));
    }
    return 0;
}

/// <summary>按调用次数递增的采样，用来核对两路拿到的是不是同一块</summary>
sealed class CountSource : IAudioSource
{
    int _n;
    public void Read(Span<short> stereo, int frames)
    {
        for (int i = 0; i < frames * 2; i++) stereo[i] = (short)_n++;
    }
}
