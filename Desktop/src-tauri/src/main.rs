#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod orb;

use serde_json::{json, Value};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Emitter, Manager, State, WebviewUrl, WebviewWindowBuilder, WindowEvent};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

// ---------- 面板几何（与前端 src/layout.js 保持一致，单位：逻辑像素） ----------
const WIN_W: f64 = 468.0;
const WIN_H: f64 = 404.0;
const ORB_X: f64 = 342.0;
const ORB_Y: f64 = 110.0;
const ORB_SIZE: f64 = 132.0;
const TRAY_PX: u32 = 32;

struct SidecarState {
    stdin: Mutex<Option<ChildStdin>>,
    child: Mutex<Option<Child>>,
    pending: Arc<Mutex<HashMap<u64, tokio::sync::oneshot::Sender<Result<Value, String>>>>>,
    next_id: AtomicU64,
}

struct TrayItems {
    connect: MenuItem<tauri::Wry>,
    disconnect: MenuItem<tauri::Wry>,
    tray: TrayIcon<tauri::Wry>,
}

/// 面板与托盘的共享状态
struct UiState {
    visible: AtomicBool,
    hiding: AtomicBool,
    last_hide: Mutex<Instant>,
    /// 0 = 未播放，1 = 连接中/重连中，2 = 播放中
    stream: AtomicU8,
    /// 最近的声音电平（f32 位模式）
    level: AtomicU32,
    intro_running: AtomicBool,
}

impl UiState {
    fn new() -> Self {
        Self {
            visible: AtomicBool::new(false),
            hiding: AtomicBool::new(false),
            last_hide: Mutex::new(Instant::now() - Duration::from_secs(10)),
            stream: AtomicU8::new(0),
            level: AtomicU32::new(0f32.to_bits()),
            intro_running: AtomicBool::new(false),
        }
    }
}

// ======================= 核心进程（sidecar） =======================

fn sidecar_path(app: &AppHandle) -> PathBuf {
    if let Ok(p) = std::env::var("HPS_SIDECAR") {
        return PathBuf::from(p);
    }
    if cfg!(debug_assertions) {
        return PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../Sidecar/bin/Debug/net8.0-windows/HomePodSidecar.exe");
    }
    app.path()
        .resource_dir()
        .unwrap_or_default()
        .join("sidecar/HomePodSidecar.exe")
}

fn empty_sidecar() -> SidecarState {
    SidecarState {
        stdin: Mutex::new(None),
        child: Mutex::new(None),
        pending: Arc::new(Mutex::new(HashMap::new())),
        next_id: AtomicU64::new(1),
    }
}

fn start_sidecar(app: &AppHandle) -> SidecarState {
    let path = sidecar_path(app);
    let mut cmd = Command::new(&path);
    cmd.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    cmd.creation_flags(0x08000000); // CREATE_NO_WINDOW

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            let _ = app.emit("sidecar:event", json!({ "ev": "error", "message": format!("无法启动核心进程：{e}") }));
            return empty_sidecar();
        }
    };

    let stdin = child.stdin.take();
    let stdout = child.stdout.take().expect("sidecar stdout");
    let stderr = child.stderr.take().expect("sidecar stderr");

    let pending: Arc<Mutex<HashMap<u64, tokio::sync::oneshot::Sender<Result<Value, String>>>>> =
        Arc::new(Mutex::new(HashMap::new()));
    let pending_reader = Arc::clone(&pending);

    let app_reader = app.clone();
    std::thread::spawn(move || {
        for line in BufReader::new(stdout).lines() {
            let Ok(line) = line else { break };
            let Ok(value) = serde_json::from_str::<Value>(&line) else { continue };
            if let Some(ev) = value.get("ev").and_then(Value::as_str) {
                match ev {
                    "state" | "ready" => {
                        let state = value.get("state").and_then(Value::as_str).unwrap_or("").to_string();
                        let status = value.get("status").and_then(Value::as_str).unwrap_or("").to_string();
                        update_tray_state(&app_reader, &state, &status);
                    }
                    "level" => {
                        if let Some(v) = value.get("value").and_then(Value::as_f64) {
                            if let Some(ui) = app_reader.try_state::<UiState>() {
                                ui.level.store((v as f32).to_bits(), Ordering::Relaxed);
                            }
                        }
                    }
                    _ => {}
                }
                let _ = app_reader.emit("sidecar:event", value);
            } else if let Some(id) = value.get("id").and_then(Value::as_u64) {
                let ok = value.get("ok").and_then(Value::as_bool).unwrap_or(false);
                let payload = value.get("payload").cloned().unwrap_or(Value::Null);
                if let Some(tx) = pending_reader.lock().unwrap().remove(&id) {
                    let result = if ok {
                        Ok(payload)
                    } else {
                        Err(payload.get("error").and_then(Value::as_str).unwrap_or("操作失败").to_string())
                    };
                    let _ = tx.send(result);
                }
            }
        }
        let _ = app_reader.emit("sidecar:event", json!({ "ev": "error", "message": "核心进程已退出" }));
    });

    std::thread::spawn(move || {
        for line in BufReader::new(stderr).lines().flatten() {
            eprintln!("[sidecar] {line}");
        }
    });

    SidecarState {
        stdin: Mutex::new(stdin),
        child: Mutex::new(Some(child)),
        pending,
        next_id: AtomicU64::new(1),
    }
}

