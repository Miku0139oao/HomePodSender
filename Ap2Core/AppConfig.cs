using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ap2Core;

public sealed class DeviceEntry
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    /// <summary>立体声对角色："组长" / "成员" / ""（非立体声对）</summary>
    public string Role { get; set; } = "";
    public string PairId { get; set; } = "";
    /// <summary>最近一次探测的结论：同对的两台没有配成立体声对（界面据此提示“未组立体声对”）</summary>
    public bool Unpaired { get; set; }
    public override string ToString() => string.IsNullOrEmpty(Name) ? Ip : Name + "（" + Ip + "）";
}

/// <summary>立体声对的选择结果：Reason = "" / "member"（成员改连组长）/ "unpaired"（两台都自称组长，沿用上次确认的组长）</summary>
public sealed record PairTarget(string Ip, string Reason);

/// <summary>应用配置，保存在 %APPDATA%\HomePodSender\config.json</summary>
public sealed class AppConfig
{
    public List<DeviceEntry> Devices { get; set; } = new();
    public string Ip { get; set; } = "";
    public int VolumePercent { get; set; } = 20;
    public int LatencyMs { get; set; } = 1500;
    public bool AutoReconnect { get; set; } = true;
    public bool AutoConnectOnStart { get; set; }
    /// <summary>打开后，Chrome 插件把网页画面推迟 LatencyMs，声音仍立刻进回环</summary>
    public bool VideoSync { get; set; }
    /// <summary>界面：显示三张卡片后面的背景卡片（关掉后卡片各自悬浮在桌面上）</summary>
    public bool ShowBoard { get; set; } = true;
    public string DeviceId { get; set; } = "";
    /// <summary>每个立体声对（PairId）最近一次确认过的组长 IP。配对报告含糊时仍以它为准，避免直连成员把分组打散</summary>
    public Dictionary<string, string> PairLeaders { get; set; } = new();

    public static string Dir => Environment.GetEnvironmentVariable("HOMEPODSENDER_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HomePodSender");
    static string FilePath => Path.Combine(Dir, "config.json");

    public static AppConfig Load()
    {
        AppConfig cfg;
        try { cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig(); }
        catch { cfg = new AppConfig(); }
        if (string.IsNullOrEmpty(cfg.DeviceId))
            cfg.DeviceId = "02:" + string.Join(":", RandomNumberGenerator.GetBytes(5).Select(b => b.ToString("X2")));
        if (cfg.Devices.Count == 0 && string.IsNullOrEmpty(cfg.Ip))
        {
            // 首次运行：预置本机已知的 HomePod（立体声对的组长）
            cfg.Devices.Add(new DeviceEntry { Name = "起居室 (2)", Ip = "192.168.50.71", Role = "组长" });
            cfg.Ip = "192.168.50.71";
        }
        cfg.VolumePercent = Math.Clamp(cfg.VolumePercent, 0, 100);
        cfg.LatencyMs = Math.Clamp(cfg.LatencyMs, 250, 2000);
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 配置写不进去不影响使用 */ }
    }

    public ControllerSettings ToControllerSettings() => new()
    {
        Ip = Ip, VolumePercent = VolumePercent, LatencyMs = LatencyMs, AutoReconnect = AutoReconnect, DeviceId = DeviceId,
    };
}

/// <summary>简单文件日志（超过 512KB 时启动时裁剪保留末尾 256KB）</summary>
public static class Logger
{
    static readonly object Lock = new();
    static string Path_ => Path.Combine(AppConfig.Dir, "log.txt");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            var fi = new FileInfo(Path_);
            if (fi.Exists && fi.Length > 512 * 1024)
            {
                var bytes = File.ReadAllBytes(Path_);
                File.WriteAllBytes(Path_, bytes[^(256 * 1024)..]);
            }
        }
        catch { }
    }

    public static void Write(string line)
    {
        try { lock (Lock) File.AppendAllText(Path_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine); }
        catch { }
    }
}
