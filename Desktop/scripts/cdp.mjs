import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const repo = path.resolve(root, '..');
export const appExe = process.env.HPS_EXE || path.join(root, 'src-tauri', 'target', 'debug', 'HomePodSender.exe');

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function buildApp() {
  // 触碰配置，确保 tauri-build 重新嵌入最新 dist
  const conf = path.join(root, 'src-tauri', 'tauri.conf.json');
  const now = new Date();
  fs.utimesSync(conf, now, now);
  const r = spawnSync('cargo', ['build', '--manifest-path', 'src-tauri/Cargo.toml'], {
    cwd: root,
    stdio: 'inherit',
  });
  if (r.status !== 0) throw new Error('cargo build 失败');
}

/** 当前所有 WebView 页面（应用有面板、启动动画两个窗口） */
export async function listPages(port) {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/json`);
    return (await res.json()).filter((t) => t.type === 'page');
  } catch {
    return [];
  }
}

/**
 * 连接 WebView2 调试页面。
 * opts.view 指定视图（'panel' / 'intro'），按页面里的 window.__HPS_VIEW__ 挑选；不指定则取第一个页面。
 */
export async function connectCdp(port, opts = {}) {
  const { timeoutMs = 30000, view = null } = typeof opts === 'number' ? { timeoutMs: opts } : opts;
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    for (const page of await listPages(port)) {
      const session = await attach(page.webSocketDebuggerUrl).catch(() => null);
      if (!session) continue;
      if (!view) return session;
      const v = await session.eval('window.__HPS_VIEW__ || ""').catch(() => '');
      if (v === view) return session;
      session.close();
    }
    await sleep(250);
  }
  throw new Error(view ? `WebView2 调试页面未就绪（${view}）` : 'WebView2 调试页面未就绪');
}

async function attach(wsUrl) {
  const ws = new WebSocket(wsUrl);
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  let seq = 0;
  const pending = new Map();
  const listeners = new Map();
  ws.onmessage = (e) => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) {
      const p = pending.get(m.id);
      pending.delete(m.id);
      m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
    } else if (m.method && listeners.has(m.method)) {
      for (const fn of listeners.get(m.method)) fn(m.params);
    }
  };
  // 窗口关闭（如启动动画窗口）时让未完成的调用尽快失败
  ws.onclose = () => {
    for (const p of pending.values()) p.reject(new Error('调试连接已关闭'));
    pending.clear();
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evalJs = async (expression) => {
    const r = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) {
      throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    }
    return r.result.value;
  };
  const on = (method, fn) => {
    if (!listeners.has(method)) listeners.set(method, []);
    listeners.get(method).push(fn);
  };
  await send('Runtime.enable');
  return { send, eval: evalJs, on, close: () => ws.close() };
}

export async function waitFor(fn, timeoutMs, interval = 250) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    try { if (await fn()) return true; } catch { /* retry */ }
    await sleep(interval);
  }
  return false;
}

export async function ensureVite() {
  const probe = async () => {
    try { const r = await fetch('http://127.0.0.1:5173'); return r.ok; } catch { return false; }
  };
  if (await probe()) return null;

  const viteBin = path.join(root, 'node_modules', 'vite', 'bin', 'vite.js');
  const proc = spawn(process.execPath, [viteBin], { cwd: root, stdio: 'ignore', windowsHide: true });
  const ok = await waitFor(probe, 20000);
  if (!ok) {
    try { proc.kill(); } catch { }
    throw new Error('Vite 开发服务器未启动');
  }
  return proc;
}

export function debugPort(base) {
  return Number(process.env.HPS_CDP_PORT || base);
}