fn write_message(state: &SidecarState, cmd: &str, payload: Value, id: u64) -> Result<(), String> {
    let mut msg = if payload.is_object() { payload } else { json!({}) };
    msg["id"] = json!(id);
    msg["cmd"] = json!(cmd);
    let line = serde_json::to_string(&msg).map_err(|e| e.to_string())? + "\n";
    let mut guard = state.stdin.lock().unwrap();
    let stdin = guard.as_mut().ok_or("核心进程尚未就绪")?;
    stdin.write_all(line.as_bytes()).map_err(|e| e.to_string())?;
    let _ = stdin.flush();
    Ok(())
}

fn write_fire(app: &AppHandle, cmd: &str, payload: Value) {
    if let Some(state) = app.try_state::<SidecarState>() {
        let id = state.next_id.fetch_add(1, Ordering::Relaxed);
        let _ = write_message(&state, cmd, payload, id);
    }
}

// ======================= 前端可调用的命令 =======================

#[tauri::command]
async fn sidecar_call(cmd: String, payload: Option<Value>, state: State<'_, SidecarState>) -> Result<Value, String> {
    let id = state.next_id.fetch_add(1, Ordering::Relaxed);
    let (tx, rx) = tokio::sync::oneshot::channel();
    state.pending.lock().unwrap().insert(id, tx);
    write_message(&state, &cmd, payload.unwrap_or_else(|| json!({})), id)?;
    match tokio::time::timeout(Duration::from_secs(35), rx).await {
        Ok(Ok(Ok(value))) => Ok(value),
        Ok(Ok(Err(message))) => Err(message),
        Ok(Err(_)) => Err("核心进程已退出".into()),
        Err(_) => {
            state.pending.lock().unwrap().remove(&id);
            Err("操作超时".into())
        }
    }
}

/// 调试版打印带墙钟毫秒的时间点，便于和前端 Date.now() 对齐排查动画衔接
fn trace(what: &str) {
    if cfg!(debug_assertions) {
        let ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0);
        eprintln!("[ui] {what} @{ms}");
    }
}

/// 启动动画开始飞向右下角：提前把面板窗口显示出来（页面处于待命态，全透明），
/// 这样落地时不用再等隐藏窗口重新显示的几百毫秒，光球能无缝交接
#[tauri::command]
fn intro_near(app: AppHandle) {
    trace("intro_near");
    let Some(w) = app.get_webview_window("main") else { return };
    let (x, y) = panel_origin(&app);
    let _ = w.set_position(tauri::LogicalPosition::new(x, y));
    let _ = w.show();
    trace("panel pre-shown");
}

