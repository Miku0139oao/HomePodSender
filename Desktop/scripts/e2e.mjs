import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { appExe, buildApp, connectCdp, debugPort, ensureVite, listPages, repo, root, sleep, waitFor } from './cdp.mjs';

/*
 * 端到端测试：真实应用 + 假 HomePod（Ap2Cli fake-receiver，127.0.0.1:7000，不会出声）。
 * 覆盖：启动动画 → 右下角面板交接、面板位置、连接 / 断开、音量（点选 / 滚轮 / 静音）、
 *       设置翻面与延迟切换、Esc 收起、再次启动唤出面板（单实例）、退出。
 * 加 --shots 时同时连拍桌面（屏幕中心到右下角），输出到 shots/live/，用来核对真实桌面上的动画。
 */

const wantShots = process.argv.includes('--shots');
const dataDir = path.join(repo, 'tests', 'test-data');
const cfgPath = path.join(dataDir, 'config.json');
const cfgBackup = fs.readFileSync(cfgPath, 'utf8');
const port = debugPort(9333);
const results = [];
const ok = (name, cond, detail = '') => {
  results.push({ name, cond: !!cond, detail });
  console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  —  ' + detail : ''}`);
};
const readCfg = () => JSON.parse(fs.readFileSync(cfgPath, 'utf8'));

if (!fs.existsSync(appExe)) buildApp();
const vite = await ensureVite();

