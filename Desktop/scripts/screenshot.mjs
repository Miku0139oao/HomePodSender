import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { appExe, buildApp, connectCdp, debugPort, ensureVite, root, sleep, waitFor } from './cdp.mjs';

/*
 * 用真实应用（WebView2）离屏渲染各个状态并截图（透明背景 PNG，2 倍分辨率）：
 *   面板：未播放 / 播放中 / 连接中 / 连接失败 / 设置面 / 关闭背景卡片
 *   启动动画：浮现 / 停顿 / 飞行 三个冻结帧
 * 输出到仓库根目录的 shots/（README 引用这里的图）。
 */
const outDir = path.resolve(root, '..', 'shots');
if (!fs.existsSync(appExe)) buildApp();
if (!fs.existsSync(appExe)) {
  console.error('未找到应用:', appExe);
  process.exit(1);
}
fs.mkdirSync(outDir, { recursive: true });

const vite = await ensureVite();

const PANEL = { x: 0, y: 0, width: 468, height: 404, scale: 2 };
const INTRO = { x: 0, y: 0, width: 1280, height: 720, scale: 1 };
const jobs = [
  { name: 'idle', args: ['--state', 'idle'], clip: PANEL },
  { name: 'streaming', args: ['--state', 'streaming', '--level', '0.72'], clip: PANEL },
  { name: 'connecting', args: ['--state', 'connecting'], clip: PANEL },
  { name: 'failed', args: ['--state', 'failed'], clip: PANEL },
  { name: 'settings', args: ['--state', 'streaming', '--level', '0.6', '--sheet', 'settings'], clip: PANEL },
  { name: 'no-board', args: ['--state', 'streaming', '--level', '0.7', '--board', '0'], clip: PANEL },
  { name: 'intro-appear', args: ['--view', 'intro', '--t', '0.35'], clip: INTRO },
  { name: 'intro-hold', args: ['--view', 'intro', '--t', '1.0'], clip: INTRO },
  { name: 'intro-fly', args: ['--view', 'intro', '--t', '1.75'], clip: INTRO },
];

const port = debugPort(9555);

for (const job of jobs) {
  const out = path.join(outDir, `${job.name}.png`);
  const proc = spawn(appExe, ['--screenshot', ...job.args], {
    cwd: root,
    stdio: 'ignore',
    env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}` },
  });
  try {
    const cdp = await connectCdp(port);
    const ready = job.clip === INTRO ? `!!document.querySelector('.intro-orb canvas')` : `!!document.querySelector('.shell .orb-btn canvas')`;
    if (!(await waitFor(() => cdp.eval(ready), 15000))) throw new Error('界面未渲染');
    await sleep(1200);
    await cdp.send('Page.enable');
    // 透明底：保留面板圆角外的阴影与启动动画的透明背景
    await cdp.send('Emulation.setDefaultBackgroundColorOverride', { color: { r: 0, g: 0, b: 0, a: 0 } });
    const shot = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false, clip: job.clip });
    fs.writeFileSync(out, Buffer.from(shot.data, 'base64'));
    console.log('saved', out);
    cdp.close();
  } catch (e) {
    console.error(job.name, '失败:', e.message);
  } finally {
    try { proc.kill(); } catch { }
    await sleep(500);
  }
}

console.log('screenshots saved in', outDir);
if (vite) { try { vite.kill(); } catch { } }