/// 启动动画落地：面板光球接替动画光球，板从光球处绽开；稍后关闭动画窗口
#[tauri::command]
fn intro_done(app: AppHandle) {
    trace("intro_done");
    if let Some(ui) = app.try_state::<UiState>() {
        ui.intro_running.store(false, Ordering::Relaxed);
    }
    show_panel_with(&app, "land");
    let handle = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(450));
        if let Some(w) = handle.get_webview_window("intro") {
            let _ = w.close();
        }
    });
}

/// 面板收起动画播完，真正隐藏窗口
#[tauri::command]
fn panel_hidden(app: AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.hide();
    }
    if let Some(ui) = app.try_state::<UiState>() {
        ui.visible.store(false, Ordering::Relaxed);
        ui.hiding.store(false, Ordering::Relaxed);
    }
}

/// 面板页加载完成后查询：若 Rust 已经把窗口显示出来（“show” 事件可能早于页面监听），直接展开
#[tauri::command]
fn panel_visible(app: AppHandle) -> bool {
    app.try_state::<UiState>()
        .map(|ui| ui.visible.load(Ordering::Relaxed) && !ui.hiding.load(Ordering::Relaxed))
        .unwrap_or(false)
}

#[tauri::command]
fn open_logs() {
    open_log_dir();
}

#[tauri::command]
fn quit_app(app: AppHandle) {
    app.exit(0);
}

fn data_dir() -> PathBuf {
    if let Ok(p) = std::env::var("HOMEPODSENDER_DATA") {
        return PathBuf::from(p);
    }
    PathBuf::from(std::env::var("APPDATA").unwrap_or_default()).join("HomePodSender")
}

fn open_log_dir() {
    let dir = data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let _ = Command::new("explorer").arg(dir).spawn();
}

// ======================= 面板显示 / 收起 =======================

/// 主显示器工作区（去掉任务栏）的逻辑坐标：(x, y, w, h, 缩放)
fn work_area(app: &AppHandle) -> Option<(f64, f64, f64, f64, f64)> {
    let m = app.primary_monitor().ok().flatten()?;
    let s = m.scale_factor();
    let wa = m.work_area();
    Some((
        wa.position.x as f64 / s,
        wa.position.y as f64 / s,
        wa.size.width as f64 / s,
        wa.size.height as f64 / s,
        s,
    ))
}

/// 面板窗口左上角：贴住工作区右下角（窗口自带 22px 透明边距，看起来离任务栏与屏幕边缘各 22px）
fn panel_origin(app: &AppHandle) -> (f64, f64) {
    match work_area(app) {
        Some((x, y, w, h, _)) => (x + w - WIN_W, y + h - WIN_H),
        None => (200.0, 200.0),
    }
}

fn show_panel(app: &AppHandle) {
    show_panel_with(app, "show");
}

/// 显示面板并通知前端展开：“show” = 托盘/唤出（光球弹出后绽开），“land” = 启动动画落地（光球直接接替）
fn show_panel_with(app: &AppHandle, how: &str) {
    let Some(w) = app.get_webview_window("main") else { return };
    let (x, y) = panel_origin(app);
    let _ = w.set_position(tauri::LogicalPosition::new(x, y));
    let _ = w.show();
    let _ = w.set_focus();
    if let Some(ui) = app.try_state::<UiState>() {
        ui.visible.store(true, Ordering::Relaxed);
        ui.hiding.store(false, Ordering::Relaxed);
    }
    let _ = app.emit_to("main", "panel", how);
    trace(&format!("panel {how}"));
}

fn request_hide(app: &AppHandle) {
    let Some(ui) = app.try_state::<UiState>() else { return };
    if !ui.visible.load(Ordering::Relaxed) || ui.hiding.swap(true, Ordering::Relaxed) {
        return;
    }
    *ui.last_hide.lock().unwrap() = Instant::now();
    let _ = app.emit_to("main", "panel", "hide");
    // 兜底：前端若没有回应，700ms 后直接隐藏
    let handle = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(700));
        if let Some(ui) = handle.try_state::<UiState>() {
            if ui.hiding.load(Ordering::Relaxed) {
                panel_hidden(handle.clone());
            }
        }
    });
}

