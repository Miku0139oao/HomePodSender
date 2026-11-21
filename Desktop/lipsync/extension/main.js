// 隔离世界：只推迟画面。视频元素继续出声，声音仍按原时刻进系统回环，再由 HomePodSender 推到喇叭。
(function () {
  if (globalThis.__hpsLipInstalled) return;
  globalThis.__hpsLipInstalled = true;

  let enabled = false;
  let delayMs = 500;
  let active = null;
  let failedVideos = new WeakSet();

  function collectVideos(root, out) {
    if (!root || !root.querySelectorAll) return;
    root.querySelectorAll('video').forEach((v) => out.push(v));
    root.querySelectorAll('*').forEach((el) => {
      if (el.shadowRoot) collectVideos(el.shadowRoot, out);
    });
  }

  function pick() {
    const all = [];
    collectVideos(document, all);
    const list = [];
    for (const v of all) {
      if (failedVideos.has(v)) continue;
      if (v.paused || v.readyState < 2) continue;
      if (v.videoWidth < 320 || v.clientWidth < 200 || v.clientHeight < 112) continue;
      list.push(v);
    }
    list.sort((a, b) => {
      const aAudible = !a.muted && a.volume > 0 ? 1 : 0;
      const bAudible = !b.muted && b.volume > 0 ? 1 : 0;
      if (aAudible !== bAudible) return bAudible - aAudible;
      return b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight;
    });
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
    canvas.style.opacity = '0';
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;

    const prevPosition = parent.style.position;
    const forcedPosition = getComputedStyle(parent).position === 'static';
    if (forcedPosition) parent.style.position = 'relative';
    parent.appendChild(canvas);

    const prevOpacity = video.style.opacity;
    let primed = false;
    let alive = true;
    let reader = null;
    let stream = null;
    const queue = [];
    let timer = 0;
    let rafId = 0;
    let lastDrawn = null;
    let layoutDirty = true;
    let drawFails = 0;

    function flushQueue() {
      for (const item of queue) {
        if (item.bmp) item.bmp.close();
      }
      queue.length = 0;
      lastDrawn = null;
      if (primed) {
        primed = false;
        video.style.opacity = prevOpacity;
        canvas.style.opacity = '0';
      }
    }

    function place() {
      layoutDirty = false;
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

    function drawOne(bmp) {
      if (layoutDirty) place();
      const cw = canvas.width;
      const ch = canvas.height;
      const sw = bmp.width || video.videoWidth || cw;
      const sh = bmp.height || video.videoHeight || ch;
      const fit = getComputedStyle(video).objectFit || 'contain';
      let dx = 0, dy = 0, dw = cw, dh = ch;
      if (fit !== 'fill' && sw > 0 && sh > 0) {
        const srcRatio = sw / sh;
        const dstRatio = cw / ch;
        const useContain = fit === 'contain' || fit === 'scale-down';
        if ((useContain && srcRatio > dstRatio) || (!useContain && srcRatio < dstRatio)) {
          dw = cw;
          dh = cw / srcRatio;
          dy = (ch - dh) / 2;
        } else {
          dh = ch;
          dw = ch * srcRatio;
          dx = (cw - dw) / 2;
        }
      }
      try {
        ctx.clearRect(0, 0, cw, ch);
        ctx.drawImage(bmp, dx, dy, dw, dh);
        drawFails = 0;
        return true;
      } catch {
        drawFails++;
        if (drawFails >= 5) {
          failedVideos.add(video);
          if (active && active.video === video) {
            active.stop();
            active = null;
          }
        }
        return false;
      }
    }

    function maxQueueLen(intervalMs) {
      return Math.max(16, Math.ceil((Math.max(delayMs, 200) + 600) / intervalMs) + 8);
    }

    function pushBitmap(t, bmp, intervalMs) {
      if (!alive) {
        bmp.close();
        return;
      }
      queue.push({ t, bmp });
      const cap = maxQueueLen(intervalMs);
      while (queue.length > cap) {
        const old = queue.shift();
        if (old.bmp) old.bmp.close();
      }
    }

    // 丢掉已经比目标更旧的帧，留下「刚好晚 delayMs」的那一张。
    function takeDue(now) {
      while (queue.length > 1 && now - queue[1].t >= delayMs) {
        const old = queue.shift();
        if (old.bmp) old.bmp.close();
      }
      if (!queue.length || now - queue[0].t < delayMs) return null;
      return queue[0];
    }

    function pumpDraw() {
      rafId = 0;
      if (!alive) return;
      if (!document.hidden) {
        const due = takeDue(performance.now());
        if (due && (due !== lastDrawn || layoutDirty)) {
          if (drawOne(due.bmp)) {
            lastDrawn = due;
            showDelayed();
          }
        }
      }
      if (alive) rafId = requestAnimationFrame(pumpDraw);
    }

    async function readFrames() {
      while (alive && reader) {
        let next;
        try { next = await reader.read(); }
        catch { break; }
        if (!next || next.done || !next.value) break;
        const frame = next.value;
        if (!alive || document.hidden) {
          frame.close();
          if (!alive) break;
          continue;
        }
        const t = performance.now();
        let bmp = null;
        try {
          const maxW = Math.max(2, Math.min(1280, canvas.width || video.clientWidth || 1280));
          const fw = frame.displayWidth || video.videoWidth || maxW;
          const fh = frame.displayHeight || video.videoHeight || 720;
          const w = Math.min(fw, maxW);
          const h = Math.max(2, Math.round(w * fh / fw));
          bmp = await createImageBitmap(frame, { resizeWidth: w, resizeHeight: h, resizeQuality: 'low' });
        } catch {
          bmp = null;
        } finally {
          // 立刻归还硬件解码器帧，避免耗尽 Chromium 硬解缓冲池
          frame.close();
        }
        if (bmp) pushBitmap(t, bmp, 16);
      }
    }

    function readBitmaps() {
      let failures = 0;
      async function step() {
        timer = 0;
        if (!alive) return;
        if (!document.hidden && video.readyState >= 2) {
          const w = Math.max(2, Math.min(640, canvas.width || video.clientWidth || 640));
          const h = Math.max(2, Math.round(w * (video.videoHeight || 9) / (video.videoWidth || 16)));
          const t = performance.now();
          try {
            const bmp = await createImageBitmap(video, { resizeWidth: w, resizeHeight: h, resizeQuality: 'low' });
            failures = 0;
            pushBitmap(t, bmp, 40);
          } catch {
            failures++;
            if (failures >= 5) {
              failedVideos.add(video);
              if (active && active.video === video) {
                active.stop();
                active = null;
              }
              return;
            }
          }
        }
        if (alive) timer = setTimeout(step, 40);
      }
      timer = setTimeout(step, 40);
    }

    const onLayout = () => { layoutDirty = true; };
    const onSeek = () => { flushQueue(); };
    let ro = null;
    if (typeof ResizeObserver === 'function') {
      ro = new ResizeObserver(onLayout);
      ro.observe(video);
      ro.observe(parent);
    }
    window.addEventListener('resize', onLayout, { passive: true });
    video.addEventListener('seeking', onSeek);
    video.addEventListener('seeked', onSeek);

    place();
    try {
      if (typeof video.captureStream === 'function') {
        stream = video.captureStream();
        const videoTrack = stream.getVideoTracks()[0] || null;
        // 立刻停掉不需要的音轨，只保留视频采集轨
        stream.getAudioTracks().forEach((t) => { try { t.stop(); } catch {} });
        if (videoTrack && typeof MediaStreamTrackProcessor === 'function') {
          reader = new MediaStreamTrackProcessor({ track: videoTrack }).readable.getReader();
          readFrames();
        } else {
          stream.getTracks().forEach((t) => { try { t.stop(); } catch {} });
          stream = null;
          readBitmaps();
        }
      } else {
        readBitmaps();
      }
    } catch {
      if (stream) {
        stream.getTracks().forEach((t) => { try { t.stop(); } catch {} });
        stream = null;
      }
      readBitmaps();
    }
    rafId = requestAnimationFrame(pumpDraw);

    return {
      video,
      parent,
      setDelay(ms) { delayMs = ms; },
      stop() {
        alive = false;
        if (timer) { clearTimeout(timer); timer = 0; }
        if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
        if (ro) { ro.disconnect(); ro = null; }
        window.removeEventListener('resize', onLayout);
        video.removeEventListener('seeking', onSeek);
        video.removeEventListener('seeked', onSeek);
        if (reader) { reader.cancel().catch(() => {}); reader = null; }
        flushQueue();
        if (stream) {
          stream.getTracks().forEach((t) => { try { t.stop(); } catch {} });
          stream = null;
        }
        video.style.opacity = prevOpacity;
        if (forcedPosition) parent.style.position = prevPosition;
        canvas.remove();
      },
    };
  }

  function tick() {
    if (!enabled || delayMs <= 0 || document.hidden) {
      if (active) { active.stop(); active = null; }
      return;
    }
    const video = pick();
    if (active && (active.video !== video || active.parent !== video.parentElement)) {
      active.stop();
      active = null;
    }
    if (!active && video) active = attach(video);
    if (active) active.setDelay(delayMs);
  }

  globalThis.__hpsLipUpdate = function (nextEnabled, nextMs) {
    enabled = !!nextEnabled;
    const ms = Number(nextMs);
    if (Number.isFinite(ms)) delayMs = Math.max(0, Math.min(2000, ms));
    tick();
  };

  document.addEventListener('visibilitychange', tick);
  setInterval(tick, 500);
})();
