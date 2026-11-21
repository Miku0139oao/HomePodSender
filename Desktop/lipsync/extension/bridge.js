// 隔离世界：向扩展后台查询是否开启画面推迟，并直接通知同在隔离世界的 main.js（不经过 window.postMessage）。
(function () {
  let timer = 0;
  let stopped = false;

  function notify(enabled, ms) {
    if (typeof globalThis.__hpsLipUpdate === 'function') {
      globalThis.__hpsLipUpdate(enabled, ms);
    }
  }

  function stopPolling() {
    stopped = true;
    if (timer) {
      clearTimeout(timer);
      timer = 0;
    }
    notify(false, 500);
  }

  function schedule(ms) {
    if (stopped) return;
    if (timer) clearTimeout(timer);
    timer = setTimeout(poll, ms);
  }

  async function poll() {
    timer = 0;
    if (stopped) return;
    if (document.hidden) {
      notify(false, 500);
      return;
    }
    if (typeof chrome === 'undefined' || !chrome.runtime || !chrome.runtime.id) {
      stopPolling();
      return;
    }
    // 没有视频元素时降低轮询频率，避免所有空标签页持续唤醒 Service Worker
    const hasVideo = !!document.querySelector('video');
    let enabled = false;
    let ms = 500;
    try {
      const j = await chrome.runtime.sendMessage({ source: 'hps-lip-ask' });
      if (j) {
        enabled = !!j.enabled;
        if (Number.isFinite(Number(j.ms))) ms = Number(j.ms);
      }
    } catch (err) {
      const msg = String(err && err.message || err || '');
      if (msg.includes('Extension context invalidated') || !chrome.runtime.id) {
        stopPolling();
        return;
      }
    }
    notify(enabled, ms);
    schedule(hasVideo || enabled ? 1000 : 3000);
  }

  document.addEventListener('visibilitychange', () => {
    if (stopped) return;
    if (document.hidden) {
      if (timer) { clearTimeout(timer); timer = 0; }
      notify(false, 500);
    } else {
      poll();
    }
  });

  poll();
})();