fn toggle_panel(app: &AppHandle) {
    let Some(ui) = app.try_state::<UiState>() else { return };
    if ui.intro_running.load(Ordering::Relaxed) {
        return;
    }
    if ui.visible.load(Ordering::Relaxed) && !ui.hiding.load(Ordering::Relaxed) {
        request_hide(app);
        return;
    }
    // 刚因失焦收起（点击托盘图标本身就会让面板失焦），这次点击视为“关闭”
    if ui.last_hide.lock().unwrap().elapsed() < Duration::from_millis(400) {
        return;
    }
    show_panel(app);
}

// ======================= 托盘 =======================

fn update_tray_state(app: &AppHandle, state: &str, status: &str) {
    if let Some(ui) = app.try_state::<UiState>() {
        let v = match state {
            "Streaming" => 2,
            "Connecting" | "Reconnecting" => 1,
            _ => 0,
        };
        ui.stream.store(v, Ordering::Relaxed);
    }
    if let Some(items) = app.try_state::<TrayItems>() {
        let _ = items.connect.set_enabled(state == "Disconnected");
        let _ = items.disconnect.set_enabled(state == "Streaming" || state == "Reconnecting");
        let tip: String = format!("HomePod 推流：{status}").chars().take(100).collect();
        let _ = items.tray.set_tooltip(Some(&tip));
    }
}

fn orb_icon(color: f32, energy: f32, time: f32) -> tauri::image::Image<'static> {
    let rgba = orb::render(TRAY_PX, orb::OrbParams { color, energy, time, radius: 0.86, icon: true });
    tauri::image::Image::new_owned(rgba, TRAY_PX, TRAY_PX)
}

/// 托盘图标动画：未播放 = 静态透明白球；连接中 = 半彩色缓动；播放中 = 随电平滚动的彩色光球
fn spawn_tray_animator(app: AppHandle) {
    std::thread::spawn(move || {
        let mut last_idle = false;
        let mut energy = 0.0f32;
        loop {
            std::thread::sleep(Duration::from_millis(80));
            let (Some(ui), Some(items)) = (app.try_state::<UiState>(), app.try_state::<TrayItems>()) else { continue };
            let stream = ui.stream.load(Ordering::Relaxed);
            if stream == 0 {
                if !last_idle {
                    let _ = items.tray.set_icon(Some(orb_icon(0.0, 0.0, 1.7)));
                    last_idle = true;
                }
                energy = 0.0;
                continue;
            }
            last_idle = false;
            let level = f32::from_bits(ui.level.load(Ordering::Relaxed));
            let target = if stream == 2 {
                let db = 20.0 * level.max(1e-5).log10();
                ((db + 50.0) / 50.0).clamp(0.0, 1.0)
            } else {
                0.15
            };
            energy += (target - energy) * if target > energy { 0.5 } else { 0.15 };
            let color = if stream == 2 { 1.0 } else { 0.55 };
            let _ = items.tray.set_icon(Some(orb_icon(color, energy, orb::clock())));
        }
    });
}

fn create_tray(app: &tauri::App) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "显示面板", true, None::<&str>)?;
    let connect = MenuItem::with_id(app, "connect", "连接", true, None::<&str>)?;
    let disconnect = MenuItem::with_id(app, "disconnect", "断开", false, None::<&str>)?;
    let logs = MenuItem::with_id(app, "logs", "打开日志文件夹", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let sep1 = PredefinedMenuItem::separator(app)?;
    let sep2 = PredefinedMenuItem::separator(app)?;
    let menu = Menu::with_items(app, &[&show, &sep1, &connect, &disconnect, &logs, &sep2, &quit])?;

    let tray = TrayIconBuilder::with_id("main")
        .icon(orb_icon(0.0, 0.0, 1.7))
        .tooltip("HomePod 推流")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_panel(app),
            "connect" => write_fire(app, "connect", json!({})),
            "disconnect" => write_fire(app, "disconnect", json!({})),
            "logs" => open_log_dir(),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                toggle_panel(tray.app_handle());
            }
        })
        .build(app)?;

    app.manage(TrayItems { connect, disconnect, tray });
    Ok(())
}

