using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ap2Core;
using Forms = System.Windows.Forms;

namespace HomePodSender;

/// <summary>设备面板里的一行</summary>
public sealed record DeviceVm(string Name, string Ip, bool Selected, string Role)
{
    public bool HasRole => Role != "";
    public string RoleText => Role switch { "组长" => "立体声对 · 组长", "成员" => "立体声对 · 成员，请改连组长", _ => "" };
    public Brush RoleBrush => new SolidColorBrush(Role == "组长" ? Color.FromRgb(0xFF, 0x8A, 0x9A) : Color.FromRgb(0xFF, 0xB3, 0x40));
}

public partial class MainWindow : Window
{
    /// <summary>截图预览模式：不自动连接、不触碰设备、不启动动画</summary>
    public static bool PreviewMode;

    readonly AppConfig _cfg;
    readonly HomePodController _ctl;
    readonly Forms.NotifyIcon _tray = new();
    readonly Forms.ContextMenuStrip _menu = new();
    readonly Forms.ToolStripMenuItem _miShow = new("显示窗口"), _miConnect = new("连接"), _miDisconnect = new("断开"), _miExit = new("退出");
    readonly DispatcherTimer _barsTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    readonly Border[] _bars = new Border[28];
    readonly double[] _barPhase = new double[28];
    double _level, _t;
    bool _exiting, _updating, _ready;
    int _volBeforeMute = 20;
    CancellationTokenSource? _scanCts;
    DateTime? _streamStart;
    Border? _openSheet;
    TranslateTransform? _openShift;
    StreamState _shownState = StreamState.Disconnected;

    // ---------- 状态配色（液态玻璃：半透明、让背景色透出来） ----------
    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static readonly Color[] CoverIdle = { C("#33FFFFFF"), C("#14FFFFFF"), C("#0A000000") };
    static readonly Color[] CoverConn = { C("#4DD6DCFF"), C("#1FAAB6FF"), C("#0F101828") };
    static readonly Color[] CoverStream = { C("#FFFF7A9A"), C("#FFFA233B"), C("#FF8E0F33") };
    static readonly Color[] CoverFail = { C("#8CFF7A70"), C("#66C23A3A"), C("#4D2A1014") };

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _cfg = AppConfig.Load();
        _ctl = new HomePodController(_cfg.ToControllerSettings());
        _ctl.Changed += () => OnUi(() => UpdateUi());
        _ctl.LogMessage += m => { Logger.Write(m); OnUi(() => AppendLog(m)); };

