/*
 * 面板几何（CSS 像素）与预览参数。
 * 窗口 = 板（424×360）+ 四周 22px 阴影边距；Rust 侧按 WIN_W/WIN_H 建窗口，并用 ORB_X/ORB_Y 计算启动动画的落点。
 */
const margin = 22;
const pad = 14;
const gap = 10;
const tallW = 206;
const rightW = 180;
const nowH = 196;

export const LAYOUT = {
  margin,
  boardW: 424,
  boardH: 360,
  winW: 424 + margin * 2,
  winH: 360 + margin * 2,
  orbSize: 132,
  orbRadius: 0.74,
  // 光球中心：相对窗口（右上卡水平居中、距卡顶 74px）
  orbX: margin + pad + tallW + gap + rightW / 2,
  orbY: margin + pad + 74,
  nowH,
};

/* 三张卡在板内的几何（与 styles.css 的 .tall / .now / .vol 一致）。
 * 关闭背景卡片时，柔焦背景只在这三个圆角矩形里显示，卡片各自悬浮在桌面上。 */
export const CARDS = [
  { x: 14, y: 14, w: 206, h: 332, r: 34 },
  { x: 230, y: 14, w: 180, h: 196, r: 31 },
  { x: 230, y: 220, w: 180, h: 126, r: 31 },
];
const roundRect = ({ x, y, w, h, r }) =>
  `M${x + r},${y}H${x + w - r}A${r},${r} 0 0 1 ${x + w},${y + r}V${y + h - r}A${r},${r} 0 0 1 ${x + w - r},${y + h}`
  + `H${x + r}A${r},${r} 0 0 1 ${x},${y + h - r}V${y + r}A${r},${r} 0 0 1 ${x + r},${y}Z`;
export const CARDS_CLIP = `path('${CARDS.map(roundRect).join('')}')`;

const Q = new URLSearchParams(window.location.search);
const inj = window.__HPS_PREVIEW__ || null;

function pick(name, fallback) {
  if (inj && inj[name] !== undefined && inj[name] !== null) return inj[name];
  const v = Q.get(name);
  return v === null ? fallback : v;
}

/** 预览模式：截图或在普通浏览器里看界面，不连接核心进程 */
export const preview = {
  active: !!inj || Q.has('preview'),
  view: pick('view', 'panel'),
  state: pick('preview', 'idle'),
  level: Number(pick('level', 0.7)),
  volume: Number(pick('volume', 32)),
  page: pick('sheet', pick('page', 'devices')),
  still: inj ? !!inj.still : Q.get('still') === '1',
  t: Number(pick('t', -1)),
  // board=0：预览“关闭背景卡片”的样子
  board: String(pick('board', '1')) !== '0',
  // pair=broken：预览“两台未组成立体声对”的样子
  pair: String(pick('pair', 'ok')),
};
