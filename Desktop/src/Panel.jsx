import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import DotText from './DotText.jsx';
import Orb from './Orb.jsx';
import { BackIcon, CheckIcon, GearIcon, PauseGlyph, PlayGlyph, PlusIcon, SpeakerIcon } from './icons.jsx';
import { CARDS_CLIP, LAYOUT, preview } from './layout.js';

/*
 * 右下角托盘面板：复刻参考图的三张玻璃小组件。
 *  · 左侧高卡（对应 “NEW PLAN”）：点阵字 AIR / PLAY + 胶囊勾选行 = AirPlay 设备列表；翻面为 SET / UP 设置。
 *  · 右上卡（对应 “Image Notes”）：Siri 光球 = 播放/暂停（连接/断开），下方标题 + 等宽小字状态。
 *  · 右下卡（对应 “Your files”）：点阵音量阶梯 + 圆形静音键 + “音量 32%”。
 */

const LATENCIES = [500, 1000, 1500, 2000];
const LADDER = 14;

/** 预览数据：?pair=broken 时两台都报告自己是组长且未成对（与 HomePod 当前的实际报告一致） */
function mockDevices() {
  const broken = preview.pair === 'broken';
  return [
    { name: '起居室 (2)', ip: '192.168.50.71', role: '组长', pairId: 'pair1', unpaired: broken },
    { name: '起居室', ip: '192.168.50.23', role: broken ? '组长' : '成员', pairId: 'pair1', unpaired: broken },
  ];
}

function previewConnection(mode) {
  switch (mode) {
    case 'streaming': return { state: 'Streaming', status: '已连接，正在播放系统声音' };
    case 'connecting': return { state: 'Connecting', status: '正在连接 192.168.50.71 …' };
    case 'reconnecting': return { state: 'Reconnecting', status: '连接中断，正在重连（第 2 次）…' };
    case 'failed': return { state: 'Disconnected', status: '连接失败：HomePod 无响应，请检查 IP 与网络' };
    default: return { state: 'Disconnected', status: '未连接' };
  }
}

function dbToLevel(v) {
  const db = 20 * Math.log10(Math.max(v, 1e-5));
  return Math.max(0, Math.min(1, (db + 50) / 50));
}

