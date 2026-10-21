// 扩展后台：只有这里去问本机端口。页面里的脚本不直接访问 127.0.0.1。
// 多个框架会同时来问，800 毫秒内复用同一次结果。
let cache = { at: 0, body: { enabled: false, ms: 500 } };

async function current() {
  if (Date.now() - cache.at < 800) return cache.body;
  let body = { enabled: false, ms: 500 };
  try {
    const r = await fetch('http://127.0.0.1:17321/lipsync', { cache: 'no-store' });
    if (r.ok) {
      const j = await r.json();
      const ms = Number(j.ms);
      body = { enabled: !!j.enabled, ms: Number.isFinite(ms) ? ms : 500 };
    }
  } catch { /* 程序没开 */ }
  cache = { at: Date.now(), body: body };
  return body;
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || msg.source !== 'hps-lip-ask') return;
  current().then(sendResponse);
  return true;
});
