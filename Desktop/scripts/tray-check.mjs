import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { appExe, connectCdp, debugPort, ensureVite, repo, root, sleep, waitFor } from './cdp.mjs';

/*
 * 托盘图标实拍：以 --tray 启动（不播启动动画、面板隐藏），截任务栏右侧原尺寸，然后退出。
 * 输出 shots/tray/taskbar-idle.png。Windows 11 默认把新程序的托盘图标收进“^”溢出区，那种情况下这里看不到图标。
 */
const outDir = path.join(root, 'shots', 'tray');
fs.mkdirSync(outDir, { recursive: true });
const port = debugPort(9444);
const vite = await ensureVite();
const app = spawn(appExe, ['--tray'], {
  cwd: root,
  stdio: 'ignore',
  env: {
    ...process.env,
    HOMEPODSENDER_DATA: path.join(repo, 'tests', 'test-data'),
    // 与可能正在运行的已安装程序隔离（不走单实例转交）
    HPS_ISOLATED: '1',
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
  },
});
try {
  const cdp = await connectCdp(port, { view: 'panel', timeoutMs: 20000 });
  await sleep(2500);
  const r = spawnSync('powershell', [
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(root, 'scripts', 'capture-screen.ps1'),
    '-Out', outDir, '-Frames', '1', '-Region', '1380,1032,540,48', '-Scale', '1',
  ], { encoding: 'utf8' });
  console.log(r.stdout.trim());
  const shot = fs.readdirSync(outDir).find((f) => /^f00-/.test(f));
  if (shot) fs.renameSync(path.join(outDir, shot), path.join(outDir, 'taskbar-idle.png'));
  await cdp.eval('window.hps.quit()').catch(() => {});
  await waitFor(() => app.exitCode !== null, 6000);
  console.log('exit', app.exitCode);
} finally {
  if (app.exitCode === null) app.kill();
  if (vite) vite.kill();
}