function formatTime(sec) {
  const s = Math.max(0, Math.floor(sec));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  return h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(ss).padStart(2, '0')}`
    : `${String(m).padStart(2, '0')}:${String(ss).padStart(2, '0')}`;
}

export default function Panel() {
  const isPreview = preview.active;
  const [conn, setConn] = useState(isPreview ? previewConnection(preview.state) : { state: 'Disconnected', status: '未连接' });
  const [config, setConfig] = useState(isPreview ? {
    ip: '192.168.50.71',
    ips: preview.pair === 'broken' ? ['192.168.50.71', '192.168.50.23'] : ['192.168.50.71'],
    volumePercent: preview.volume, latencyMs: 1500, autoReconnect: true, autoConnectOnStart: false,
    showBoard: preview.board, videoSync: false, devices: mockDevices(),
  } : { ip: '', ips: [], volumePercent: 20, latencyMs: 1500, autoReconnect: true, autoConnectOnStart: false, showBoard: true, videoSync: false, devices: [] });
  const [volume, setVolumeState] = useState(isPreview ? preview.volume : 20);
  const [page, setPage] = useState(preview.page === 'settings' ? 'settings' : 'devices');
  const [scan, setScan] = useState({ active: false, progress: 0, message: '' });
  const [adding, setAdding] = useState(false);
  const [ipInput, setIpInput] = useState('');
  const [hint, setHint] = useState('');
  const [elapsed, setElapsed] = useState(isPreview && preview.state === 'streaming' ? 207 : 0);
  const [loaded, setLoaded] = useState(isPreview);
  // 面板开合：standby = 全透明待命（窗口隐藏或等待启动动画落地）；open = 展开；closing = 收拢中
  const [phase, setPhase] = useState(isPreview || !window.hps ? 'open' : 'standby');
  // 展开方式：land = 启动动画落地（光球直接接替，不再弹入）；show = 托盘唤出（光球弹入后绽开）
  const [entry, setEntry] = useState('land');

  const levelRef = useRef(isPreview ? preview.level : 0);
  const energyRef = useRef(isPreview ? preview.level : 0);
  const muteMemory = useRef(volume || 32);
  const dragging = useRef(false);
  const volTimer = useRef(null);
  const hintTimer = useRef(null);
  const ipRef = useRef(null);

  const mode = useMemo(() => {
    if (conn.state === 'Streaming') return 'streaming';
    if (conn.state === 'Connecting') return 'connecting';
    if (conn.state === 'Reconnecting') return 'reconnecting';
    if (/连接失败|连接已断开|重连失败|核心进程/.test(conn.status)) return 'failed';
    return 'idle';
  }, [conn]);
  const live = mode === 'streaming';
  const busy = mode === 'connecting' || mode === 'reconnecting';

  const targets = config.ips?.length ? config.ips : (config.ip ? [config.ip] : []);
  const both = targets.length >= 2;
  const device = config.devices.find((d) => d.ip === config.ip) || config.devices.find((d) => targets.includes(d.ip));
  const deviceName = both ? '两台一起' : (device?.name || (config.ip ? 'HomePod' : '未选择设备'));
  // 未成对且只有一台目标：只会从这一台出声。两台目标时各推一路完整声音
  const unpaired = !!(device?.unpaired || config.devices.some((d) => targets.includes(d.ip) && d.unpaired));

  /* ---------- sidecar 事件 ---------- */
  useEffect(() => {
    if (isPreview || !window.hps) return undefined;
    const apply = (cfg) => {
      setConfig(cfg);
      if (!dragging.current) {
        setVolumeState(cfg.volumePercent);
        if (cfg.volumePercent > 0) muteMemory.current = cfg.volumePercent;
      }
    };
    const off = window.hps.onEvent((ev) => {
      switch (ev.ev) {
        case 'ready': apply(ev.config); setConn({ state: ev.state, status: ev.status }); setLoaded(true); break;
        case 'state': setConn({ state: ev.state, status: ev.status }); break;
        case 'config': setConfig(ev.config); break;
        case 'devices': setConfig((c) => ({ ...c, devices: ev.devices })); break;
        case 'level': levelRef.current = ev.value; break;
        case 'scan':
          if (ev.phase === 'start') setScan({ active: true, progress: 0, message: '' });
          else if (ev.phase === 'progress') setScan((s) => ({ ...s, active: true, progress: ev.progress }));
          else if (ev.phase === 'done') setScan({ active: false, progress: 100, message: ev.count ? `找到 ${ev.count} 台` : '没有找到' });
          break;
        case 'error': setConn({ state: 'Disconnected', status: ev.message }); setLoaded(true); break;
        default: break;
      }
    });
    window.hps.call('state')
      .then((p) => { setConn({ state: p.state, status: p.status }); apply(p.config); setLoaded(true); })
      .catch((e) => setConn({ state: 'Disconnected', status: e.message }));
    return off;
  }, [isPreview]);

  /* ---------- 面板开合（由 Rust 通知） ---------- */
  useEffect(() => {
    if (isPreview || !window.hps) return undefined;
    const off = window.hps.onPanel((what) => {
      if (what === 'land') {
        // 窗口已提前显示并以待命态渲染过，直接展开即可触发绽开过渡
        setEntry('land');
        setPhase('open');
      } else if (what === 'show') {
        // 窗口刚从隐藏变为显示：先以待命态渲染一帧，再展开，保证过渡能播出来
        setEntry('show');
        setPhase('standby');
        requestAnimationFrame(() => requestAnimationFrame(() => setPhase('open')));
      } else if (what === 'hide') {
        setPhase((p) => (p === 'open' ? 'closing' : p));
      }
    });
    // “show/land” 事件可能早于本页开始监听：加载完主动问一次
    window.hps.panelVisible().then((v) => { if (v) setPhase((p) => (p === 'standby' ? 'open' : p)); });
    return off;
  }, [isPreview]);

  useEffect(() => {
    if (phase !== 'closing') return undefined;
    const id = setTimeout(() => {
      setPhase('standby');
      setAdding(false);
      window.hps?.panelHidden();
    }, 300);
    return () => clearTimeout(id);
  }, [phase]);

  useEffect(() => {
    const onKey = (e) => {
      if (e.key !== 'Escape') return;
      if (adding) { setAdding(false); return; }
      if (window.hps) setPhase((p) => (p === 'open' ? 'closing' : p));
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [adding]);

  /* ---------- 计时 ---------- */
  useEffect(() => {
    if (mode !== 'streaming') { setElapsed(0); return undefined; }
    if (isPreview) { setElapsed(207); return undefined; }
    const t0 = Date.now();
    const id = setInterval(() => setElapsed((Date.now() - t0) / 1000), 500);
    return () => clearInterval(id);
  }, [mode, isPreview]);

  /* ---------- 光球能量：跟随实时电平 ---------- */
  useEffect(() => {
    if (isPreview) { energyRef.current = live ? preview.level : 0; return undefined; }
    let raf = 0;
    let shown = 0;
    const tick = () => {
      const target = live ? dbToLevel(levelRef.current) : busy ? 0.15 : 0;
      shown += (target - shown) * (target > shown ? 0.35 : 0.08);
      energyRef.current = shown;
      raf = requestAnimationFrame(tick);
    };
    tick();
    return () => cancelAnimationFrame(raf);
  }, [live, busy, isPreview]);

  useEffect(() => { if (adding) ipRef.current?.focus(); }, [adding]);

  /* ---------- 操作 ---------- */
  const changeVolume = useCallback((v) => {
    v = Math.max(0, Math.min(100, Math.round(v)));
    setVolumeState(v);
    if (v > 0) muteMemory.current = v;
    if (isPreview || !window.hps) return;
    clearTimeout(volTimer.current);
    volTimer.current = setTimeout(() => window.hps.call('setVolume', { value: v }).catch(() => {}), 60);
  }, [isPreview]);

  function toggleMute() { changeVolume(volume >= 0.5 ? 0 : muteMemory.current || 32); }

  async function togglePlay() {
    setHint('');
    if (isPreview || !window.hps || mode === 'connecting') return;
    try {
      if (live || mode === 'reconnecting') await window.hps.call('disconnect');
      else {
        if (!config.ip) { setHint('先在左侧选择设备'); return; }
        await window.hps.call('connect');
      }
    } catch { /* 失败原因由状态事件带回 */ }
  }

  async function pickDevice(d) {
    setHint('');
    clearTimeout(hintTimer.current);
    if (isPreview) { setConfig((c) => ({ ...c, ip: d.ip })); return; }
    try {
      const r = await window.hps.call('selectDevice', { ip: d.ip });
      if (r?.hint) {
        setHint(r.hint);
        hintTimer.current = setTimeout(() => setHint(''), 6000);
      }
    } catch (e) { setHint(e.message); }
  }

  async function doScan() {
    if (isPreview) { setScan({ active: false, progress: 100, message: '找到 2 台' }); return; }
    setScan({ active: true, progress: 0, message: '' });
    try { await window.hps.call('scan'); }
    catch (e) { setScan({ active: false, progress: 0, message: e.message }); }
  }

  async function addIp() {
    const raw = ipInput.trim();
    if (!raw) { setAdding(false); return; }
    if (isPreview) { setIpInput(''); setAdding(false); return; }
    try {
      const r = await window.hps.call('addDevice', { ip: raw });
      if (r?.hint) {
        setHint(r.hint);
        clearTimeout(hintTimer.current);
        hintTimer.current = setTimeout(() => setHint(''), 6000);
      }
      setIpInput('');
      setAdding(false);
    } catch (e) { setScan((s) => ({ ...s, message: e.message })); }
  }

  function setOption(key) {
    // showBoard 缺省（旧配置没有这个字段）视为开启
    const cur = key === 'showBoard' ? config.showBoard !== false : !!config[key];
    const next = { ...config, [key]: !cur };
    setConfig(next);
    if (!isPreview) {
      window.hps?.call('setOptions', {
        autoReconnect: next.autoReconnect, autoConnectOnStart: next.autoConnectOnStart,
        showBoard: next.showBoard !== false, videoSync: !!next.videoSync,
      }).then((r) => {
        if (!r?.hint) return;
        setHint(r.hint);
        clearTimeout(hintTimer.current);
        hintTimer.current = setTimeout(() => setHint(''), r.hint.length > 40 ? 20000 : 8000);
      }).catch(() => {});
    }
  }

  function cycleLatency() {
    const i = LATENCIES.indexOf(config.latencyMs);
    const ms = LATENCIES[(i + 1) % LATENCIES.length];
    setConfig((c) => ({ ...c, latencyMs: ms }));
    if (!isPreview) window.hps?.call('setLatency', { value: ms }).catch(() => {});
  }

  /* ---------- 音量阶梯拖动 ---------- */
  const ladderRef = useRef(null);
  function volumeFromPointer(e) {
    const r = ladderRef.current.getBoundingClientRect();
    const x = Math.max(0, Math.min(1, (e.clientX - r.left) / r.width));
    return x * 100;
  }
  function onLadderDown(e) {
    dragging.current = true;
    e.currentTarget.setPointerCapture(e.pointerId);
    changeVolume(volumeFromPointer(e));
  }
  function onLadderMove(e) { if (dragging.current) changeVolume(volumeFromPointer(e)); }
  function onLadderUp() { dragging.current = false; }
  function onVolumeWheel(e) { changeVolume(volume + (e.deltaY < 0 ? 2 : -2)); }

  /* ---------- 文案 ---------- */
  let sub = '轻点光球 · 推流';
  let subClass = '';
  if (!loaded) sub = '正在启动…';
  else if (hint) { sub = hint; subClass = 'warn'; }
  else if (live) {
    sub = both ? `${formatTime(elapsed)} · 两台同时` : unpaired ? `${formatTime(elapsed)} · 仅此一台` : `${formatTime(elapsed)} · 实时`;
    subClass = unpaired && !both ? 'warn' : '';
  }
  else if (mode === 'connecting') sub = both ? '正在连接两台…' : '正在连接…';
  else if (mode === 'reconnecting') sub = '重新连接中…';
  else if (mode === 'failed') { sub = '连接失败'; subClass = 'warn'; }
  else if (both) sub = '两台同时';
  else if (unpaired) { sub = '未组立体声对'; subClass = 'warn'; }

  const lit = Math.round((volume / 100) * LADDER);
  const scanLabel = scan.active ? `扫描中 ${scan.progress || 0}%` : (scan.message || '扫描设备');
  const orbColor = live ? 1 : busy ? 0.55 : 0;
  const showBoard = config.showBoard !== false;

  // 板内坐标系下的光球中心（板相对窗口偏移 margin）
  const style = { '--ox': `${LAYOUT.orbX - LAYOUT.margin}px`, '--oy': `${LAYOUT.orbY - LAYOUT.margin}px` };

  return (
    <div className={`shell phase-${phase} entry-${entry} mode-${mode}${showBoard ? '' : ' no-board'}${preview.still ? ' still' : ''}`} style={style}>
      <div className="board">
        {/* 关闭背景卡片时，柔焦背景只在三张卡片的形状里显示 */}
        <div className="backdrop" aria-hidden="true" style={showBoard ? undefined : { clipPath: CARDS_CLIP }}>
          <span className="bd bd-base" />
          <span className="bd bd-umber" />
          <span className="bd bd-umber2" />
          <span className="bd bd-skin" />
          <span className="bd bd-olive" />
          <span className="bd bd-olive2" />
          <span className="bd bd-cream" />
          <span className="bd bd-aura" />
          <span className="bd bd-grain" />
        </div>

        {/* ---------- 左：AirPlay 设备 / 设置 ---------- */}
        <section className={`card tall glass${page === 'settings' ? ' flipped' : ''}`}>
          <div className="flipper">
            {/* React 19 的 inert 是布尔属性（传空串会被当成 false 而不写入） */}
            <div className="face face-front" aria-hidden={page !== 'devices'} inert={page !== 'devices'}>
              <button className="mini mini-right" aria-label="设置" title="设置" onClick={() => setPage('settings')}><GearIcon size={13} /></button>
              <DotText lines={['AIR', 'PLAY']} pitch={5} dot={3.6} className="headline" />
              <div className="rows">
                <div className="device-scroll">
                  {config.devices.length === 0 && (
                    <div className="row ghost"><span className="row-label dim">还没有设备</span></div>
                  )}
                  {config.devices.map((d) => {
                    const on = targets.includes(d.ip);
                    return (
                      <button className={`row${on ? ' on' : ''}${d.role === '成员' ? ' member' : ''}`} key={d.ip} onClick={() => pickDevice(d)}
                        title={both && d.unpaired ? '两台一起推流，每只播放完整声音，不是左右声道' : d.unpaired ? '两台 HomePod 没有组成立体声对，只会从所选的这一台出声；请在家庭 App 里重新设置立体声对' : d.role === '成员' ? '立体声对的成员，点击会自动改选组长' : d.ip}>
                        <span className={`check${on ? ' on' : ''}`}>{on && <CheckIcon />}</span>
                        <span className="row-label">{d.name || 'HomePod'}</span>
                        {d.role && <span className="row-tag mono">{d.role}</span>}
                      </button>
                    );
                  })}
                </div>
                {adding ? (
                  <div className="row input-row">
                    <span className="check plus"><PlusIcon /></span>
                    <input
                      ref={ipRef} className="row-input mono" placeholder="192.168.x.x" value={ipInput}
                      onChange={(e) => setIpInput(e.target.value)}
                      onKeyDown={(e) => { if (e.key === 'Enter') addIp(); }}
                      aria-label="手动输入 IP"
                    />
                    <button className="row-chip" onClick={addIp}>添加</button>
                  </div>
                ) : (
                  <div className="row action">
                    <button className="row-main" onClick={doScan} disabled={scan.active}>
                      <span className={`check plus${scan.active ? ' spin' : ''}`}><PlusIcon /></span>
                      <span className="row-label">{scanLabel}</span>
                    </button>
                    <button className="row-chip mono" onClick={() => setAdding(true)} title="手动输入 IP">IP</button>
                  </div>
                )}
              </div>
            </div>

            <div className="face face-back" aria-hidden={page !== 'settings'} inert={page !== 'settings'}>
              <button className="mini mini-left" aria-label="返回" title="返回" onClick={() => setPage('devices')}><BackIcon size={13} /></button>
              {/* 设置面有五行选项，标题略小并上移，行高也收一点 */}
              <DotText lines={['SET', 'UP']} pitch={4.2} dot={3.1} className="headline headline-sm" />
              <div className="rows rows-set">
                <button className={`row${config.autoReconnect ? ' on' : ''}`} onClick={() => setOption('autoReconnect')}>
                  <span className={`check${config.autoReconnect ? ' on' : ''}`}>{config.autoReconnect && <CheckIcon />}</span>
                  <span className="row-label">断线自动重连</span>
                </button>
                <button className={`row${config.autoConnectOnStart ? ' on' : ''}`} onClick={() => setOption('autoConnectOnStart')}>
                  <span className={`check${config.autoConnectOnStart ? ' on' : ''}`}>{config.autoConnectOnStart && <CheckIcon />}</span>
                  <span className="row-label">启动时自动连接</span>
                </button>
                <button className="row" onClick={cycleLatency} title="延迟越大越抗网络抖动；看视频请选小一点。下次连接生效。对齐画面用的就是这个数">
                  <span className="check ring"><i /></span>
                  <span className="row-label">播放延迟</span>
                  <span className="row-tag mono">{config.latencyMs / 1000}s</span>
                </button>
                <button className={`row${config.videoSync ? ' on' : ''}`} onClick={() => setOption('videoSync')}
                  title="插件只推迟 Chrome 网页画面，声音仍马上送出，口型按上面的播放延迟对齐。这台电脑自动安装会弹出一次管理员确认；若取消，就按提示手动加载插件。打开后保持本程序运行，并完全退出再打开一次 Chrome">
                  <span className={`check${config.videoSync ? ' on' : ''}`}>{config.videoSync && <CheckIcon />}</span>
                  <span className="row-label">对齐画面</span>
                  {config.videoSync && <span className="row-tag mono">{config.latencyMs / 1000}s</span>}
                </button>
                <button className={`row${showBoard ? ' on' : ''}`} onClick={() => setOption('showBoard')} title="关掉后三张卡片各自悬浮在桌面上">
                  <span className={`check${showBoard ? ' on' : ''}`}>{showBoard && <CheckIcon />}</span>
                  <span className="row-label">背景卡片</span>
                </button>
              </div>
            </div>
          </div>
        </section>

        {/* ---------- 右上：正在播放（光球本体在板外层，见下方 orb-btn） ---------- */}
        <section className="card now glass">
          <div className="now-text">
            <div className="now-title" title={both ? targets.map((ip) => config.devices.find((d) => d.ip === ip)?.name || ip).join(' · ') : config.ip}>{deviceName}</div>
            <div className={`now-sub mono ${subClass}`} title={hint ? hint : mode === 'failed' ? conn.status : both ? '每只 HomePod 播放同一路完整声音。左右声道要在家庭 App 里组成立体声对' : unpaired ? '两台 HomePod 没有组成立体声对，只会从当前设备出声' : undefined}>{sub}</div>
          </div>
        </section>

        {/* ---------- 右下：音量 ---------- */}
        <section className="card vol glass" onWheel={onVolumeWheel}>
          <div
            className="ladder" ref={ladderRef} role="slider" aria-label="音量" aria-valuemin={0} aria-valuemax={100} aria-valuenow={volume}
            onPointerDown={onLadderDown} onPointerMove={onLadderMove} onPointerUp={onLadderUp} onPointerCancel={onLadderUp}
          >
            {Array.from({ length: LADDER }, (_, i) => {
              const h = 1 + Math.round((i * 4) / (LADDER - 1));
              return (
                <span className="lcol" key={i}>
                  {Array.from({ length: 5 }, (__, j) => (
                    <i key={j} className={j >= 5 - h ? (i < lit ? 'on' : 'off') : 'none'} />
                  ))}
                </span>
              );
            })}
          </div>
          <div className="vol-row">
            <button className="round" aria-label="静音 / 恢复" title="静音 / 恢复" onClick={toggleMute}>
              <SpeakerIcon size={17} muted={volume < 0.5} />
            </button>
            <span className="vol-label">音量</span>
            <span className="vol-val mono">{volume}%</span>
          </div>
          <span className="iris" aria-hidden="true" />
        </section>
      </div>

      {/*
        Siri 光球放在板的外层，与启动动画落点完全重合：展开时板从光球处以圆形向外绽开，
        收起时板先缩回光球、光球再淡出。
      */}
      <button
        className={`orb-btn${busy ? ' busy' : ''}`}
        style={{ left: LAYOUT.orbX, top: LAYOUT.orbY }}
        aria-label={live || mode === 'reconnecting' ? '断开' : '连接'}
        title={live || mode === 'reconnecting' ? '断开' : '连接 HomePod'}
        disabled={!loaded || mode === 'connecting'}
        onClick={togglePlay}
      >
        <Orb size={LAYOUT.orbSize} color={orbColor} energyRef={energyRef} still={preview.still} radius={LAYOUT.orbRadius} className="orb" />
        <span className="orb-ring" />
        <span className="orb-glyph">{live || mode === 'reconnecting' ? <PauseGlyph size={20} /> : <PlayGlyph size={20} />}</span>
      </button>
    </div>
  );
}
