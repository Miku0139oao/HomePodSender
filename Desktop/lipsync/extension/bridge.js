// 隔离世界：问后台要不要推迟、推迟多少，再告诉页面主世界。
// 真正访问 127.0.0.1 的是扩展后台。网页是公网站点时，页面自己的请求会被浏览器拦住。
(function () {
  const url = 'http://127.0.0.1:17321/lipsync';
  async function poll() {
    let enabled = false;
    let ms = 500;
    try {
      let j = null;
      if (typeof chrome !== 'undefined' && chrome.runtime && chrome.runtime.sendMessage) {
        j = await chrome.runtime.sendMessage({ source: 'hps-lip-ask' });
      } else {
        const r = await fetch(url, { cache: 'no-store' });
        if (r.ok) j = await r.json();
      }
      if (j) {
        enabled = !!j.enabled;
        ms = j.ms;
      }
    } catch { /* 程序没开或后台刚睡醒：这一拍先恢复原画 */ }
    window.postMessage({ source: 'hps-lip', enabled: enabled, ms: ms }, '*');
  }
  poll();
  setInterval(poll, 1000);
})();
