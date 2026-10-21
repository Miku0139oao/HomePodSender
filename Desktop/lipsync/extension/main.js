// 主世界：只推迟画面。视频元素继续出声，声音仍按原时刻进系统回环，再由 HomePodSender 推到喇叭。
(function () {
  if (window.__hpsLipInstalled) return;
  window.__hpsLipInstalled = true;

  let enabled = false;
  let delayMs = 500;
  let active = null;

  function rootData(name, value) {
    if (document.documentElement) document.documentElement.dataset[name] = value;
  }

  function pick() {
    const list = [];
    document.querySelectorAll('video').forEach((v) => {
      if (v.paused || v.readyState < 2) return;
      if (v.videoWidth < 320 || v.clientWidth < 200 || v.clientHeight < 112) return;
      list.push(v);
    });
    list.sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight);
    return list[0] || null;
  }

  function attach(video) {
    const parent = video.parentElement;
    if (!parent) return null;
    const canvas = document.createElement('canvas');
    canvas.className = 'hps-lip';
    canvas.style.position = 'absolute';
    canvas.style.pointerEvents = 'none';
    canvas.style.zIndex = '2';
    const prevPosition = parent.style.position;
    // 只在父级还是静态定位时改成 relative，卸下时按原样还原
    const forcedPosition = getComputedStyle(parent).position === 'static';
    if (forcedPosition) parent.style.position = 'relative';
    parent.appendChild(canvas);
    const ctx = canvas.getContext('2d');
    const prevOpacity = video.style.opacity;
    let primed = false;
    let alive = true;
    let reader = null;
    let track = null;
    const queue = [];
    let timer = 0;

    function place() {
      const pr = parent.getBoundingClientRect();
      const vr = video.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const w = Math.max(2, Math.round(vr.width));
      const h = Math.max(2, Math.round(vr.height));
      canvas.style.left = (vr.left - pr.left + parent.scrollLeft) + 'px';
      canvas.style.top = (vr.top - pr.top + parent.scrollTop) + 'px';
      canvas.style.width = w + 'px';
      canvas.style.height = h + 'px';
      const bw = Math.max(2, Math.round(w * dpr));
      const bh = Math.max(2, Math.round(h * dpr));
      if (canvas.width !== bw || canvas.height !== bh) {
        canvas.width = bw;
        canvas.height = bh;
      }
    }

    function showDelayed() {
      if (primed) return;
      primed = true;
      video.style.opacity = '0';
      canvas.style.opacity = '1';
    }

    function drawOne(source) {
      place();
      try { ctx.drawImage(source, 0, 0, canvas.width, canvas.height); } catch { /* 这一帧画不了就等下一帧 */ }
    }

    // 丢掉已经比目标更旧的帧，留下「刚好晚 delayMs」的那一张。
    function takeDue(now) {
      while (queue.length > 1 && now - queue[1].t >= delayMs) {
        const old = queue.shift();
        if (old.frame) old.frame.close();
        if (old.bmp) old.bmp.close();
      }
      if (!queue.length || now - queue[0].t < delayMs) return null;
      return queue[0];
    }

    function pumpDraw() {
      if (!alive) return;
      const due = takeDue(performance.now());
      if (due) {
        drawOne(due.frame || due.bmp);
        showDelayed();
        rootData('hpsLag', String(Math.round(performance.now() - due.t)));
      }
      requestAnimationFrame(pumpDraw);
    }

    async function readFrames() {
      while (alive && reader) {
        let next;
        try { next = await reader.read(); }
        catch { break; }
        if (!alive || !next || next.done || !next.value) break;
        queue.push({ t: performance.now(), frame: next.value });
        if (queue.length > 180) {
          const old = queue.shift();
          old.frame.close();
        }
      }
    }

    function readBitmaps() {
      timer = setInterval(async () => {
        if (!alive || video.readyState < 2) return;
        const w = Math.max(2, Math.min(640, canvas.width || video.clientWidth || 640));
        const h = Math.max(2, Math.round(w * (video.videoHeight || 9) / (video.videoWidth || 16)));
        const t = performance.now();
        let bmp;
        try { bmp = await createImageBitmap(video, { resizeWidth: w, resizeHeight: h, resizeQuality: 'low' }); }
        catch {
          rootData('hpsLip', 'fail');
          return;
        }
        if (!alive) { bmp.close(); return; }
        queue.push({ t: t, bmp: bmp });
        while (queue.length > 48) queue.shift().bmp.close();
      }, 40);
    }

    place();
    canvas.style.opacity = '0';
    try {
      const stream = video.captureStream();
      track = stream.getVideoTracks()[0] || null;
      if (track && typeof MediaStreamTrackProcessor === 'function') {
        reader = new MediaStreamTrackProcessor({ track: track }).readable.getReader();
        readFrames();
      } else {
        readBitmaps();
      }
    } catch {
      readBitmaps();
    }
    requestAnimationFrame(pumpDraw);

    return {
      video: video,
      setDelay: function (ms) { delayMs = ms; },
      stop: function () {
        alive = false;
        if (timer) clearInterval(timer);
        if (reader) reader.cancel().catch(function () {});
        queue.forEach(function (item) {
          if (item.frame) item.frame.close();
          if (item.bmp) item.bmp.close();
        });
        queue.length = 0;
        if (track) { try { track.stop(); } catch { /* 停掉采集轨，不影响元素自己的声音 */ } }
        video.style.opacity = prevOpacity;
        if (forcedPosition) parent.style.position = prevPosition;
        canvas.remove();
        rootData('hpsLag', '');
      },
    };
  }

  function tick() {
    rootData('hpsLip', enabled ? 'on' : 'off');
    if (!enabled) {
      if (active) { active.stop(); active = null; }
      return;
    }
    const video = pick();
    if (active && active.video !== video) { active.stop(); active = null; }
    if (!active && video) active = attach(video);
    if (active) active.setDelay(delayMs);
  }

  window.addEventListener('message', function (e) {
    if (e.source !== window || !e.data || e.data.source !== 'hps-lip') return;
    enabled = !!e.data.enabled;
    const ms = Number(e.data.ms);
    if (Number.isFinite(ms)) delayMs = Math.max(0, Math.min(2000, ms));
    tick();
  });
  setInterval(tick, 500);
})();
