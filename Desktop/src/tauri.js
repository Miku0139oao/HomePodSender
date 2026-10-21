import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import { getCurrentWindow } from '@tauri-apps/api/window';

function subscribe(name, cb) {
  let unlisten = null;
  let dead = false;
  listen(name, (e) => cb(e.payload)).then((fn) => {
    if (dead) fn();
    else unlisten = fn;
  });
  return () => {
    dead = true;
    if (unlisten) unlisten();
  };
}

if (typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window) {
  const win = getCurrentWindow();
  window.hps = {
    tauri: true,
    call: (cmd, payload) => invoke('sidecar_call', { cmd, payload: payload || {} }),
    onEvent: (cb) => subscribe('sidecar:event', cb),
    /** Rust 通知面板展开（'show'）或收起（'hide'） */
    onPanel: (cb) => subscribe('panel', cb),
    /** 收起动画播完后告诉 Rust 真正隐藏窗口 */
    panelHidden: () => invoke('panel_hidden').catch(() => {}),
    /** 面板窗口当前是否已由 Rust 显示（页面加载晚于 “show” 事件时用来补上展开） */
    panelVisible: () => invoke('panel_visible').catch(() => false),
    /** 启动动画开始飞向右下角：让 Rust 提前显示（全透明待命的）面板窗口 */
    introNear: () => invoke('intro_near').catch(() => {}),
    /** 启动动画落地，交接给面板 */
    introDone: () => invoke('intro_done').catch(() => {}),
    openLogs: () => invoke('open_logs').catch(() => {}),
    minimize: () => win.minimize().catch(() => {}),
    hide: () => invoke('panel_hidden').catch(() => {}),
    quit: () => invoke('quit_app'),
  };
}
