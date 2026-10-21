using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Ap2Core;

public sealed record FoundDevice(string Ip, string Name, string Model, string DeviceId, string Role = "", string PairId = "")
{
    /// <summary>TXT 里 igl=1：此刻自称组长</summary>
    public bool ClaimsLeader { get; init; }
    /// <summary>TXT 里带 pgid：隶属上级分组（立体声对的成员会带）</summary>
    public bool HasParentGroup { get; init; }
    /// <summary>同一对的两台没有配成立体声对（例如两台都自称组长）：此时只会从所选的这一台出声</summary>
    public bool Unpaired { get; init; }
    public override string ToString() => $"{Name}（{Ip}，{Model}）";
}

/// <summary>
/// 设备发现：不依赖多播 mDNS（本机的 HomePod 不回应多播），而是在局域网网段上探测 TCP 7000，
/// 对有响应的地址发一次明文 GET /info（不配对、不发声），读取名称与型号。
/// </summary>
public static class Discovery
{
    static readonly string[] SkipNic =
        { "mihomo", "tap", "wintun", "vmware", "hyper-v", "virtualbox", "loopback", "vethernet", "tailscale", "zerotier", "clash", "wireguard", "bluetooth" };

    /// <summary>本机各物理网卡所在网段内的候选主机地址（/24 为限）</summary>
    public static List<IPAddress> CandidateHosts()
    {
        var result = new HashSet<uint>();
        var self = new HashSet<uint>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)) continue;
            string label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (SkipNic.Any(label.Contains)) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                uint ip = ToUInt(ua.Address);
                if ((ip >> 16) == 0xA9FE) continue;   // 169.254.x.x 链路本地
                self.Add(ip);
                int prefix = Math.Clamp(ua.PrefixLength, 24, 30);
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                uint net = ip & mask, bcast = net | ~mask;
                for (uint h = net + 1; h < bcast; h++) result.Add(h);
            }
        }
        foreach (var s in self) result.Remove(s);
        return result.OrderBy(x => x).Select(FromUInt).ToList();
    }

    static uint ToUInt(IPAddress a) { var b = a.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
    static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    /// <summary>探测单个地址：连接 7000 并读取 /info；不是 AirPlay 接收端返回 null</summary>
    public static async Task<FoundDevice?> ProbeAsync(string ip, int port = 7000, int timeoutMs = 800, CancellationToken ct = default)
    {
        if (await FetchInfoAsync(ip, port, timeoutMs, ct).ConfigureAwait(false) is not { } d) return null;
        return FromInfo(ip, d);
    }

    /// <summary>由 /info 属性表构造设备信息（含立体声对角色初判）</summary>
    public static FoundDevice FromInfo(string ip, PDict d)
    {
        var (role, pairId, igl, parent) = DetectRole(d);
        return new FoundDevice(ip, d.GetStr("name") ?? ip, d.GetStr("model") ?? "?", d.GetStr("deviceID") ?? "", role, pairId)
        {
            ClaimsLeader = igl,
            HasParentGroup = parent,
        };
    }

    /// <summary>/info 里的 AirPlay TXT 记录（键值对）；没有时返回空表</summary>
    public static Dictionary<string, string> TxtOf(PDict info) =>
        info.Get("txtAirPlay") is byte[] raw ? ParseTxt(raw) : new Dictionary<string, string>();

    /// <summary>明文 GET /info（不配对、不发声），返回解码后的属性表；失败返回 null</summary>
    public static async Task<PDict?> FetchInfoAsync(string ip, int port = 7000, int timeoutMs = 800, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await sock.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), port), cts.Token).ConfigureAwait(false);
            await sock.SendAsync(Encoding.Latin1.GetBytes("GET /info RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: AirPlay/670.6.2\r\nContent-Length: 0\r\n\r\n"), cts.Token).ConfigureAwait(false);
            var buf = new byte[65536];
            int total = 0, headerEnd = -1, clen = 0;
            while (total < buf.Length)
            {
                int n = await sock.ReceiveAsync(buf.AsMemory(total), cts.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (headerEnd < 0)
                {
                    for (int i = 0; i + 3 < total; i++)
                        if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10) { headerEnd = i + 4; break; }
                    if (headerEnd > 0)
                    {
                        var head = Encoding.Latin1.GetString(buf, 0, headerEnd);
                        var m = System.Text.RegularExpressions.Regex.Match(head, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        clen = m.Success ? int.Parse(m.Groups[1].Value) : 0;
                        if (!head.StartsWith("RTSP/1.0 200")) return null;
                    }
                }
                if (headerEnd > 0 && total >= headerEnd + clen) break;
            }
            if (headerEnd < 0 || total < headerEnd + clen || clen == 0) return null;
            var body = buf.AsSpan(headerEnd, clen).ToArray();
            return Bplist.IsBplist(body) && Bplist.Decode(body) is PDict d ? d : null;
        }
        catch { return null; }
    }

    /// <summary>解析 DNS-SD TXT 记录（长度前缀的 key=value 序列）</summary>
    public static Dictionary<string, string> ParseTxt(byte[] b)
    {
        var r = new Dictionary<string, string>();
        int i = 0;
        while (i < b.Length)
        {
            int n = b[i++];
            if (i + n > b.Length) break;
            var kv = Encoding.UTF8.GetString(b, i, n);
            i += n;
            int e = kv.IndexOf('=');
            if (e > 0) r[kv[..e]] = kv[(e + 1)..];
        }
        return r;
    }

    /// <summary>
    /// 立体声对角色初判：同一对的两台设备 tsid 相同；组长 igl=1 且不带 pgid，成员 igl=0 并带 pgid（指向组长所在分组）。
    /// 返回 (角色, tsid, igl=1?, 带 pgid?)；不属于立体声对时角色为空。
    /// 单台的初判只看它自己；两台都自称组长时，整组会被 <see cref="ResolvePairs"/> 判为未成对。
    /// </summary>
    static (string Role, string PairId, bool Igl, bool HasParent) DetectRole(PDict info)
    {
        var t = TxtOf(info);
        t.TryGetValue("tsid", out var tsid);
        if (string.IsNullOrEmpty(tsid)) return ("", "", false, false);
        bool igl = t.TryGetValue("igl", out var v) && v == "1";
        bool parent = t.TryGetValue("pgid", out var pg) && !string.IsNullOrEmpty(pg);
        return (igl && !parent ? "组长" : "成员", tsid, igl, parent);
    }

    /// <summary>
    /// 按整组裁决立体声对角色（同一 tsid 的两台一起看）。
    /// 成对的一组：一台自称组长（igl=1）且不带 pgid，另一台自称成员（igl=0）并带 pgid，这时按报告给出组长 / 成员。
    /// 否则（例如两台都自称组长）：两台并没有配成一对，保留各自的报告并标记 Unpaired，不替它们猜谁是组长。
    /// 2026-10-20 实测：两台都自称组长、都不带 pgid，且组 ID 也变了，02:58、03:05、03:12、03:15 四次探测一致。
    /// 只探测到一台时没有比对对象，保持它自己的判断。
    /// </summary>
    public static List<FoundDevice> ResolvePairs(IEnumerable<FoundDevice> found)
    {
        var list = found.ToList();
        foreach (var g in list.Where(f => f.PairId != "").GroupBy(f => f.PairId).ToList())
        {
            var members = g.ToList();
            if (members.Count != 2) continue;
            var leaders = members.Where(f => f.ClaimsLeader && !f.HasParentGroup).ToList();
            var followers = members.Where(f => !f.ClaimsLeader && f.HasParentGroup).ToList();
            bool paired = leaders.Count == 1 && followers.Count == 1;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].PairId != g.Key) continue;
                list[i] = paired
                    ? list[i] with { Role = list[i].Ip == leaders[0].Ip ? "组长" : "成员", Unpaired = false }
                    : list[i] with { Unpaired = true };
            }
        }
        return list;
    }

    /// <summary>
    /// 连接目标裁决（基于刚刷新过的角色）：
    /// 成对时，选成员自动改连组长；两台都自称组长时，若记得这一对上次确认的组长，就继续连它。
    /// 2026-10-20 实测：直连成员会让 HomePod 重新报告两台都是组长，改连组长后半分钟左右恢复成对。
    /// 没有确认过组长时仍按所选设备连接，不猜。
    /// </summary>
    public static PairTarget ChooseTarget(string selectedIp, IReadOnlyList<DeviceEntry> devices, IReadOnlyDictionary<string, string> rememberedLeaders)
    {
        var selected = devices.FirstOrDefault(d => d.Ip == selectedIp);
        if (selected == null || string.IsNullOrEmpty(selected.PairId)) return new(selectedIp, "");
        // 只有“确认是成员”时才改连组长；两台都自称组长（未成对）时按所选设备连接，方便单独使用那一台
        if (selected.Role != "成员") return new(selectedIp, "");
        var group = devices.Where(d => d.PairId == selected.PairId).ToList();
        var leader = group.FirstOrDefault(d => d.Role == "组长");
        if (leader != null) return new(leader.Ip, "member");
        if (rememberedLeaders.TryGetValue(selected.PairId, out var remembered)
            && !string.IsNullOrEmpty(remembered) && remembered != selectedIp
            && group.Any(d => d.Ip == remembered))
            return new(remembered, "member");
        return new(selectedIp, "");
    }

    /// <summary>
    /// 实际要打开的推流会话。
    /// 已经配成立体声对时只返回组长：再连成员会让它退出上级分组，两台又都变成组长。
    /// 同一对的两台都未成对时返回两台：各开一路，每路都是完整的立体声，两只一起出声。
    /// 这不是家庭 App 的左右声道分离，分声道仍然只能由 HomePod 自己做。
    /// </summary>
    public static List<string> PlaybackTargets(string selectedIp, IReadOnlyList<DeviceEntry> devices)
    {
        if (string.IsNullOrWhiteSpace(selectedIp)) return new();
        var selected = devices.FirstOrDefault(d => d.Ip == selectedIp);
        if (selected == null || string.IsNullOrEmpty(selected.PairId)) return new List<string> { selectedIp };

        var group = devices.Where(d => d.PairId == selected.PairId && !string.IsNullOrEmpty(d.Ip)).ToList();
        bool paired = group.Count(d => d.Role == "组长" && !d.Unpaired) == 1
            && group.Any(d => d.Role == "成员" && !d.Unpaired);
        if (paired) return new List<string> { group.First(d => d.Role == "组长" && !d.Unpaired).Ip };
        if (group.Count == 2 && group.All(d => d.Unpaired))
        {
            var other = group.First(d => d.Ip != selectedIp).Ip;
            return new List<string> { selectedIp, other };
        }
        return new List<string> { selectedIp };
    }

    /// <summary>扫描本机所在网段，返回所有响应 /info 的 AirPlay 接收端</summary>
    public static async Task<List<FoundDevice>> ScanAsync(CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var hosts = CandidateHosts();
        var found = new List<FoundDevice>();
        using var gate = new SemaphoreSlim(64);
        int done = 0;
        var tasks = hosts.Select(async h =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var d = await ProbeAsync(h.ToString(), ct: ct).ConfigureAwait(false);
                if (d != null) lock (found) found.Add(d);
            }
            finally { gate.Release(); progress?.Report(Interlocked.Increment(ref done) * 100 / Math.Max(1, hosts.Count)); }
        }).ToList();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
        return ResolvePairs(found).OrderBy(f => ToUInt(IPAddress.Parse(f.Ip))).ToList();
    }
}
