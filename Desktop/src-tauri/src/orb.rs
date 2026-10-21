//! 托盘图标用的 Siri 光团。
//! 与前端 `src/Orb.jsx` 的片元着色器是同一套公式，这里在 CPU 上逐像素绘制（3×3 超采样），
//! 这样窗口隐藏、WebView 停止渲染时托盘图标照样能动。

#[derive(Clone, Copy)]
pub struct OrbParams {
    /// 0 = 透明白色球体（未播放），1 = 彩色光团（播放中）
    pub color: f32,
    /// 0..1 声音能量
    pub energy: f32,
    /// 秒（与前端一样取墙钟，花纹同相）
    pub time: f32,
    /// 球半径（相对半边长）
    pub radius: f32,
    /// 图标模式：光团收紧、光晕减弱，白球加实并加一圈深色描边，保证 16~32px 下在深浅任务栏上都看得清
    pub icon: bool,
}

type V3 = [f32; 3];

fn add(a: V3, b: V3) -> V3 { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
fn scale(a: V3, k: f32) -> V3 { [a[0] * k, a[1] * k, a[2] * k] }
fn mix3(a: V3, b: V3, t: f32) -> V3 { [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t] }
fn mix(a: f32, b: f32, t: f32) -> f32 { a + (b - a) * t }
fn smoothstep(e0: f32, e1: f32, x: f32) -> f32 {
    let t = ((x - e0) / (e1 - e0)).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}
fn g(d: f32, w: f32) -> f32 { (-(d * d) / (w * w)).exp() }

/// 单个采样点的预乘颜色与不透明度；坐标 p ∈ [-1,1]²，y 向上
fn shade(px: f32, py: f32, o: &OrbParams) -> (V3, f32) {
    let r = (px * px + py * py).sqrt();
    let ang = py.atan2(px);
    let big_r = o.radius;
    let e = o.energy.clamp(0.0, 1.0);
    let t = o.time;
    let k = o.color.clamp(0.0, 1.0);
    // 画布边缘前淡出；图标没有大光晕，可以更贴边
    let edge_fade = if o.icon { 1.0 - smoothstep(0.93, 1.0, r) } else { 1.0 - smoothstep(0.86, 0.995, r) };

    // ---------- 彩色光团 ----------
    let wob = 0.038 * (3.0 * ang + t * 1.3).sin() + 0.018 * (5.0 * ang - t * 2.1).sin() + 0.024 * (2.0 * ang - t * 0.8 + 1.0).sin();
    let base = big_r;
    let rc = base * (0.90 + 0.10 * e) * (1.0 + wob * (0.45 + 1.4 * e));
    let x = r / rc;
    // 漩涡坐标：越靠近中心转得越多
    let tw = (1.0 - smoothstep(0.0, 1.1, x)) * (1.3 + 1.7 * e);
    let sa = ang + tw + t * (0.22 + 0.85 * e);
    let (qy, qx) = sa.sin_cos();
    let (qx, qy) = (qx * x, qy * x);
    let sp = t * (0.45 + 0.9 * e);
    let blob = |c: V3, cx: f32, cy: f32, s: f32, gain: f32| -> V3 {
        let (dx, dy) = (qx - cx, qy - cy);
        scale(c, (-(dx * dx + dy * dy) / (s * s)).exp() * gain)
    };
    let mut c = scale([0.32, 0.18, 0.70], 0.30);
    c = add(c, blob([1.00, 0.18, 0.56], 0.50 * sp.cos(), 0.50 * (sp * 1.3).sin(), 0.50, 1.0));
    c = add(c, blob([0.10, 0.52, 1.00], 0.52 * (sp * 0.8 + 2.1).cos(), 0.52 * (sp * 1.1 + 2.1).sin(), 0.50, 1.0));
    c = add(c, blob([0.56, 0.26, 1.00], 0.46 * (sp * 1.2 + 4.2).cos(), 0.46 * (sp * 0.9 + 4.2).sin(), 0.48, 1.0));
    c = add(c, blob([0.16, 0.95, 0.80], 0.42 * (sp * 1.5 + 1.0).cos(), 0.42 * (sp * 1.4 + 3.0).sin(), 0.38, 0.80));
    c = add(c, blob([1.00, 0.55, 0.30], 0.40 * (sp * 0.7 + 5.3).cos(), 0.40 * (sp * 1.6 + 0.6).sin(), 0.34, 0.55));

    let body = 1.0 - if o.icon { smoothstep(0.80, 1.03, x) } else { smoothstep(0.62, 1.10, x) };
    let core = g(x, 0.30 + 0.08 * e);
    let tint = mix3([0.62, 0.42, 1.0], [1.0, 0.36, 0.66], 0.5 + 0.5 * (t * 0.6).sin());
    let halo_gain = if o.icon { 0.3 } else { 1.0 };
    let mut cc = add(scale(c, body * (0.95 + 0.75 * e)), scale([1.0, 1.0, 1.0], core * (0.30 + 0.40 * e)));
    let halo = (-(x - 0.80).max(0.0) * mix(4.2, 2.6, e)).exp() * (1.0 - body * 0.6) * (0.28 + 0.30 * e) * halo_gain;
    cc = add(cc, scale(tint, halo));
    let cc = [
        (1.0 - (-cc[0] * 1.15).exp()) * edge_fade,
        (1.0 - (-cc[1] * 1.15).exp()) * edge_fade,
        (1.0 - (-cc[2] * 1.15).exp()) * edge_fade,
    ];
    // 当作发光：不透明度取最亮通道（cc 本身即预乘色）
    let ca = cc[0].max(cc[1]).max(cc[2]);

    // ---------- 透明白色球体 ----------
    let (ux, uy) = (px / big_r, py / big_r);
    let ur = (ux * ux + uy * uy).sqrt();
    let in_s = if ur < 1.0 { 1.0 } else { 0.0 };
    let z = (1.0 - ur * ur).max(0.0).sqrt();
    let fres = (1.0 - z).powf(2.2);
    let hl = (-((ux + 0.32) * (ux + 0.32) / 0.09 + (uy - 0.45) * (uy - 0.45) / 0.035)).exp();
    let lum = c[0] * 0.299 + c[1] * 0.587 + c[2] * 0.114;
    let pearl = mix3([lum, lum, lum], c, 0.45);
    let mut wa = (0.10 + 0.52 * fres + 0.30 * hl + 0.06 * lum).clamp(0.0, 1.0) * in_s;
    let w0 = add([0.97, 0.98, 1.0], scale(pearl, 0.18));
    let mut wcol = [w0[0].min(1.0), w0[1].min(1.0), w0[2].min(1.0)];
    if o.icon && in_s > 0.0 {
        // 图标尺寸下整体再实一点，避免在浅色任务栏上“消失”
        wa = (wa * 1.45 + 0.12).clamp(0.0, 1.0);
        wcol = mix3(wcol, [0.93, 0.94, 0.97], 0.2 * (1.0 - fres));
    }
    let mut wh = (-(ur - 1.0).max(0.0) * big_r * 11.0).exp() * 0.14 * (1.0 - in_s) * edge_fade;
    let mut wpm = add(scale(wcol, wa), [wh, wh, wh]);
    let mut wa_total = (wa + wh).min(1.0);
    if o.icon && in_s == 0.0 {
        // 图标：外缘一圈半透明深色描边
        wh *= 0.6;
        let dark = 0.38 * g(r - big_r - 0.015, 0.035);
        if dark > wh {
            wpm = scale([0.08, 0.08, 0.1], dark);
            wa_total = dark;
        } else {
            wpm = [wh, wh, wh];
            wa_total = wh;
        }
    }

    (mix3(wpm, cc, k), mix(wa_total, ca, k))
}

/// 渲染 size×size 的 RGBA（非预乘 alpha），可直接交给 `tauri::image::Image::new_owned`
pub fn render(size: u32, o: OrbParams) -> Vec<u8> {
    const SS: usize = 3;
    let n = size as usize;
    let mut out = vec![0u8; n * n * 4];
    for py in 0..n {
        for px in 0..n {
            let mut acc = [0.0f32; 4];
            for sy in 0..SS {
                for sx in 0..SS {
                    let fx = (px as f32 + (sx as f32 + 0.5) / SS as f32) / n as f32 * 2.0 - 1.0;
                    let fy = 1.0 - (py as f32 + (sy as f32 + 0.5) / SS as f32) / n as f32 * 2.0;
                    // shade 返回预乘色，直接累加
                    let (c, a) = shade(fx, fy, &o);
                    acc[0] += c[0].min(1.0);
                    acc[1] += c[1].min(1.0);
                    acc[2] += c[2].min(1.0);
                    acc[3] += a.min(1.0);
                }
            }
            let kk = (SS * SS) as f32;
            let a = acc[3] / kk;
            let i = (py * n + px) * 4;
            if a > 1e-4 {
                out[i] = ((acc[0] / kk / a).clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
                out[i + 1] = ((acc[1] / kk / a).clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
                out[i + 2] = ((acc[2] / kk / a).clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
            }
            out[i + 3] = (a.clamp(0.0, 1.0) * 255.0 + 0.5) as u8;
        }
    }
    out
}

/// 墙钟秒数（与前端 orbClock 一致：一小时取模）
pub fn clock() -> f32 {
    let ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis())
        .unwrap_or(0);
    ((ms % 3_600_000) as f32) / 1000.0
}

/// 导出应用图标（exe / 开始菜单 / 安装包）用的彩色光团，原始 RGBA，由 design-ref/make_app_icons.py 转成 PNG 与 ICO
pub fn dump_app_icons(dir: &std::path::Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    // 能量偏低时边缘起伏小、外形更圆；这一相位青 / 品红 / 紫的漩涡最分明
    for &size in &[16u32, 24, 32, 48, 64, 128, 256] {
        let px = render(size, OrbParams { color: 1.0, energy: 0.3, time: 1.7, radius: 0.9, icon: true });
        std::fs::write(dir.join(format!("app-{size}x{size}.rgba")), px)?;
    }
    Ok(())
}

/// 调试：把若干帧写成原始 RGBA 文件（宽高写在文件名里），供脚本拼成预览图
pub fn dump(dir: &std::path::Path) -> std::io::Result<()> {
    std::fs::create_dir_all(dir)?;
    let cases: [(&str, f32, f32, f32); 5] = [
        ("idle", 0.0, 0.0, 1.7),
        ("busy", 0.55, 0.15, 1.7),
        ("play-a", 1.0, 0.3, 1.7),
        ("play-b", 1.0, 0.7, 2.5),
        ("play-c", 1.0, 0.9, 3.3),
    ];
    for &size in &[16u32, 24, 32, 64] {
        for (name, color, energy, time) in cases.iter() {
            let px = render(size, OrbParams { color: *color, energy: *energy, time: *time, radius: 0.86, icon: true });
            std::fs::write(dir.join(format!("{name}-{size}x{size}.rgba")), px)?;
        }
    }
    Ok(())
}
