import React from 'react';
import { createRoot } from 'react-dom/client';
import './tauri.js';
import './styles.css';
import { preview } from './layout.js';
import { readLanguage, translate } from './i18n.js';
import Panel from './Panel.jsx';
import Intro from './Intro.jsx';
import Orb from './Orb.jsx';

// The panel is an application surface, not a web page: never expose WebView2's
// browser/reload/save context menu to the user.
window.addEventListener('contextmenu', (event) => {
  event.preventDefault();
  event.stopPropagation();
}, { capture: true });

// 视图：intro = 启动动画窗口；panel = 右下角托盘面板（默认）；orbtest = 光球调试页
const view = window.__HPS_VIEW__ || preview.view;
document.documentElement.dataset.view = view;
if (preview.active && !window.__HPS_PREVIEW__) document.documentElement.dataset.browserPreview = '1';

// 浏览器预览时可用 ?zoom=2 放大检查细节（光球画布会按倍率提高分辨率）
const zoom = Number(new URLSearchParams(window.location.search).get('zoom') || 1);
if (zoom > 1) {
  document.documentElement.style.zoom = String(zoom);
  window.__HPS_ZOOM__ = zoom;
}

function OrbTest() {
  // ?bg=dark / light 切换底色，检查光团在深浅桌面上的效果
  document.documentElement.dataset.bg = new URLSearchParams(window.location.search).get('bg') || 'warm';
  const language = readLanguage();
  const t = (key) => translate(language, key);
  const cells = [
    { label: language === 'en' ? 'Transparent orb (idle)' : '透明白球（未播放）', color: 0, energy: 0 },
    { label: language === 'en' ? 'Orb · calm' : '光團 · 安靜', color: 1, energy: 0.1 },
    { label: language === 'en' ? 'Orb · medium' : '光團 · 中等', color: 1, energy: 0.5 },
    { label: language === 'en' ? 'Orb · loud' : '光團 · 響亮', color: 1, energy: 0.95 },
    { label: t('status.connecting').replace('…', '') + (language === 'en' ? ' (half color)' : '（半色）'), color: 0.55, energy: 0.15 },
  ];
  return (
    <div className="orbtest">
      {cells.map((c) => (
        <figure key={c.label}>
          <Orb size={220} color={c.color} energy={c.energy} still={preview.still} />
          <figcaption>{c.label}</figcaption>
        </figure>
      ))}
    </div>
  );
}

const root = createRoot(document.getElementById('root'));
if (view === 'intro') root.render(<Intro />);
else if (view === 'orbtest') root.render(<OrbTest />);
else root.render(<Panel />);
