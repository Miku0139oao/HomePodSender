import { useEffect, useRef } from 'react';
import Orb from './Orb.jsx';
import { LAYOUT, preview } from './layout.js';

/*
 * 启动动画（全屏透明、鼠标穿透的独立窗口）：
 * 屏幕中央浮现 Siri 光团 → 停顿“说话” → 沿弧线飞向右下角面板里的光球位置并缩小；
 * 落地颜色跟随当前推流状态（播放中 = 光团，连接中 = 半色，未播放 = 透明白球），
 * 与面板光球完全重合后通知 Rust 交接。
 */

/** 与面板一致：播放中 1，连接/重连中 0.55，其余 0 */
function colorForState(state) {
  if (state === 'Streaming') return 1;
  if (state === 'Connecting' || state === 'Reconnecting') return 0.55;
  return 0;
}

const BIG = 300; // 起始画布尺寸（球径 = 0.74 × 300 ≈ 222px）
const T_APPEAR = 720;
const T_HOLD = 620;
const T_FLY = 880;
export const INTRO_TOTAL = T_APPEAR + T_HOLD + T_FLY;

const clamp01 = (x) => Math.max(0, Math.min(1, x));
const easeOutBack = (x) => {
  const c1 = 1.55;
  const c3 = c1 + 1;
  return 1 + c3 * (x - 1) ** 3 + c1 * (x - 1) ** 2;
};
const easeInOutCubic = (x) => (x < 0.5 ? 4 * x * x * x : 1 - (-2 * x + 2) ** 3 / 2);

function frameAt(ms, cfg) {
  const W = window.innerWidth;
  const H = window.innerHeight;
  const cx = W / 2;
  const cy = H / 2;
  if (ms < T_APPEAR) {
    const p = clamp01(ms / T_APPEAR);
    return { x: cx, y: cy, s: 0.18 + 0.82 * easeOutBack(p), o: clamp01(p * 2.4), color: 1, energy: 0.2 + 0.45 * p };
  }
  if (ms < T_APPEAR + T_HOLD) {
    const p = (ms - T_APPEAR) / T_HOLD;
    return {
      x: cx, y: cy, s: 1 + 0.025 * Math.sin(p * Math.PI * 2), o: 1, color: 1,
      energy: 0.55 + 0.35 * Math.abs(Math.sin(p * Math.PI * 3)),
    };
  }
  const p = clamp01((ms - T_APPEAR - T_HOLD) / T_FLY);
  const e = easeInOutCubic(p);
  // 二次贝塞尔：控制点在起终点之间偏上，形成向右下“抛落”的弧线
  const qx = cx + (cfg.tx - cx) * 0.55;
  const qy = Math.min(cy, cfg.ty) - H * 0.16;
  const x = (1 - e) * (1 - e) * cx + 2 * (1 - e) * e * qx + e * e * cfg.tx;
  const y = (1 - e) * (1 - e) * cy + 2 * (1 - e) * e * qy + e * e * cfg.ty;
  const s = 1 + (cfg.size / BIG - 1) * e;
  const color = 1 + (cfg.endColor - 1) * clamp01((p - 0.25) / 0.75);
  const energy = 0.6 * (1 - e) + cfg.endEnergy * e;
  return { x, y, s, o: 1, color, energy };
}

export default function Intro() {
  // 动画参数只建一次（落地颜色会随推流状态被改写，逐帧读取）
  const cfgRef = useRef(null);
  if (!cfgRef.current) {
    cfgRef.current = {
      tx: window.innerWidth - 160,
      ty: window.innerHeight - 320,
      size: LAYOUT.orbSize,
      endColor: 0,
      endEnergy: 0,
      ...(window.__HPS_INTRO__ || {}),
    };
  }
  const cfg = cfgRef.current;
  const frozen = preview.t >= 0;
  const first = frameAt(frozen ? preview.t * 1000 : 0, cfg);
  const wrapRef = useRef(null);
  const colorRef = useRef(first.color);
  const energyRef = useRef(first.energy);

  // 动画进行中持续跟踪推流状态，更新落地颜色
  useEffect(() => {
    if (frozen || !window.hps) return undefined;
    const setState = (state) => {
      if (!state) return;
      cfg.endColor = colorForState(state);
      cfg.endEnergy = state === 'Streaming' ? 0.35 : 0;
    };
    const off = window.hps.onEvent((ev) => {
      if (ev.ev === 'ready' || ev.ev === 'state') setState(ev.state);
    });
    window.hps.call('state').then((p) => setState(p.state)).catch(() => {});
    return off;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const el = wrapRef.current;
    const apply = (f) => {
      el.style.transform = `translate(${f.x - BIG / 2}px, ${f.y - BIG / 2}px) scale(${f.s})`;
      el.style.opacity = String(f.o);
      colorRef.current = f.color;
      energyRef.current = f.energy;
    };
    apply(first);
    if (frozen) return undefined;
    const t0 = performance.now();
    let raf = 0;
    let near = false;
    let done = false;
    let fadeTimer = 0;
    const tick = (now) => {
      const ms = now - t0;
      apply(frameAt(Math.min(ms, INTRO_TOTAL), cfg));
      // 开始飞行时让 Rust 提前显示待命的面板窗口
      if (!near && ms >= T_APPEAR + T_HOLD) { near = true; window.hps?.introNear(); }
      if (ms >= INTRO_TOTAL) {
        if (!done) {
          done = true;
          window.hps?.introDone();
          // 面板光球接替后（同位置同样子）再淡掉自己，避免两层叠加或中间空一帧
          fadeTimer = setTimeout(() => {
            el.style.transition = 'opacity 120ms linear';
            el.style.opacity = '0';
          }, 90);
        }
        return;
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => { cancelAnimationFrame(raf); clearTimeout(fadeTimer); };
    // 只在挂载时运行一次
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className={`intro${frozen ? ' still' : ''}`}>
      <span className="ripple r1" />
      <span className="ripple r2" />
      <div className="intro-orb" ref={wrapRef} style={{ width: BIG, height: BIG }}>
        <Orb size={BIG} colorRef={colorRef} energyRef={energyRef} radius={LAYOUT.orbRadius} still={frozen} />
      </div>
    </div>
  );
}