const fake = spawn('dotnet', [
  path.join(repo, 'Ap2Cli', 'bin', 'Debug', 'net8.0-windows', 'Ap2Cli.dll'),
  'fake-receiver', '--port', '7000', '--seconds', '240',
], { cwd: repo, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
let fakeReady = false;
let fakeOut = '';
fake.stdout.setEncoding('utf8');
fake.stdout.on('data', (d) => { fakeOut += d; if (/监听/.test(fakeOut)) fakeReady = true; });
fake.stderr.setEncoding('utf8');
fake.stderr.on('data', (d) => process.stderr.write('[fake] ' + d));

// 已安装的程序正在运行时，测试实例走隔离模式（不注册单实例、单独的 WebView2 数据目录），
// 否则会被当成“再次启动”转交给它后直接退出；这种情况下跳过单实例相关检查
const pidsOf = (image) => {
  const r = spawnSync('tasklist', ['/FI', `IMAGENAME eq ${image}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
  return [...r.stdout.matchAll(/"[^"]+","(\d+)"/g)].map((m) => m[1]);
};
const isolated = pidsOf('HomePodSender.exe').length > 0;
const sidecarsBefore = pidsOf('HomePodSidecar.exe');
if (isolated) console.log('检测到已安装的 HomePodSender 正在运行：测试实例使用隔离模式，跳过单实例检查');

const appEnv = {
  ...process.env,
  HOMEPODSENDER_DATA: dataDir,
  HPS_KEEP_PANEL: '1',
  ...(isolated ? { HPS_ISOLATED: '1' } : {}),
  WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
};

let shooter = null;
if (wantShots) {
  const outDir = path.join(root, 'shots', 'live');
  fs.rmSync(outDir, { recursive: true, force: true });
  shooter = spawn('powershell', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(root, 'scripts', 'capture-screen.ps1'),
    '-Out', outDir, '-Frames', '30', '-IntervalMs', '180',
  ], { stdio: 'ignore', windowsHide: true });
}

if (!(await waitFor(() => fakeReady, 15000))) {
  console.error('假 HomePod 未启动');
  fake.kill();
  process.exit(1);
}
ok('假 HomePod 启动', true);

const t0 = Date.now();
const app = spawn(appExe, [], { cwd: root, stdio: ['ignore', 'pipe', 'pipe'], env: appEnv });
let appErr = '';
app.stdout.setEncoding('utf8');
app.stderr.setEncoding('utf8');
app.stdout.on('data', (d) => process.stdout.write('[app] ' + d));
app.stderr.on('data', (d) => { appErr += d; process.stderr.write('[app] ' + d); });

/* ---------- CDP 输入助手（真实鼠标 / 键盘事件，指针捕获等行为与手动操作一致） ---------- */
const mouse = (cdp, type, x, y, extra = {}) =>
  cdp.send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1, ...extra });
async function rectOf(cdp, selector) {
  const r = await cdp.eval(`(() => {
    const el = document.querySelector(${JSON.stringify(selector)});
    if (!el) return null;
    const b = el.getBoundingClientRect();
    return { left: b.left, top: b.top, width: b.width, height: b.height };
  })()`);
  if (!r) throw new Error('找不到元素 ' + selector);
  return r;
}
async function clickAt(cdp, x, y) {
  await mouse(cdp, 'mouseMoved', x, y, { button: 'none' });
  await mouse(cdp, 'mousePressed', x, y);
  await mouse(cdp, 'mouseReleased', x, y);
}
async function click(cdp, selector) {
  const r = await rectOf(cdp, selector);
  await clickAt(cdp, r.left + r.width / 2, r.top + r.height / 2);
}
async function key(cdp, k, code, vk) {
  await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key: k, code, windowsVirtualKeyCode: vk });
  await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code, windowsVirtualKeyCode: vk });
}

let cdp = null;
const pageErrors = [];
try {
  /* ---------- 启动动画 ---------- */
  let introSeen = false;
  try {
    const intro = await connectCdp(port, { view: 'intro', timeoutMs: 8000 });
    introSeen = await waitFor(() => intro.eval(`!!document.querySelector('.intro-orb canvas')`), 3000, 100);
    intro.close();
  } catch { /* 动画可能已经播完 */ }
  ok('启动动画窗口出现（屏幕中央光团）', introSeen);

  cdp = await connectCdp(port, { view: 'panel', timeoutMs: 20000 });
  cdp.on('Runtime.exceptionThrown', (p) => pageErrors.push(p.exceptionDetails?.exception?.description || p.exceptionDetails?.text));
  cdp.on('Runtime.consoleAPICalled', (p) => {
    if (p.type === 'error') pageErrors.push(p.args.map((a) => a.value ?? a.description).join(' '));
  });

  const opened = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.phase-open')`), 12000, 100);
  const tOpen = Date.now() - t0;
  ok('动画结束后面板自动展开', opened, `${tOpen}ms`);
  ok('由动画交接（早于 6 秒兜底）', opened && tOpen < 5800, `${tOpen}ms`);
  const introGone = await waitFor(async () => (await listPages(port)).length === 1, 4000);
  ok('启动动画窗口已关闭', introGone, `页面数 ${(await listPages(port)).length}`);

  /* ---------- 位置与几何 ---------- */
  const geo = await cdp.eval(`({
    x: window.screenX, y: window.screenY, w: window.outerWidth, h: window.outerHeight,
    al: screen.availLeft, at: screen.availTop, aw: screen.availWidth, ah: screen.availHeight, dpr: devicePixelRatio,
  })`);
  ok('面板窗口 468×404', geo.w === 468 && geo.h === 404, `${geo.w}×${geo.h}`);
  ok('面板贴住工作区右下角（任务栏上方）',
    Math.abs(geo.x + geo.w - (geo.al + geo.aw)) <= 2 && Math.abs(geo.y + geo.h - (geo.at + geo.ah)) <= 2,
    JSON.stringify(geo));
  const orb = await rectOf(cdp, '.orb-btn');
  ok('光球中心与动画落点一致 (342,110)',
    Math.abs(orb.left + orb.width / 2 - 342) < 1 && Math.abs(orb.top + orb.height / 2 - 110) < 1,
    `${(orb.left + orb.width / 2).toFixed(1)},${(orb.top + orb.height / 2).toFixed(1)}`);

  /* ---------- 初始内容 ---------- */
  await waitFor(() => cdp.eval(`!document.querySelector('.orb-btn').disabled`), 10000);
  const title = () => cdp.eval(`document.querySelector('.now-title')?.textContent || ''`);
  const sub = () => cdp.eval(`document.querySelector('.now-sub')?.textContent || ''`);
  const orbLabel = () => cdp.eval(`document.querySelector('.orb-btn')?.getAttribute('aria-label') || ''`);
  const volText = () => cdp.eval(`document.querySelector('.vol-val')?.textContent || ''`);
  ok('右上卡显示设备名', (await title()) === '假 HomePod', await title());
  ok('未播放提示', (await sub()) === '輕點光球 · 開始推流', await sub());
  ok('设备列表勾选当前设备', await cdp.eval(`document.querySelector('.row.on .row-label')?.textContent === '假 HomePod'`));
  ok('点阵标题 AIR PLAY', await cdp.eval(`document.querySelectorAll('.face-front .headline circle').length > 40`));
  ok('光球画布在渲染', await cdp.eval(`(() => { const c = document.querySelector('.orb-btn canvas'); return !!c && c.width > 0; })()`));

  /* ---------- 连接 ---------- */
  await click(cdp, '.orb-btn');
  const connected = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.mode-streaming')`), 25000);
  ok('轻点光球进入推流', connected, await sub());
  ok('光球按钮变为断开', (await orbLabel()) === '中斷連線', await orbLabel());
  await sleep(1600);
  const s1 = await sub();
  ok('计时器在走', /^\d\d:\d\d · 即時$/.test(s1) && !s1.startsWith('00:00'), s1);

  /* ---------- 音量 ---------- */
  const ladder = await rectOf(cdp, '.ladder');
  await clickAt(cdp, ladder.left + ladder.width * 0.33, ladder.top + ladder.height / 2);
  await sleep(1200);
  ok('点阵音量可点选', (await volText()) === '33%', await volText());
  ok('音量写入配置', readCfg().VolumePercent === 33, 'VolumePercent=' + readCfg().VolumePercent);
  const volCard = await rectOf(cdp, '.card.vol');
  await cdp.send('Input.dispatchMouseEvent', {
    type: 'mouseWheel', x: volCard.left + volCard.width / 2, y: volCard.top + 30, deltaX: 0, deltaY: -120,
  });
  await sleep(400);
  ok('滚轮调音量 +2', (await volText()) === '35%', await volText());
  await click(cdp, '.vol-row .round');
  await sleep(400);
  ok('静音', (await volText()) === '0%', await volText());
  await click(cdp, '.vol-row .round');
  await sleep(400);
  ok('取消静音恢复音量', (await volText()) === '35%', await volText());

  /* ---------- 设置翻面 ---------- */
  const flipped = () => cdp.eval(`!!document.querySelector('.card.tall.flipped')`);
  // 点击前记下该位置实际命中的元素，失败时便于定位
  const hitAt = (sel) => cdp.eval(`(() => {
    const b = document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect();
    const el = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2);
    return el ? (el.closest('button')?.getAttribute('aria-label') || el.closest('button')?.textContent || el.tagName) : '(无)';
  })()`);
  await sleep(300);
  const gearHit = await hitAt('.mini-right');
  await click(cdp, '.mini-right');
  const toSettings = await waitFor(flipped, 2500, 100);
  ok('齿轮翻到设置面', toSettings, `命中：${gearHit}`);
  if (toSettings) {
    await sleep(700); // 等翻转动画结束再点背面
    const back = await cdp.eval(`document.querySelector('.face-back').innerText.replace(/\\s+/g, ' ')`);
    ok('设置项齐全', ['音訊', '斷線自動重連', '啟動時自動連線', '播放延遲', '背景卡片'].every((s) => back.includes(s)), back);
    ok('音訊來源預設跟隨系統', await cdp.eval(`document.querySelector('[data-setting="audio"] .audio-tag')?.textContent === '跟隨系統預設'`));

    // 语言切换必须同步整个界面；切回繁中后继续后续测试。
    await click(cdp, '.language-mini');
    await waitFor(() => cdp.eval(`document.documentElement.lang === 'en' && document.querySelector('.face-back').innerText.includes('Reconnect automatically')`), 1500, 100);
    ok('切換英文介面', await cdp.eval(`document.documentElement.lang === 'en' && document.querySelector('.language-mini')?.textContent === '繁中'`));
    await click(cdp, '.language-mini');
    await waitFor(() => cdp.eval(`document.documentElement.lang === 'zh-TW' && document.querySelector('.face-back').innerText.includes('斷線自動重連')`), 1500, 100);
    ok('切回繁體中文介面', await cdp.eval(`document.documentElement.lang === 'zh-TW' && document.querySelector('.language-mini')?.textContent === 'EN'`));

    await click(cdp, '[data-setting="audio"]');
    const audioPickerOpen = await waitFor(() => cdp.eval(`!!document.querySelector('.rows-audio')`), 1500, 100);
    ok('開啟音訊輸出選擇', audioPickerOpen);
    const audioRows = await cdp.eval(`document.querySelectorAll('.rows-audio .row').length`);
    ok('音訊選擇列出系統預設與播放裝置', audioRows >= 2, `rows=${audioRows}`);
    await click(cdp, '.rows-audio .row:nth-child(2)');
    const fixedAudio = await waitFor(() => !!(readCfg().AudioCaptureDeviceId), 4000, 100);
    ok('可綁定指定聲卡並寫入配置', fixedAudio, `AudioCaptureDeviceId=${readCfg().AudioCaptureDeviceId || '(default)'}`);
    const fixedPickerClosed = await waitFor(() => cdp.eval(`!!document.querySelector('.rows-set')`), 5000, 100);
    ok('切換指定聲卡後返回設定', fixedPickerClosed);
    const resumedAfterAudio = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.mode-streaming')`), 30000, 150);
    ok('播放中切換聲卡會自動重新連線', resumedAfterAudio, await sub());
    await click(cdp, '[data-setting="audio"]');
    await waitFor(() => cdp.eval(`!!document.querySelector('.rows-audio')`), 1500, 100);
    await click(cdp, '.rows-audio .row:first-child');
    const defaultAudio = await waitFor(() => readCfg().AudioCaptureDeviceId === '', 4000, 100);
    ok('可切回跟隨系統預設', defaultAudio, `AudioCaptureDeviceId=${readCfg().AudioCaptureDeviceId || '(default)'}`);
    const defaultPickerClosed = await waitFor(() => cdp.eval(`!!document.querySelector('.rows-set')`), 5000, 100);
    ok('切回預設後返回設定', defaultPickerClosed);
    const resumedAfterDefault = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.mode-streaming')`), 30000, 150);
    ok('切回預設後恢復推流', resumedAfterDefault, await sub());

    const latency = () => cdp.eval(`document.querySelector('[data-setting="latency"] .row-tag')?.textContent || ''`);
    ok('延迟显示 1s', (await latency()) === '1s', await latency());
    await click(cdp, '[data-setting="latency"]');
    await waitFor(async () => (await latency()) === '0.5s', 1500, 100);
    ok('点击延迟切到 0.5s', (await latency()) === '0.5s', await latency());
    for (const expected of ['0.25s', '0.1s', '0s']) {
      await click(cdp, '[data-setting="latency"]');
      await waitFor(async () => (await latency()) === expected, 1500, 100);
      ok(`延迟可切到 ${expected}`, (await latency()) === expected, await latency());
    }

    // 背景卡片开关：关掉后板外框消失、柔焦背景按卡片形状裁剪，并写入配置；再打开恢复
    const boardRow = '[data-setting="board"]';
    const noBoard = () => cdp.eval(`!!document.querySelector('.shell.no-board')`);
    ok('背景卡片默认开启', !(await noBoard()) && readCfg().ShowBoard !== false);
    await click(cdp, boardRow);
    await waitFor(noBoard, 1500, 100);
    await sleep(400);
    const clip = await cdp.eval(`getComputedStyle(document.querySelector('.backdrop')).clipPath`);
    ok('关闭背景卡片', (await noBoard()) && clip.startsWith('path('), clip.slice(0, 40));
    ok('背景卡片设置写入配置', readCfg().ShowBoard === false, 'ShowBoard=' + readCfg().ShowBoard);
    await click(cdp, boardRow);
    await waitFor(async () => !(await noBoard()), 1500, 100);
    await sleep(400);
    ok('重新打开背景卡片', !(await noBoard()) && readCfg().ShowBoard === true, 'ShowBoard=' + readCfg().ShowBoard);
    const backHit = await hitAt('.mini-left');
    await click(cdp, '.mini-left');
    const toDevices = await waitFor(async () => !(await flipped()), 2500, 100);
    ok('返回设备面', toDevices, `命中：${backHit}`);
    await sleep(700);
  }

  /* ---------- 断开 ---------- */
  const logText = fs.existsSync(path.join(dataDir, 'log.txt')) ? fs.readFileSync(path.join(dataDir, 'log.txt'), 'utf8') : '';
  ok('sidecar 记录连接日志', /配对|推流|连接/.test(logText), logText.trim().split('\n').slice(-1)[0] || '(空)');
  await click(cdp, '.orb-btn');
  const disconnected = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.mode-idle')`), 15000);
  ok('再次轻点光球断开', disconnected, await sub());
  ok('光球按钮变回连接', (await orbLabel()) === '連線', await orbLabel());

  /* ---------- 收起 / 唤出 ---------- */
  // 窗口在系统层面是否可见（WebView2 的 visibilityState 不随窗口隐藏变化，改问 Tauri）
  const osVisible = () => cdp.eval(`window.__TAURI_INTERNALS__.invoke('plugin:window|is_visible', { label: 'main' })`);
  await key(cdp, 'Escape', 'Escape', 27);
  const collapsed = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.phase-standby')`), 3000, 100);
  ok('Esc 收起面板', collapsed);
  const hidden = await waitFor(async () => (await osVisible()) === false, 2000, 100);
  ok('收起后窗口隐藏', hidden, `is_visible=${await osVisible().catch((e) => e.message)}`);

  if (!isolated) {
    const second = spawnSync(appExe, [], { env: appEnv, timeout: 10000 });
    ok('第二个实例立即退出（单实例）', second.status === 0 || second.status === null, `status=${second.status}`);
    const reopened = await waitFor(() => cdp.eval(`!!document.querySelector('.shell.phase-open')`), 5000, 100);
    ok('再次启动程序唤出面板', reopened);
  }

  ok('面板无脚本错误', pageErrors.length === 0, pageErrors.slice(0, 2).join(' | '));
  ok('应用无崩溃', app.exitCode === null && !/panicked|uncaught|Unhandled/i.test(appErr), appErr.slice(-160));

  /* ---------- 退出 ---------- */
  await cdp.eval('window.hps.quit()').catch(() => { /* 退出时连接会断开 */ });
  const exited = await waitFor(() => app.exitCode !== null, 6000);
  ok('托盘“退出”后进程结束', exited, `exit=${app.exitCode}`);
  await sleep(500);
  // 只看本次测试启动的核心进程（已安装的程序自己的核心进程不算）
  const leftover = pidsOf('HomePodSidecar.exe').filter((p) => !sidecarsBefore.includes(p));
  ok('核心进程随之退出', leftover.length === 0, leftover.length ? `残留 PID ${leftover.join(',')}` : '');
} catch (e) {
  ok('测试执行', false, e.message);
} finally {
  try { cdp?.close(); } catch { }
  if (app.exitCode === null) { try { app.kill(); } catch { } }
  try { fake.kill(); } catch { }
  if (shooter) await waitFor(() => shooter.exitCode !== null, 10000);
  if (vite) { try { vite.kill(); } catch { } }
  await sleep(600);
  fs.writeFileSync(cfgPath, cfgBackup);
  const failed = results.filter((r) => !r.cond).length;
  console.log(`\n${results.length - failed}/${results.length} 通过`);
  process.exit(failed ? 1 : 0);
}
