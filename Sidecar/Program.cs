using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ap2Core;
using Ap2Core.Audio;

namespace HomePodSidecar;

/// <summary>
/// 界面无关的宿主进程：stdin/stdout 上跑 NDJSON 协议，内部复用 HomePodController。
/// 每行一个 JSON：{"id":1,"cmd":"connect"}；回包 {"id":1,"ok":true,"payload":{...}}；
/// 主动事件 {"ev":"state"|"level"|"log"|"config"|"devices"|"scan",...}。
/// </summary>
internal static class Program
{
    static readonly object WriteLock = new();
    /// <summary>配置读写都经过这把锁（不在此锁内做耗时的 COM 枚举）</summary>
    static readonly object CfgLock = new();
    static readonly object ScanLock = new();
    static readonly object SaveLock = new();
    static readonly SemaphoreSlim Control = new(1, 1);
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static AppConfig _cfg = null!;
    static HomePodController _ctl = null!;
    static Stream _out = null!;
    static CancellationTokenSource? _scanCts;
    static Timer? _saveTimer;
    static int _stopping;
    static LipSyncServer? _lip;

    static bool Stopping => Volatile.Read(ref _stopping) != 0;

    sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--lipsync-policy")
            return LipSyncPolicyCommand(args);
        if (args.Length > 0 && args[0] == "--tone-test")
            return await ToneTest(args);

        Console.OutputEncoding = Encoding.UTF8;
        _out = Console.OpenStandardOutput();
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        _cfg = AppConfig.Load();
        Logger.Init();
        _ctl = new HomePodController(_cfg.ToControllerSettings());
        _ctl.Changed += () => Send(new { ev = "state", state = _ctl.State.ToString(), status = _ctl.Status });
        _ctl.LogMessage += line => { Logger.Write(line); Send(new { ev = "log", line }); };

        _ = Task.Run(LevelLoop);
        StartLipSync();
        Send(new { ev = "ready", state = _ctl.State.ToString(), status = _ctl.Status, config = Snapshot() });
        bool autoConnect;
        List<string> pairIps;
        lock (CfgLock)
        {
            autoConnect = _cfg.AutoConnectOnStart;
            pairIps = _cfg.Devices.Where(d => !string.IsNullOrEmpty(d.PairId)).Select(d => d.Ip).ToList();
        }
        if (autoConnect) _ = ConnectCore();   // 连接前会自行校正立体声对目标
        else if (pairIps.Count > 0) _ = Task.Run(() => RefreshRolesAsync(pairIps));
        _ = Task.Run(RoleRefreshLoop);   // 角色会随播放状态变化，定期重读才能记住真正的组长
        Logger.Write("sidecar 启动");