        BuildBars();
        LoadConfigToUi();
        BuildTray();
        _barsTimer.Tick += (_, _) => AnimateBars();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };
        SourceInitialized += (_, _) => ApplyDwm();
        if (args.Contains("--offscreen")) { WindowStartupLocation = WindowStartupLocation.Manual; Left = -3000; Top = -3000; }
        if (!PreviewMode) StartBlobDrift();
        UpdateUi();
        _ready = true;
    }

    /// <summary>窗口创建完成后的启动动作（自动连接）</summary>
    public void OnReady()
    {
        if (!PreviewMode && _cfg.AutoConnectOnStart && !string.IsNullOrWhiteSpace(_cfg.Ip))
            _ = ConnectAsync();
    }

    // ---------- 窗口外观 ----------
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyDwm()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            int on = 1, round = 2, border = 0x3A3A3A;   // 深色模式、圆角、边框色(BGR)
            DwmSetWindowAttribute(h, 20, ref on, 4);
            DwmSetWindowAttribute(h, 33, ref round, 4);
            DwmSetWindowAttribute(h, 34, ref border, 4);
        }
        catch { /* 旧系统不支持则忽略 */ }
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>系统注销/关机前调用：标记为真正退出并尽快断开推流</summary>
    public void PrepareExit()
    {
        _exiting = true;
        _tray.Visible = false;
        try { _ctl.DisconnectAsync().Wait(1500); } catch { }
        _cfg.Save();
    }

    public void ForceClose() { _exiting = true; _tray.Visible = false; _tray.Dispose(); Close(); }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _openSheet != null) { CloseSheet(); e.Handled = true; }
    }

    // ---------- 配置 ----------
    void LoadConfigToUi()
    {
        _updating = true;
        VolSlider.Value = _cfg.VolumePercent;
        _volBeforeMute = Math.Max(1, _cfg.VolumePercent);
        ChkAuto.IsChecked = _cfg.AutoReconnect;
        ChkStart.IsChecked = _cfg.AutoConnectOnStart;
        (_cfg.LatencyMs switch { 500 => Lat500, 1000 => Lat1000, 2000 => Lat2000, _ => Lat1500 }).IsChecked = true;
        UpdateVolIcon();
        RefreshDeviceList();
        _updating = false;
    }

    void SaveSoon() { _saveTimer.Stop(); _saveTimer.Start(); }

    string DeviceNameOf(string? ip)
    {
        var d = _cfg.Devices.FirstOrDefault(x => x.Ip == ip);
        return d != null && !string.IsNullOrEmpty(d.Name) ? d.Name : "HomePod";
    }

    string CurrentDeviceName => DeviceNameOf(_cfg.Ip);
    string CurrentRole => _cfg.Devices.FirstOrDefault(x => x.Ip == _cfg.Ip)?.Role ?? "";

    void UpdateVolIcon() => VolLowIcon.Text = VolSlider.Value < 0.5 ? "" : "";

    // ---------- 声波可视化（封面内） ----------
    void BuildBars()
    {
        Bars.Children.Clear();
        for (int i = 0; i < _bars.Length; i++)
        {
            var b = new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.White, Opacity = 0.92 };
            Canvas.SetLeft(b, i * 7.5);
            Canvas.SetTop(b, (Bars.Height - 4) / 2);
            _bars[i] = b;
            Bars.Children.Add(b);
            _barPhase[i] = i * 0.55 + (i % 3) * 0.9;
        }
    }

    void SetBars(double lv)
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            double shape = 0.45 + 0.55 * Math.Sin(Math.PI * (i + 0.5) / _bars.Length);   // 两端低、中间高
            double wob = 0.5 + 0.5 * Math.Sin(_t * (1.3 + (i % 5) * 0.17) + _barPhase[i]);
            double h = 4 + (Bars.Height - 4) * lv * shape * (0.3 + 0.7 * wob);
            _bars[i].Height = h;
            Canvas.SetTop(_bars[i], (Bars.Height - h) / 2);
        }
    }

    void AnimateBars()
    {
        if (!IsVisible) return;
        float peak = _ctl.TakePeak();
        _level = Math.Max(_level * 0.9, peak);                  // 快速上升、缓慢衰减
        double db = 20 * Math.Log10(Math.Max(_level, 1e-5));
        double lv = Math.Clamp((db + 55) / 55, 0, 1);
        _t += 0.12;
        SetBars(lv);
        LevelFill.Width = Math.Max(0, LevelTrack.ActualWidth * lv);
    }

    void ResetBars() { _level = 0; SetBars(0); LevelFill.Width = 0; }

    void UpdateClock()
    {
        if (_streamStart is { } s)
        {
            var el = DateTime.Now - s;
            ElapsedText.Text = el.TotalHours >= 1 ? $"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}" : $"{el.Minutes:00}:{el.Seconds:00}";
        }
    }

    // ---------- 动画与配色 ----------
    void OnUi(Action a)
    {
        try { Dispatcher.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    void AnimateStop(GradientStop s, Color to)
    {
        if (PreviewMode) { s.Color = to; return; }
        s.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase() });
    }

    void SetCover(Color[] c)
    {
        AnimateStop(CoverS1, c[0]); AnimateStop(CoverS2, c[1]); AnimateStop(CoverS3, c[2]);
    }

    void SetBlobs(Color a, Color b, Color c, byte alpha)
    {
        AnimateStop(B1a, Color.FromArgb(alpha, a.R, a.G, a.B)); AnimateStop(B1b, Color.FromArgb(0, a.R, a.G, a.B));
        AnimateStop(B2a, Color.FromArgb(alpha, b.R, b.G, b.B)); AnimateStop(B2b, Color.FromArgb(0, b.R, b.G, b.B));
        AnimateStop(B3a, Color.FromArgb(alpha, c.R, c.G, c.B)); AnimateStop(B3b, Color.FromArgb(0, c.R, c.G, c.B));
    }

    // 背景光斑缓慢漂移（低帧率，几乎不占 CPU；窗口隐藏时 WPF 不会渲染）
    void StartBlobDrift()
    {
        void Drift(TranslateTransform t, double dx, double dy, double sec)
        {
            var ax = new DoubleAnimation(-dx, dx, TimeSpan.FromSeconds(sec)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var ay = new DoubleAnimation(-dy, dy, TimeSpan.FromSeconds(sec * 1.31)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(ax, 20);
            Timeline.SetDesiredFrameRate(ay, 20);
            t.BeginAnimation(TranslateTransform.XProperty, ax);
            t.BeginAnimation(TranslateTransform.YProperty, ay);
        }
        Drift(B1T, 50, 40, 11);
        Drift(B2T, -60, 50, 13);
        Drift(B3T, 45, -45, 15);
    }

    void StartBreath()
    {
        if (PreviewMode) return;
        var a = new DoubleAnimation(1.0, 1.018, TimeSpan.FromSeconds(2.4)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        Timeline.SetDesiredFrameRate(a, 30);
        CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void StopBreath()
    {
        CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CoverScale.ScaleX = CoverScale.ScaleY = 1;
    }

    void MoveIcon(double top)
    {
        var to = new Thickness(0, top, 0, 0);
        if (PreviewMode) { CoverIcon.Margin = to; return; }
        CoverIcon.BeginAnimation(MarginProperty, new ThicknessAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void StartPlaySpinner()
    {
        PlaySpinner.Visibility = Visibility.Visible;
        ShowPlayShape(2);
        if (PreviewMode) return;
        PlaySpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.0)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    void StopPlaySpinner()
    {
        PlaySpin.BeginAnimation(RotateTransform.AngleProperty, null);
        PlaySpinner.Visibility = Visibility.Collapsed;
    }

    /// <summary>0=播放三角（连接） 1=暂停（断开） 2=不显示（连接中显示转圈）</summary>
    void ShowPlayShape(int mode)
    {
        PlayShape.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseShape.Visibility = mode == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetIconBrush(Brush b) { IconArcs.Stroke = b; IconTri.Fill = b; IconTri.Stroke = b; }

    Brush Res(string key) => (Brush)FindResource(key);

    // ---------- 状态刷新 ----------
    public void UpdateUi(StreamState? previewState = null, string? previewStatus = null)
    {
        var st = previewState ?? _ctl.State;
        var status = previewStatus ?? _ctl.Status;
        bool failed = st == StreamState.Disconnected && (status.StartsWith("连接失败") || status.StartsWith("连接已断开") || status.StartsWith("重连失败"));
        string dev = CurrentDeviceName;
        System.Drawing.Color trayColor;

        switch (st)
        {
            case StreamState.Streaming:
                StopPlaySpinner();
                SetCover(CoverStream);
                SetBlobs(C("#FF2D55"), C("#FF7A45"), C("#C13BFF"), 0x7E);
                SetIconBrush(Brushes.White);
                MoveIcon(38);
                Bars.Visibility = Visibility.Visible;
                TitleText.Text = "电脑的声音";
                SubText.Text = dev + (CurrentRole == "组长" ? " · 立体声对" : "") + " · 实时";
                SubText.Foreground = Res("Text2Brush");
                ShowPlayShape(1);                          // 暂停图标 = 断开
                LiveBadge.Visibility = Visibility.Visible;
                if (_streamStart == null) _streamStart = DateTime.Now;
                if (_shownState != StreamState.Streaming) StartBreath();
                if (!PreviewMode) { _barsTimer.Start(); _clockTimer.Start(); }
                UpdateClock();
                DevicesButton.Foreground = new SolidColorBrush(C("#FF5C6E"));
                trayColor = Icons.Green;
                break;
            case StreamState.Connecting:
            case StreamState.Reconnecting:
                StopBreath(); _barsTimer.Stop(); _clockTimer.Stop(); ResetBars(); _streamStart = null;
                SetCover(CoverConn);
                SetBlobs(C("#5B6CFF"), C("#8F6BFF"), C("#4AC8FF"), 0x66);
                SetIconBrush(new SolidColorBrush(C("#E6E8F5")));
                MoveIcon(84);
                Bars.Visibility = Visibility.Collapsed;
                TitleText.Text = st == StreamState.Connecting ? "正在连接…" : "重新连接中…";
                SubText.Text = st == StreamState.Connecting ? dev : status;
                SubText.Foreground = Res("Text2Brush");
                StartPlaySpinner();
                LiveBadge.Visibility = Visibility.Hidden;
                ElapsedText.Text = "--:--";
                DevicesButton.ClearValue(ForegroundProperty);
                trayColor = Icons.Orange;
                break;
            default:
                StopPlaySpinner(); StopBreath(); _barsTimer.Stop(); _clockTimer.Stop(); ResetBars(); _streamStart = null;
                SetCover(failed ? CoverFail : CoverIdle);
                SetBlobs(failed ? C("#FF453A") : C("#4F63FF"), failed ? C("#9E2A4A") : C("#A45CFF"), failed ? C("#5A2A3A") : C("#1FB6C8"), failed ? (byte)0x5C : (byte)0x6C);
                SetIconBrush(failed ? new SolidColorBrush(C("#FFC9C3")) : new SolidColorBrush(C("#F2FFFFFF")));
                MoveIcon(84);
                Bars.Visibility = Visibility.Collapsed;
                TitleText.Text = "未在播放";
                SubText.Text = failed ? status : "点击 ▶ 推流到 " + dev;
                SubText.Foreground = failed ? Res("ErrBrush") : Res("Text2Brush");
                ShowPlayShape(0);                          // 播放图标 = 连接
                LiveBadge.Visibility = Visibility.Hidden;
                ElapsedText.Text = "--:--";
                DevicesButton.ClearValue(ForegroundProperty);
                trayColor = failed ? Icons.Red : Icons.Gray;
                break;
        }
        _shownState = st;

        AutomationProperties.SetName(PlayButton, st is StreamState.Streaming or StreamState.Reconnecting ? "断开" : "连接");
        AutomationProperties.SetName(TitleText, "状态: " + status);
        PlayButton.IsEnabled = st != StreamState.Connecting;
        ScanButton.IsEnabled = _scanCts == null;
        _miConnect.Enabled = st == StreamState.Disconnected;
        _miDisconnect.Enabled = st is StreamState.Streaming or StreamState.Reconnecting;
        var trayIcon = Icons.Make(trayColor);
        _tray.Icon = trayIcon;
        try { Icon = Imaging.CreateBitmapSourceFromHIcon(trayIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); } catch { }
        string tip = "HomePod 推流：" + status;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>截图预览：按指定状态摆好界面</summary>
    public void ApplyPreview(StreamState st, string status, double level, string sheet, bool log)
    {
        UpdateUi(st, status);
        _t = 3.1;
        double lv = st == StreamState.Streaming ? level : 0;
        SetBars(lv);
        UpdateLayout();
        LevelFill.Width = Math.Max(0, LevelTrack.ActualWidth * lv * 0.72);
        if (st == StreamState.Streaming) ElapsedText.Text = "03:27";
        if (log)
        {
            ChkLog.IsChecked = true;
            LogText.Text = "[  0.13s] TCP 已连接 192.168.50.71:7000\n[  0.21s] 配对完成：服务端证明校验通过\n[  0.47s] 流 SETUP -> 200\n[  0.52s] 开始推流";
        }
        if (sheet == "devices")
        {
            _cfg.Devices.Clear();
            _cfg.Devices.Add(new DeviceEntry { Name = "起居室 (2)", Ip = "192.168.50.71", Role = "组长", PairId = "pair1" });
            _cfg.Devices.Add(new DeviceEntry { Name = "起居室", Ip = "192.168.50.23", Role = "成员", PairId = "pair1" });
            _cfg.Ip = "192.168.50.71";
            RefreshDeviceList();
            ScanStatus.Text = "找到 2 台设备";
            OpenSheet(DevicesSheet, DevicesShift);
        }
        else if (sheet == "settings") OpenSheet(SettingsSheet, SettingsShift);
    }

    // ---------- 播放按钮 ----------
    void ScalePlay(double to, double ms)
    {
        if (PreviewMode) return;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        PlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        PlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void Play_MouseEnter(object sender, MouseEventArgs e) => ScalePlay(1.06, 150);
    void Play_MouseLeave(object sender, MouseEventArgs e) => ScalePlay(1.0, 170);

    async void Play_Click(object sender, RoutedEventArgs e)
    {
        switch (_ctl.State)
        {
            case StreamState.Disconnected: await ConnectAsync(); break;
            case StreamState.Streaming:
            case StreamState.Reconnecting: await _ctl.DisconnectAsync(); break;
        }
    }

    async Task ConnectAsync()
    {
        if (_ctl.State != StreamState.Disconnected) return;
        if (string.IsNullOrWhiteSpace(_cfg.Ip) || !IPAddress.TryParse(_cfg.Ip, out _))
        {
            TitleText.Text = "未在播放";
            SubText.Text = "请先点底部的 AirPlay 图标选择或添加 HomePod";
            SubText.Foreground = Res("ErrBrush");
            return;
        }
        _ctl.Settings.Ip = _cfg.Ip;
        _cfg.Save();
        try { await _ctl.ConnectAsync(); }
        catch { /* 状态与原因已由控制器写入 */ }
    }

    // ---------- 底部面板 ----------
    void OpenSheet(Border sheet, TranslateTransform shift)
    {
        if (_openSheet != null) return;
        _openSheet = sheet;
        _openShift = shift;
        Overlay.Visibility = Visibility.Visible;
        sheet.Visibility = Visibility.Visible;
        sheet.UpdateLayout();
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        Scrim.BeginAnimation(OpacityProperty, null);
        if (PreviewMode) { Scrim.Opacity = 1; shift.Y = 0; return; }
        shift.Y = Math.Max(sheet.ActualHeight, 300) + 30;
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(340)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void CloseSheet()
    {
        var sheet = _openSheet;
        var shift = _openShift;
        if (sheet == null || shift == null) return;
        _openSheet = null;
        _openShift = null;
        if (PreviewMode) { sheet.Visibility = Visibility.Collapsed; Overlay.Visibility = Visibility.Collapsed; return; }
        var down = new DoubleAnimation(sheet.ActualHeight + 40, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        down.Completed += (_, _) =>
        {
            if (_openSheet == null) { sheet.Visibility = Visibility.Collapsed; Overlay.Visibility = Visibility.Collapsed; }
        };
        shift.BeginAnimation(TranslateTransform.YProperty, down);
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)));
    }

    void Scrim_Down(object sender, MouseButtonEventArgs e) => CloseSheet();
    void CloseSheet_Click(object sender, RoutedEventArgs e) => CloseSheet();
    void Devices_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceList();
        OpenSheet(DevicesSheet, DevicesShift);
        if (!PreviewMode) _ = RefreshDeviceInfoAsync();
    }

    /// <summary>对已保存的设备各发一次只读的 /info，更新名称与立体声对角色（不配对、不出声）</summary>
    async Task RefreshDeviceInfoAsync()
    {
        var list = _cfg.Devices.ToList();
        var found = await Task.WhenAll(list.Select(d => Discovery.ProbeAsync(d.Ip, timeoutMs: 700)));
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (found[i] is not { } f) continue;
            if (list[i].Name != f.Name || list[i].Role != f.Role || list[i].PairId != f.PairId) changed = true;
            list[i].Name = f.Name; list[i].Role = f.Role; list[i].PairId = f.PairId;
        }
        if (changed) { _cfg.Save(); RefreshDeviceList(); UpdateUi(); }
    }
    void Settings_Click(object sender, RoutedEventArgs e) => OpenSheet(SettingsSheet, SettingsShift);

    // ---------- 设备列表 ----------
    void RefreshDeviceList()
    {
        DeviceItems.ItemsSource = _cfg.Devices
            .Select(d => new DeviceVm(string.IsNullOrEmpty(d.Name) ? "HomePod" : d.Name, d.Ip, d.Ip == _cfg.Ip, d.Role))
            .ToList();
    }

    async void Device_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string ip }) await SelectDeviceAsync(ip);
    }

    /// <summary>选择目标设备；正在推流时会先断开再连到新设备</summary>
    async Task SelectDeviceAsync(string ip)
    {
        // 立体声对的成员自己出不了声：已知同一对的组长时，自动改选组长
        var picked = _cfg.Devices.FirstOrDefault(d => d.Ip == ip);
        if (picked is { Role: "成员" } && !string.IsNullOrEmpty(picked.PairId))
        {
            var leader = _cfg.Devices.FirstOrDefault(d => d.Role == "组长" && d.PairId == picked.PairId);
            if (leader != null) ip = leader.Ip;
        }
        bool active = _ctl.State != StreamState.Disconnected;
        if (ip == _cfg.Ip) { CloseSheet(); return; }
        if (active) await _ctl.DisconnectAsync();
        _cfg.Ip = ip;
        _ctl.Settings.Ip = ip;
        _cfg.Save();
        RefreshDeviceList();
        UpdateUi();
        CloseSheet();
        if (active) await ConnectAsync();
    }

    void NewIp_TextChanged(object sender, TextChangedEventArgs e) => NewIpHint.Visibility = NewIpBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void NewIp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AddIp_Click(sender, e); e.Handled = true; }
    }

    async void AddIp_Click(object sender, RoutedEventArgs e)
    {
        var m = Regex.Match(NewIpBox.Text.Trim(), @"(\d{1,3}(?:\.\d{1,3}){3})");
        if (!m.Success || !IPAddress.TryParse(m.Groups[1].Value, out _))
        {
            ScanStatus.Text = "请输入有效的 IP 地址";
            return;
        }
        string ip = m.Groups[1].Value;
        if (_cfg.Devices.All(d => d.Ip != ip)) _cfg.Devices.Add(new DeviceEntry { Name = "", Ip = ip });
        _cfg.Save();
        NewIpBox.Text = "";
        ScanStatus.Text = "";
        // 顺便读一下名称
        _ = Task.Run(async () =>
        {
            var f = await Discovery.ProbeAsync(ip);
            if (f != null) OnUi(() => { var d = _cfg.Devices.First(x => x.Ip == ip); d.Name = f.Name; d.Role = f.Role; d.PairId = f.PairId; _cfg.Save(); RefreshDeviceList(); UpdateUi(); });
        });
        await SelectDeviceAsync(ip);
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        ScanButton.Content = "扫描中…";
        ScanStatus.Text = "";
        try
        {
            var found = await Task.Run(() => Discovery.ScanAsync(cts.Token, new Progress<int>(p => OnUi(() => ScanStatus.Text = p + "%"))));
            if (found.Count == 0) ScanStatus.Text = "没有找到设备";
            else
            {
                foreach (var f in found)
                {
                    var ex = _cfg.Devices.FirstOrDefault(d => d.Ip == f.Ip);
                    if (ex == null) _cfg.Devices.Add(new DeviceEntry { Name = f.Name, Ip = f.Ip, Role = f.Role, PairId = f.PairId }); else { ex.Name = f.Name; ex.Role = f.Role; ex.PairId = f.PairId; }
                }
                _cfg.Save();
                RefreshDeviceList();
                UpdateUi();
                ScanStatus.Text = "找到 " + found.Count + " 台设备";
            }
        }
        finally
        {
            if (_scanCts == cts) _scanCts = null;
            ScanButton.Content = "扫描局域网设备";
            ScanButton.IsEnabled = true;
        }
    }

    // ---------- 音量 ----------
    void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _updating) return;
        int v = (int)Math.Round(VolSlider.Value);
        UpdateVolIcon();
        if (v > 0) _volBeforeMute = v;
        _cfg.VolumePercent = v;
        _ctl.SetVolumePercent(v);
        SaveSoon();
    }

    void Mute_Click(object sender, RoutedEventArgs e) => VolSlider.Value = VolSlider.Value >= 0.5 ? 0 : _volBeforeMute;

    // ---------- 设置 ----------
    void Latency_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating || sender is not RadioButton rb || !int.TryParse(rb.Tag as string, out int ms)) return;
        _cfg.LatencyMs = ms;
        _ctl.Settings.LatencyMs = ms;
        _cfg.Save();
    }

    void Opt_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        _cfg.AutoReconnect = ChkAuto.IsChecked == true;
        _ctl.Settings.AutoReconnect = _cfg.AutoReconnect;
        _cfg.AutoConnectOnStart = ChkStart.IsChecked == true;
        _cfg.Save();
    }

    void Log_Toggled(object sender, RoutedEventArgs e) => LogBox.Visibility = ChkLog.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    void AppendLog(string m)
    {
        if (LogText.Text.Length > 60000) LogText.Text = LogText.Text[^30000..];
        LogText.AppendText(m + "\n");
        LogText.ScrollToEnd();
    }

    // ---------- 托盘 ----------
    void BuildTray()
    {
        _miShow.Click += (_, _) => ShowFromTray();
        _miConnect.Click += async (_, _) => await ConnectAsync();
        _miDisconnect.Click += async (_, _) => await _ctl.DisconnectAsync();
        _miExit.Click += async (_, _) => await ExitAppAsync();
        _menu.Items.AddRange(new Forms.ToolStripItem[] { _miShow, new Forms.ToolStripSeparator(), _miConnect, _miDisconnect, new Forms.ToolStripSeparator(), _miExit });
        _tray.ContextMenuStrip = _menu;
        _tray.Icon = Icons.Make(Icons.Gray);
        _tray.Text = "HomePod 推流：未连接";
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = !PreviewMode;
    }

    async Task ExitAppAsync()
    {
        _exiting = true;
        _tray.Visible = false;
        try { await Task.WhenAny(_ctl.DisconnectAsync(), Task.Delay(3000)); } catch { }
        _cfg.Save();
        _ctl.Dispose();
        _tray.Dispose();
        Application.Current.Shutdown();
    }
}
