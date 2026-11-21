// 扩展后台：只有这里去问本机端口。页面里的脚本不直接访问 127.0.0.1。
// 多个框架会同时来问，800 毫秒内复用同一次结果，并对并发请求去重。
let cache = { at: 0, body: { enabled: false, ms: 500 } };
let inFlight = null;

async function fetchStatus() {
  let body = { enabled: false, ms: 500 };
  try {
    const opts = { cache: 'no-store' };
    if (typeof AbortSignal !== 'undefined' && typeof AbortSignal.timeout === 'function') {
      opts.signal = AbortSignal.timeout(400);
    }
    const r = await fetch('http://127.0.0.1:17321/lipsync', opts);
    if (r.ok) {
      const j = await r.json();
      const ms = Number(j.ms);
      body = { enabled: !!j.enabled, ms: Number.isFinite(ms) ? Math.max(0, Math.min(2000, ms)) : 500 };
    }
  } catch { /* 程序没开或超时 */ }
  cache = { at: Date.now(), body };
  return body;
}

function current() {
  if (Date.now() - cache.at < 800) return Promise.resolve(cache.body);
  if (!inFlight) {
    inFlight = fetchStatus().finally(() => { inFlight = null; });
  }
  return inFlight;
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || msg.source !== 'hps-lip-ask') return;
  current().then(sendResponse, () => sendResponse({ enabled: false, ms: 500 }));
  return true;
});