        while (!Stopping)
        {
            string? text;
            try { text = await input.ReadLineAsync(); }
            catch { break; }
            if (text == null) break;
            if (string.IsNullOrWhiteSpace(text)) continue;
            JsonElement msg;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    Send(new { ev = "error", message = "bad json" });
                    continue;
                }
                msg = doc.RootElement.Clone();
            }
            catch { Send(new { ev = "error", message = "bad json" }); continue; }
            _ = Task.Run(() => Handle(msg));
        }
        Shutdown();
        return 0;
    }

    static bool TryParseIpv4(string raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string trimmed = raw.Trim();
        if (IPAddress.TryParse(trimmed, out var direct) && direct.AddressFamily == AddressFamily.InterNetwork)
        {
            normalized = direct.ToString();
            return true;
        }
        var m = Regex.Match(trimmed, @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])");
        if (m.Success && IPAddress.TryParse(m.Groups[1].Value, out var extracted) && extracted.AddressFamily == AddressFamily.InterNetwork)
        {
            normalized = extracted.ToString();
            return true;
        }
        return false;
    }

    // ---------- 命令 ----------
    static async Task Handle(JsonElement msg)
    {
        long id = 0;
        try
        {
            if (msg.ValueKind != JsonValueKind.Object)
            {
                Send(new { ev = "error", message = "bad json" });
                return;
            }
            id = msg.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
            string cmd = Str(msg, "cmd");
            switch (cmd)
            {
                case "state":
                    Reply(id, new { state = _ctl.State.ToString(), status = _ctl.Status, config = Snapshot() });
                    break;

                case "connect":
                    await Control.WaitAsync();
                    try { await ConnectUnderLock(); }
                    finally { Control.Release(); }
                    Reply(id, new { state = _ctl.State.ToString(), status = _ctl.Status });
                    break;

                case "disconnect":
                    await Control.WaitAsync();
                    try { await _ctl.DisconnectAsync(); }
                    finally { Control.Release(); }
                    Reply(id, new { state = _ctl.State.ToString(), status = _ctl.Status });
                    break;

                case "setVolume":
                {
                    int pct;
                    lock (CfgLock)
                    {
                        pct = Math.Clamp(Int(msg, "value", _cfg.VolumePercent), 0, 100);
                        _cfg.VolumePercent = pct;
                    }
                    _ctl.SetVolumePercent(pct);
                    SaveSoon();
                    Reply(id, new { volumePercent = pct });
                    break;
                }

                case "setLatency":
                {
                    int ms;
                    lock (CfgLock)
                    {
                        ms = Math.Clamp(Int(msg, "value", _cfg.LatencyMs), 0, 2000);
                        _cfg.LatencyMs = ms;
                    }
                    _ctl.Settings.LatencyMs = ms;
                    SaveNow();
                    Reply(id, new { latencyMs = ms });
                    break;
                }

                case "setAudioDevice":
                {
                    string endpointId = Str(msg, "deviceId").Trim();
                    var endpoints = LoopbackCapture.GetRenderEndpoints();
                    if (endpointId.Length > 0 && endpoints.All(x => x.Id != endpointId))
                        throw new InvalidOperationException("选择的音频输出设备当前不可用");

                    bool changed;
                    bool restart = false;
                    object snap;
                    await Control.WaitAsync();
                    try
                    {
                        lock (CfgLock)
                        {
                            changed = endpointId != _cfg.AudioCaptureDeviceId;
                            if (changed) _cfg.AudioCaptureDeviceId = endpointId;
                        }
                        restart = changed && _ctl.State != StreamState.Disconnected;
                        if (changed)
                        {
                            string selected = endpointId.Length == 0
                                ? "跟随 Windows 默认播放设备"
                                : endpoints.First(x => x.Id == endpointId).Name;
                            Logger.Write("切换音频来源: " + selected);
                            _ctl.Settings.AudioCaptureDeviceId = endpointId;
                            SaveNow();
                        }
                        snap = Snapshot(endpoints);
                        if (changed) Send(new { ev = "config", config = snap });
                        if (restart) await _ctl.DisconnectAsync();
                    }
                    finally { Control.Release(); }
                    Reply(id, new { audioCaptureDeviceId = endpointId, config = snap });
                    if (restart) _ = ConnectCore();
                    break;
                }

                case "refreshAudioDevices":
                    Reply(id, new { config = Snapshot() });
                    break;

                case "setOptions":
                {
                    bool ar, ac, sb, vs, vsChanged;
                    lock (CfgLock)
                    {
                        bool prevVs = _cfg.VideoSync;
                        ar = Bool(msg, "autoReconnect", _cfg.AutoReconnect);
                        ac = Bool(msg, "autoConnectOnStart", _cfg.AutoConnectOnStart);
                        sb = Bool(msg, "showBoard", _cfg.ShowBoard);
                        vs = Bool(msg, "videoSync", _cfg.VideoSync);
                        _cfg.AutoReconnect = ar;
                        _cfg.AutoConnectOnStart = ac;
                        _cfg.ShowBoard = sb;
                        _cfg.VideoSync = vs;
                        vsChanged = vs != prevVs;
                    }
                    _ctl.Settings.AutoReconnect = ar;
                    string hint = vsChanged ? ApplyVideoSync() : "";
                    SaveNow();
                    Reply(id, new { autoReconnect = ar, autoConnectOnStart = ac, showBoard = sb, videoSync = vs, hint });
                    break;
                }

                case "selectDevice":
                {
                    string ip = Str(msg, "ip");
                    var selected = await SelectDeviceAsync(ip);
                    Reply(id, new { ip = selected.Ip, hint = selected.Hint, config = Snapshot() });
                    break;
                }

                case "addDevice":
                {
                    string raw = Str(msg, "ip");
                    if (!TryParseIpv4(raw, out string ip))
                        throw new InvalidOperationException("请输入有效的 IP 地址");
                    lock (CfgLock)
                    {
                        if (_cfg.Devices.All(d => d.Ip != ip)) _cfg.Devices.Add(new DeviceEntry { Ip = ip });
                    }
                    SaveNow();
                    SendDevices();
                    // 先探测再选：手工加的 IP 如果其实是成员，要等角色读回来才能改连组长，不能先连上去
                    var f = await Discovery.ProbeAsync(ip);
                    if (f != null)
                    {
                        // 单台看不出是否成对：把同一对的设备一起探测、一起裁决
                        List<string> pairIps;
                        lock (CfgLock)
                            pairIps = _cfg.Devices.Where(d => d.Ip == ip || (f.PairId != "" && d.PairId == f.PairId)).Select(d => d.Ip).ToList();
                        await RefreshRolesAsync(pairIps);
                    }
                    var selected = await SelectDeviceAsync(ip);
                    Reply(id, new { ip = selected.Ip, hint = selected.Hint, config = Snapshot() });
                    break;
                }

                case "scan":
                {
                    CancellationTokenSource cts;
                    CancellationTokenSource? prev;
                    lock (ScanLock)
                    {
                        prev = _scanCts;
                        cts = _scanCts = new CancellationTokenSource();
                    }
                    try { prev?.Cancel(); prev?.Dispose(); } catch { }
                    Send(new { ev = "scan", phase = "start" });
                    try
                    {
                        var found = await Task.Run(() => Discovery.ScanAsync(
                            cts.Token,
                            new SyncProgress<int>(p =>
                            {
                                if (!cts.IsCancellationRequested)
                                    Send(new { ev = "scan", phase = "progress", progress = p });
                            })));
                        if (cts.IsCancellationRequested)
                        {
                            Send(new { ev = "scan", phase = "done", cancelled = true, count = 0 });
                            Reply(id, new { cancelled = true });
                            break;
                        }
                        ApplyFound(found);
                        SaveNow();
                        SendDevices();
                        Send(new { ev = "scan", phase = "done", count = found.Count });
                        Reply(id, new { count = found.Count, config = Snapshot() });
                    }
                    catch
                    {
                        Send(new { ev = "scan", phase = "done", cancelled = true, count = 0 });
                        throw;
                    }
                    finally
                    {
                        bool disposeNow = false;
                        lock (ScanLock)
                        {
                            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
                            disposeNow = true;
                        }
                        if (disposeNow) { try { cts.Dispose(); } catch { } }
                    }
                    break;
                }

                case "cancelScan":
                {
                    CancellationTokenSource? cur;
                    lock (ScanLock) cur = _scanCts;
                    try { cur?.Cancel(); } catch { }
                    Reply(id, new { cancelled = true });
                    break;
                }

                case "refreshDevices":
                {
                    List<string> ips;
                    lock (CfgLock) ips = _cfg.Devices.Select(d => d.Ip).ToList();
                    await RefreshRolesAsync(ips);
                    Reply(id, new { config = Snapshot() });
                    break;
                }

                case "quit":
                    Reply(id, new { });
                    Shutdown();
                    break;

                default:
                    Reply(id, new { error = "unknown command: " + cmd }, ok: false);
                    break;
            }
        }
        catch (Exception e)
        {
            string reason = string.IsNullOrWhiteSpace(e.Message) ? "操作失败" : e.Message;
            Send(new { ev = "state", state = _ctl.State.ToString(), status = _ctl.Status });
            Reply(id, new { error = reason, state = _ctl.State.ToString(), status = _ctl.Status }, ok: false);
        }
    }

    // ---------- 立体声对 ----------

    /// <summary>
    /// 把扫描 / 探测结果并入配置。立体声对按整组裁决（见 <see cref="Discovery.ResolvePairs"/>）：
    /// 成对时更新组长 / 成员；没有成对时保留 HomePod 自己报告的角色，并标记为未成对，不替它猜。
    /// </summary>
    static void ApplyFound(IEnumerable<FoundDevice> found)
    {
        lock (CfgLock)
        {
            var resolved = Discovery.ResolvePairs(found);
            foreach (var f in resolved)
            {
                var ex = _cfg.Devices.FirstOrDefault(d => d.Ip == f.Ip);
                if (ex == null) _cfg.Devices.Add(new DeviceEntry { Name = f.Name, Ip = f.Ip, Role = f.Role, PairId = f.PairId, Unpaired = f.Unpaired });
                else { ex.Name = f.Name; ex.Role = f.Role; ex.PairId = f.PairId; ex.Unpaired = f.Unpaired; }
            }
            // 成对时记住组长：之后角色再变含糊，也以这个确认为准，绝不改连成员
            foreach (var g in resolved.Where(f => f.PairId != "").GroupBy(f => f.PairId))
            {
                var members = g.ToList();
                var leader = members.FirstOrDefault(f => f.Role == "组长" && !f.Unpaired);
                var member = members.FirstOrDefault(f => f.Role == "成员" && !f.Unpaired);
                if (members.Count == 2 && leader != null && member != null)
                {
                    if (!_cfg.PairLeaders.TryGetValue(g.Key, out var known) || known != leader.Ip)
                        Logger.Write($"立体声对 {leader.Name} 确认组长 {leader.Ip}（成员 {member.Ip}）");
                    _cfg.PairLeaders[g.Key] = leader.Ip;
                }
            }
            // 成对时，本轮没探测到的同对设备记为成员
            foreach (var leader in resolved.Where(f => f.Role == "组长" && f.PairId != "" && !f.Unpaired))
                foreach (var d in _cfg.Devices.Where(d => d.PairId == leader.PairId && d.Ip != leader.Ip))
                    d.Role = "成员";
        }
    }

    /// <summary>现场探测给定设备并更新角色（不在线的保持原样）；有变化才保存</summary>
    static async Task RefreshRolesAsync(IReadOnlyList<string> ips)
    {
        if (ips.Count == 0) return;
        string before;
        lock (CfgLock) before = JsonSerializer.Serialize(new { _cfg.Devices, _cfg.PairLeaders });
        var probed = await Task.WhenAll(ips.Select(ip => Discovery.ProbeAsync(ip, timeoutMs: 700)));
        var fresh = probed.OfType<FoundDevice>().ToList();
        if (fresh.Count > 0) ApplyFound(fresh);
        bool changed;
        lock (CfgLock) changed = JsonSerializer.Serialize(new { _cfg.Devices, _cfg.PairLeaders }) != before;
        if (changed) SaveNow();
        SendDevices();
        if (changed) Send(new { ev = "config", config = Snapshot() });
    }

    /// <summary>
    /// 立体声对里当前真正的组长：现场探测同一对的所有设备再裁决（角色会变化，保存的标记可能已过期）。
    /// 不是立体声对、或探测不到时返回原 IP。
    /// </summary>
    static async Task<PairTarget> CurrentLeaderAsync(string ip)
    {
        List<string> pair;
        lock (CfgLock)
        {
            var picked = _cfg.Devices.FirstOrDefault(d => d.Ip == ip);
            if (picked == null || string.IsNullOrEmpty(picked.PairId)) return new(ip, "");
            pair = _cfg.Devices.Where(d => d.PairId == picked.PairId).Select(d => d.Ip).ToList();
        }
        await RefreshRolesAsync(pair);
        lock (CfgLock) return Discovery.ChooseTarget(ip, _cfg.Devices, _cfg.PairLeaders);
    }

    /// <summary>连接前确认目标：若当前目标是立体声对的成员，改连组长并通知界面</summary>
    static async Task RetargetToLeaderAsync()
    {
        string currentIp;
        lock (CfgLock) currentIp = _cfg.Ip;
        var target = await CurrentLeaderAsync(currentIp);
        if (target.Ip == currentIp) return;
        Logger.Write($"目标 {currentIp} 是立体声对成员，改连组长 {target.Ip}");
        lock (CfgLock) _cfg.Ip = target.Ip;
        _ctl.Settings.Ip = target.Ip;
        SaveNow();
        Send(new { ev = "config", config = Snapshot() });
    }

    static async Task<(string Ip, string Hint)> SelectDeviceAsync(string rawIp)
    {
        if (!TryParseIpv4(rawIp, out string ip)) throw new InvalidOperationException("无效的 IP 地址");
        // 立体声对：成对时点成员改连组长；两台都自称组长时按你点的设备连，但提示未成对
        await Control.WaitAsync();
        bool restart = false;
        string targetIp;
        string hint;
        try
        {
            var target = await CurrentLeaderAsync(ip);
            targetIp = target.Ip;
            List<string> next;
            List<string> current;
            lock (CfgLock)
            {
                next = Discovery.PlaybackTargets(targetIp, _cfg.Devices);
                current = _ctl.Settings.Ips.Count > 0 ? _ctl.Settings.Ips.ToList() : new List<string> { _cfg.Ip };
                var now = _cfg.Devices.FirstOrDefault(d => d.Ip == targetIp);
                // 两台都未成对时会各开一路；只剩一台可连时才提示“仅此一台”
                hint = next.Count >= 2 ? "" : now?.Unpaired == true ? $"未组立体声对，只会从 {targetIp} 这一台出声" : "";
                _cfg.Ip = targetIp;
            }
            bool same = next.Count == current.Count && next.All(current.Contains);
            bool active = _ctl.State != StreamState.Disconnected;
            _ctl.Settings.Ip = targetIp;
            _ctl.Settings.Ips = next;
            // 两台一起播时，在这两台之间切换不必拆掉会话再重连
            if (same)
            {
                SaveNow();
                Send(new { ev = "config", config = Snapshot() });
                return (targetIp, hint);
            }
            if (active)
            {
                await _ctl.DisconnectAsync();
                restart = true;
            }
            SaveNow();
            Send(new { ev = "config", config = Snapshot() });
        }
        finally { Control.Release(); }
        if (restart) _ = ConnectCore();
        return (targetIp, hint);
    }

    /// <summary>按当前角色决定推一台还是两台，并写进控制器。成对时只有组长</summary>
    static void ApplyPlaybackTargets()
    {
        List<string> ips;
        string cfgIp;
        lock (CfgLock)
        {
            cfgIp = _cfg.Ip;
            ips = Discovery.PlaybackTargets(cfgIp, _cfg.Devices);
        }
        _ctl.Settings.Ips = ips;
        if (ips.Count == 1) _ctl.Settings.Ip = ips[0];
        else if (!string.IsNullOrEmpty(cfgIp)) _ctl.Settings.Ip = cfgIp;
        if (ips.Count >= 2) Logger.Write("同时推流到 " + string.Join(" 和 ", ips));
    }

    /// <summary>角色随播放状态变化（播放 / 空闲时 igl、pgid 不同）；定期重读，才能记住真正的组长并刷新界面</summary>
    static async Task RoleRefreshLoop()
    {
        while (!Stopping)
        {
            try { await Task.Delay(30_000); } catch { return; }
            if (Stopping) return;
            List<string> ips;
            lock (CfgLock) ips = _cfg.Devices.Where(d => !string.IsNullOrEmpty(d.PairId)).Select(d => d.Ip).ToList();
            if (ips.Count == 0) continue;
            try { await RefreshRolesAsync(ips); } catch { /* 探测失败保持原状 */ }
        }
    }

    static Task ConnectCore()
    {
        return Task.Run(async () =>
        {
            await Control.WaitAsync();
            try { await ConnectUnderLock(); }
            catch { /* 状态已由控制器写入 */ }
            finally { Control.Release(); }
        });
    }

    static async Task ConnectUnderLock()
    {
        await RetargetToLeaderAsync();
        ApplyPlaybackTargets();
        await _ctl.ConnectAsync();
    }

    // ---------- 事件 ----------
    static async Task LevelLoop()
    {
        while (!Stopping)
        {
            try
            {
                await Task.Delay(50);
                if (_ctl.State == StreamState.Streaming)
                {
                    float peak = _ctl.TakePeak();
                    if (!float.IsFinite(peak)) peak = 0f;
                    Send(new { ev = "level", value = peak });
                }
            }
            catch (Exception e)
            {
                Logger.Write("电平采样异常: " + e.Message);
            }
        }
    }

    static void SendDevices() => Send(new { ev = "devices", devices = DeviceList() });

    static object[] DeviceList()
    {
        lock (CfgLock)
            return _cfg.Devices
                .Select(d => new { name = d.Name, ip = d.Ip, role = d.Role, pairId = d.PairId, unpaired = d.Unpaired })
                .Cast<object>().ToArray();
    }

    static object Snapshot(IReadOnlyList<AudioEndpoint>? prefetchedEndpoints = null)
    {
        // 在 CfgLock 外枚举 COM 音频端点，避免卡住配置读写与 /lipsync 查询
        var audioDevices = prefetchedEndpoints != null
            ? prefetchedEndpoints.Select(x => new { id = x.Id, name = x.Name, isDefault = x.IsDefault }).Cast<object>().ToArray()
            : AudioDeviceList();
        lock (CfgLock)
            return new
            {
                ip = _cfg.Ip,
                ips = Discovery.PlaybackTargets(_cfg.Ip, _cfg.Devices),
                volumePercent = _cfg.VolumePercent,
                latencyMs = _cfg.LatencyMs,
                autoReconnect = _cfg.AutoReconnect,
                autoConnectOnStart = _cfg.AutoConnectOnStart,
                showBoard = _cfg.ShowBoard,
                videoSync = _cfg.VideoSync,
                deviceId = _cfg.DeviceId,
                audioCaptureDeviceId = _cfg.AudioCaptureDeviceId,
                audioDevices,
                devices = _cfg.Devices
                    .Select(d => new { name = d.Name, ip = d.Ip, role = d.Role, pairId = d.PairId, unpaired = d.Unpaired })
                    .Cast<object>().ToArray(),
            };
    }

    static object[] AudioDeviceList()
    {
        try
        {
            return LoopbackCapture.GetRenderEndpoints()
                .Select(x => new { id = x.Id, name = x.Name, isDefault = x.IsDefault })
                .Cast<object>()
                .ToArray();
        }
        catch (Exception e)
        {
            Logger.Write("列举音频输出设备失败: " + e.Message);
            return Array.Empty<object>();
        }
    }

    // ---------- 工具 ----------
    static void Reply(long id, object payload, bool ok = true) => Send(new { id, ok, payload });

    static void Send(object o)
    {
        byte[] bytes;
        try
        {
            bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o, Json) + "\n");
        }
        catch (Exception e)
        {
            Logger.Write("序列化事件失败: " + e.Message);
            return;
        }
        try
        {
            lock (WriteLock) { _out.Write(bytes, 0, bytes.Length); _out.Flush(); }
        }
        catch { /* 宿主管道已关闭 */ }
    }

    static string Str(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static int Int(JsonElement m, string name, int fallback) =>
        m.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    static bool Bool(JsonElement m, string name, bool fallback) =>
        m.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    static void SaveSoon()
    {
        lock (SaveLock)
        {
            if (Stopping) return;
            _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            try { _saveTimer.Change(500, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }
    }

    static void SaveNow() { try { lock (CfgLock) _cfg.Save(); } catch { } }

    /// <summary>本地端口一直开着，插件才能读到「关掉了」。启动时不弹管理员窗口。</summary>
    static void StartLipSync()
    {
        try
        {
            _lip = new LipSyncServer(() =>
            {
                lock (CfgLock) return (_cfg.VideoSync, _cfg.LatencyMs);
            });
            Logger.Write("画面对齐端口 " + _lip.Port + " 扩展 " + _lip.ExtensionId);
        }
        catch (Exception ex)
        {
            Logger.Write("画面对齐服务没起来: " + ex.Message);
            return;
        }
        bool enabled;
        lock (CfgLock) enabled = _cfg.VideoSync;
        if (!enabled) return;
        try { Logger.Write("插件目录 " + _lip.ExportUnpacked()); }
        catch (Exception ex) { Logger.Write("导出插件失败: " + ex.Message); }
        try
        {
            if (!LipSyncPolicy.InstalledForUser(_lip.ExtensionId, _lip.Port))
                LipSyncPolicy.InstallForUser(_lip.ExtensionId, _lip.Port);
        }
        catch (Exception ex)
        {
            Logger.Write("启动时没能写入浏览器策略: " + ex.Message);
        }
    }

    static string ApplyVideoSync()
    {
        if (_lip == null) return "画面对齐服务没起来";
        string folder = "";
        try { folder = _lip.ExportUnpacked(); }
        catch (Exception ex) { Logger.Write("导出插件失败: " + ex.Message); }
        double sec;
        bool enabled;
        lock (CfgLock) { sec = _cfg.LatencyMs / 1000.0; enabled = _cfg.VideoSync; }
        string late = sec.ToString("0.#", CultureInfo.InvariantCulture);
        if (enabled)
        {
            if (EnsurePolicy(_lip.ExtensionId, _lip.Port))
                return "对齐画面已打开。保持本程序运行，然后完全退出并重新打开一次 Chrome。网页画面会晚 " + late + " 秒，声音仍马上送出。";
            OpenFolder(folder);
            return "自动安装需要管理员确认。若已取消，请打开 chrome://extensions，打开开发者模式，加载文件夹 " + folder + " 。只需一次，然后刷新正在看的网页。";
        }
        if (!LipSyncPolicy.AnyPolicyPresent(_lip.ExtensionId))
            return "已关闭画面推迟。正在播的网页会马上恢复。";
        if (RemovePolicy())
            return "已关闭画面推迟。正在播的网页会马上恢复。";
        return "画面推迟已关掉，策略没删干净。正在播的网页会马上恢复；可以再关一次并在管理员窗口点同意。";
    }

    static bool EnsurePolicy(string id, int port)
    {
        if (LipSyncPolicy.InstalledForUser(id, port)) return true;
        try
        {
            LipSyncPolicy.InstallForUser(id, port);
            if (LipSyncPolicy.InstalledForUser(id, port)) return true;
        }
        catch (Exception ex)
        {
            Logger.Write("直接写策略失败: " + ex.Message);
        }
        return ElevatePolicy("install", id, port) && LipSyncPolicy.InstalledForUser(id, port);
    }

    static bool RemovePolicy()
    {
        string id = _lip!.ExtensionId;
        int port = _lip.Port;
        try
        {
            LipSyncPolicy.RemoveForUser(id, port);
            if (!LipSyncPolicy.AnyPolicyPresent(id)) return true;
        }
        catch (Exception ex)
        {
            Logger.Write("直接删策略失败: " + ex.Message);
        }
        return ElevatePolicy("remove", id, port) && !LipSyncPolicy.AnyPolicyPresent(id);
    }

    /// <summary>当前用户对 HKCU\Software\Policies 只有读权限，写策略要管理员令牌。只在用户打开或关闭对齐时弹出。</summary>
    static bool ElevatePolicy(string action, string id, int port)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || !File.Exists(exe)) return false;
        if (id.Length != 32 || id.Any(c => c < 'a' || c > 'p')) return false;
        string args = action == "install"
            ? "--lipsync-policy install " + id + " " + port
            : "--lipsync-policy remove " + id + " " + port;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return false;
            if (!p.WaitForExit(30000)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.Write("管理员安装被取消或失败: " + ex.Message);
            return false;
        }
    }

    static int LipSyncPolicyCommand(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[1] == "install")
            {
                string id = args[2];
                if (id.Length != 32 || id.Any(c => c < 'a' || c > 'p')) return 2;
                if (!int.TryParse(args[3], out int port) || port < 1 || port > 65535) return 2;
                LipSyncPolicy.InstallForUser(id, port);
                return LipSyncPolicy.InstalledForUser(id, port) ? 0 : 3;
            }
            if (args.Length >= 4 && args[1] == "remove")
            {
                string id = args[2];
                if (id.Length != 32 || id.Any(c => c < 'a' || c > 'p')) return 2;
                if (!int.TryParse(args[3], out int port) || port < 1 || port > 65535) return 2;
                LipSyncPolicy.RemoveForUser(id, port);
                return LipSyncPolicy.AnyPolicyPresent(id) ? 3 : 0;
            }
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static void OpenFolder(string folder)
    {
        if (folder.Length == 0 || !Directory.Exists(folder)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(folder);
            Process.Start(psi);
        }
        catch { /* 打不开文件夹不影响提示文字 */ }
    }

    static void Shutdown()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        lock (SaveLock)
        {
            try { _saveTimer?.Dispose(); } catch { }
            _saveTimer = null;
        }
        // 先落盘配置，避免宿主强杀前尚未保存防抖的音量或选项
        SaveNow();
        lock (ScanLock)
        {
            try { _scanCts?.Cancel(); } catch { }
        }
        try { _ctl.Dispose(); } catch { }
        try { _lip?.Dispose(); } catch { }
        Logger.Write("sidecar 退出");
        try { lock (WriteLock) _out.Flush(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>诊断用：绕过界面与回环抓取，直接向 HomePod 发送一段测试音。</summary>
    static async Task<int> ToneTest(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = AppConfig.Load();
        string rawIp = args.Length > 1 ? args[1] : cfg.Ip;
        if (!TryParseIpv4(rawIp, out string ip))
        {
            Console.WriteLine("测试音失败: 请提供有效的 HomePod IPv4 地址");
            return 1;
        }
        double seconds = args.Length > 2 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 10;
        double volumeDb = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -6;
        seconds = Math.Clamp(seconds, 1, 60);
        try
        {
            using var session = new Ap2Session(new Ap2Options
            {
                Ip = ip,
                Port = 7000,
                LatencyFrames = 500 * Ap2Session.Rate / 1000,
                DeviceId = cfg.DeviceId,
            });
            session.LogMessage += Console.WriteLine;
            try
            {
                await session.ConnectAsync();
                await session.SetVolumeDbAsync(volumeDb);
                session.StartStreaming(new ToneSource(440));
                Console.WriteLine($"播放 440 Hz 测试音 {seconds:0.#} 秒，音量 {volumeDb:0.#} dB");
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                Console.WriteLine($"统计: 音频包 {session.AudioPackets} 同步包 {session.SyncPackets} 授时应答 {session.TimingReplies}");
                await session.StopAsync();
                return 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("测试音失败: " + e.Message);
                try { await session.StopAsync(); } catch { }
                return 1;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("测试音失败: " + e.Message);
            return 1;
        }
    }
}