/// 隔离模式下给窗口指定单独的 WebView2 数据目录（不与已安装的程序共用浏览器进程）
fn with_data_dir<'a, R: tauri::Runtime, M: Manager<R>>(
    b: WebviewWindowBuilder<'a, R, M>,
    dir: &Option<PathBuf>,
) -> WebviewWindowBuilder<'a, R, M> {
    match dir {
        Some(d) => b.data_directory(d.clone()),
        None => b,
    }
}

// ======================= 截图 / 预览参数 =======================

fn arg_value(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned()
}

fn preview_config(args: &[String]) -> Option<Value> {
    if !args.iter().any(|a| a == "--screenshot") {
        return None;
    }
    let level = arg_value(args, "--level").and_then(|v| v.parse::<f64>().ok()).unwrap_or(0.7);
    let volume = arg_value(args, "--volume").and_then(|v| v.parse::<i64>().ok()).unwrap_or(32);
    let t = arg_value(args, "--t").and_then(|v| v.parse::<f64>().ok()).unwrap_or(-1.0);
    Some(json!({
        "preview": arg_value(args, "--state").unwrap_or_else(|| "idle".into()),
        "view": arg_value(args, "--view").unwrap_or_else(|| "panel".into()),
        "still": true,
        "level": level,
        "volume": volume,
        "sheet": arg_value(args, "--sheet"),
        "t": t,
        "board": arg_value(args, "--board").unwrap_or_else(|| "1".into()),
    }))
}

// ======================= 入口 =======================

