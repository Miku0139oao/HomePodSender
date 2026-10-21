using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ap2Core;

namespace HomePodSender;

public partial class App : Application
{
    public string[] Args = Array.Empty<string>();
    MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _window = new MainWindow(Args);
        MainWindow = _window;
        // 注销/关机时不要拦截：先礼貌断开 HomePod 再放行
        SessionEnding += (_, _) => _window?.PrepareExit();
        if (!Args.Contains("--tray")) _window.Show();
        _window.OnReady();
    }

    /// <summary>另一个实例启动时，通知本实例把窗口显示出来</summary>
    public void ShowMainWindow() => Dispatcher.BeginInvoke(() => _window?.ShowFromTray());

    /// <summary>截图预览：离屏渲染窗口到 PNG（2 倍分辨率），不连接任何设备</summary>
    public int RunScreenshot(string path, string[] args)
    {
        string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        var w = new MainWindow(new[] { "--offscreen" });
        w.Show();
        StreamState st = StreamState.Disconnected;
        string status = "未连接";
        switch (Opt("--state"))
        {
            case "streaming": st = StreamState.Streaming; status = "已连接，正在播放系统声音"; break;
            case "connecting": st = StreamState.Connecting; status = "正在连接 192.168.50.71 …"; break;
            case "failed": st = StreamState.Disconnected; status = "连接失败：HomePod 无响应，请检查 IP 与网络"; break;
            case "reconnecting": st = StreamState.Reconnecting; status = "连接中断，正在重连（第 2 次）…"; break;
        }
        double level = double.TryParse(Opt("--level"), System.Globalization.CultureInfo.InvariantCulture, out var lv) ? lv : 0.7;
        w.ApplyPreview(st, status, level, Opt("--sheet"), args.Contains("--log"));
        w.UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        double scale = 2;
        var rtb = new RenderTargetBitmap((int)(w.ActualWidth * scale), (int)(w.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        w.ForceClose();
        return 0;
    }
}

public static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        int si = Array.IndexOf(args, "--screenshot");
        if (si >= 0 && si + 1 < args.Length)
        {
            MainWindow.PreviewMode = true;
            var pa = new App();
            pa.InitializeComponent();
            return pa.RunScreenshot(args[si + 1], args);
        }

        // 单实例：再次启动时只通知已运行的实例显示窗口
        using var mutex = new Mutex(true, @"Local\HomePodSender.Single", out bool first);
        var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HomePodSender.Show");
        if (!first) { showEvt.Set(); return 0; }

        Logger.Init();
        Logger.Write("程序启动");
        var app = new App { Args = args };
        app.InitializeComponent();
        var t = new Thread(() => { while (showEvt.WaitOne()) app.ShowMainWindow(); }) { IsBackground = true };
        t.Start();
        int rc = app.Run();
        Logger.Write("程序退出");
        return rc;
    }
}