fn main() {
    let args: Vec<String> = std::env::args().collect();

    // 调试：导出托盘图标帧 / 应用图标帧
    if let Some(dir) = arg_value(&args, "--dump-tray") {
        if let Err(e) = orb::dump(std::path::Path::new(&dir)) {
            eprintln!("dump-tray 失败: {e}");
        }
        return;
    }
    if let Some(dir) = arg_value(&args, "--dump-app-icons") {
        if let Err(e) = orb::dump_app_icons(std::path::Path::new(&dir)) {
            eprintln!("dump-app-icons 失败: {e}");
        }
        return;
    }

    let start_hidden = args.iter().any(|a| a == "--tray");
    let preview = preview_config(&args);
    // 自动化测试用：面板失焦时不自动收起（避免测试期间点了别的窗口导致面板消失）
    let keep_panel = std::env::var_os("HPS_KEEP_PANEL").is_some();
    // 截图模式与自动化测试（HPS_ISOLATED）要和已安装、正在运行的程序完全隔离：
    // 不注册单实例（否则会被当成“再次启动”转交给它后直接退出），WebView2 也用单独的数据目录
    let isolated = preview.is_some() || std::env::var_os("HPS_ISOLATED").is_some();
    let webview_dir = isolated.then(|| std::env::temp_dir().join("HomePodSender-isolated-webview"));

    let mut builder = tauri::Builder::default();
    if !isolated {
        builder = builder.plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_panel(app);
        }));
    }
    builder
        .invoke_handler(tauri::generate_handler![
            sidecar_call,
            intro_near,
            intro_done,
            panel_hidden,
            panel_visible,
            open_logs,
            quit_app
        ])
        .setup(move |app| {
            let handle = app.handle().clone();
            app.manage(UiState::new());

            // ---------- 截图模式：只建一个离屏窗口渲染指定视图 ----------
            if let Some(preview) = &preview {
                let view = preview.get("view").and_then(Value::as_str).unwrap_or("panel").to_string();
                let script = format!(
                    "window.__HPS_PREVIEW__ = {}; window.__HPS_VIEW__ = {};",
                    serde_json::to_string(preview).unwrap_or_else(|_| "null".into()),
                    serde_json::to_string(&view).unwrap_or_else(|_| "\"panel\"".into()),
                );
                let (w, h) = if view == "intro" { (1280.0, 720.0) } else { (WIN_W, WIN_H) };
                let window = with_data_dir(WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into())), &webview_dir)
                    .title("HomePod 推流")
                    .inner_size(w, h)
                    .resizable(false)
                    .decorations(false)
                    .transparent(true)
                    .shadow(false)
                    .skip_taskbar(true)
                    .position(-3000.0, -3000.0)
                    .initialization_script(&script)
                    .visible(false)
                    .build()?;
                window.show()?;
                return Ok(());
            }

            // ---------- 正常运行 ----------
            let state = start_sidecar(&handle);
            app.manage(state);
            create_tray(app)?;
            spawn_tray_animator(handle.clone());

            let (px, py) = panel_origin(&handle);
            let main = with_data_dir(WebviewWindowBuilder::new(app, "main", WebviewUrl::App("index.html".into())), &webview_dir)
                .title("HomePod 推流")
                .inner_size(WIN_W, WIN_H)
                .position(px, py)
                .resizable(false)
                .maximizable(false)
                .minimizable(false)
                .decorations(false)
                .transparent(true)
                .shadow(false)
                .always_on_top(true)
                .skip_taskbar(true)
                .focused(false)
                .initialization_script("window.__HPS_VIEW__ = 'panel';")
                .visible(false)
                .build()?;
            let _ = main;

            if !start_hidden {
                // 启动动画：覆盖整个主显示器的透明、鼠标穿透窗口
                let (mx, my, mw, mh) = match handle.primary_monitor().ok().flatten() {
                    Some(m) => {
                        let s = m.scale_factor();
                        (m.position().x as f64 / s, m.position().y as f64 / s, m.size().width as f64 / s, m.size().height as f64 / s)
                    }
                    None => (0.0, 0.0, 1280.0, 720.0),
                };
                let cfg = json!({
                    "tx": px + ORB_X - mx,
                    "ty": py + ORB_Y - my,
                    "size": ORB_SIZE,
                    "endColor": 0.0,
                    "endEnergy": 0.0,
                });
                let script = format!("window.__HPS_VIEW__ = 'intro'; window.__HPS_INTRO__ = {};", cfg);
                let intro = with_data_dir(WebviewWindowBuilder::new(app, "intro", WebviewUrl::App("index.html".into())), &webview_dir)
                    .title("HomePod 推流")
                    .inner_size(mw, mh)
                    .position(mx, my)
                    .resizable(false)
                    .decorations(false)
                    .transparent(true)
                    .shadow(false)
                    .always_on_top(true)
                    .skip_taskbar(true)
                    .focused(false)
                    .initialization_script(&script)
                    .visible(false)
                    .build()?;
                let _ = intro.set_ignore_cursor_events(true);
                intro.show()?;
                if let Some(ui) = app.try_state::<UiState>() {
                    ui.intro_running.store(true, Ordering::Relaxed);
                }
                // 兜底：动画异常时 6 秒后仍然交接
                let h2 = handle.clone();
                std::thread::spawn(move || {
                    std::thread::sleep(Duration::from_secs(6));
                    if h2.get_webview_window("intro").is_some() {
                        intro_done(h2.clone());
                    }
                });
            }
            Ok(())
        })
        .on_window_event(move |window, event| {
            if window.label() != "main" {
                return;
            }
            match event {
                WindowEvent::CloseRequested { api, .. } => {
                    api.prevent_close();
                    request_hide(window.app_handle());
                }
                WindowEvent::Focused(false) if !keep_panel => request_hide(window.app_handle()),
                _ => {}
            }
        })
        .build(tauri::generate_context!())
        .expect("error while building HomePodSender")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                write_fire(app, "quit", json!({}));
                std::thread::sleep(Duration::from_millis(200));
                if let Some(state) = app.try_state::<SidecarState>() {
                    if let Some(mut child) = state.child.lock().unwrap().take() {
                        let _ = child.kill();
                        let _ = child.wait();
                    }
                }
            }
        });
}
