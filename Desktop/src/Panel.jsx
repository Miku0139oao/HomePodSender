import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import DotText from './DotText.jsx';
import Orb from './Orb.jsx';
import { BackIcon, CheckIcon, GearIcon, PauseGlyph, PlayGlyph, PlusIcon, SpeakerIcon } from './icons.jsx';
import { localizeMessage, localizeRole, localizeStatus, persistLanguage, readLanguage, translate } from './i18n.js';
import { CARDS_CLIP, LAYOUT, preview } from './layout.js';

/*
 * 右下角托盘面板：复刻参考图的三张玻璃小组件。
 *  · 左侧高卡（对应 “NEW PLAN”）：点阵字 AIR / PLAY + 胶囊勾选行 = AirPlay 设备列表；翻面为 SET / UP 设置。
 *  · 右上卡（对应 “Image Notes”）：Siri 光球 = 播放/暂停（连接/断开），下方标题 + 等宽小字状态。
 *  · 右下卡（对应 “Your files”）：点阵音量阶梯 + 圆形静音键 + “音量 32%”。
 */

const LATENCIES = [0, 100, 250, 500, 1000, 1500, 2000];
const LADDER = 14;

/** 预览数据：?pair=broken 时两台都报告自己是组长且未成对（与 HomePod 当前的实际报告一致） */
function mockDevices() {
  const broken = preview.pair === 'broken';
  return [
    { name: '起居室 (2)', ip: '192.168.50.71', role: '组长', pairId: 'pair1', unpaired: broken },
    { name: '起居室', ip: '192.168.50.23', role: broken ? '组长' : '成员', pairId: 'pair1', unpaired: broken },
  ];
}

function mockAudioDevices() {
  return [
    { id: 'speakers', name: '喇叭 (Realtek(R) Audio)', isDefault: true },
    { id: 'cable', name: 'CABLE Input (VB-Audio Virtual Cable)', isDefault: false },
    { id: 'headphones', name: '耳機 (AirPods Pro)', isDefault: false },
    { id: 'hdmi', name: 'GS27U (NVIDIA High Definition Audio)', isDefault: false },
    { id: 'usb', name: '喇叭 (HyperX QuadCast S)', isDefault: false },
  ];
}

function previewConnection(mode) {
  switch (mode) {
    case 'streaming': return { state: 'Streaming', status: '' };
    case 'connecting': return { state: 'Connecting', status: '' };
    case 'reconnecting': return { state: 'Reconnecting', status: '' };
    case 'failed': return { state: 'Disconnected', status: 'connection failed' };
    default: return { state: 'Disconnected', status: '' };
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

function formatLatency(ms) {
  return `${Number((ms / 1000).toFixed(2))}s`;
}

export default function Panel() {
  const isPreview = preview.active;
  const [conn, setConn] = useState(isPreview ? previewConnection(preview.state) : { state: 'Disconnected', status: '' });
  const [config, setConfig] = useState(isPreview ? {
    ip: '192.168.50.71',
    ips: preview.pair === 'broken' ? ['192.168.50.71', '192.168.50.23'] : ['192.168.50.71'],
    volumePercent: preview.volume, latencyMs: 1500, autoReconnect: true, autoConnectOnStart: false,
    showBoard: preview.board, videoSync: false, devices: mockDevices(),
    audioCaptureDeviceId: '', audioDevices: mockAudioDevices(),
  } : {
    ip: '', ips: [], volumePercent: 20, latencyMs: 1500, autoReconnect: true, autoConnectOnStart: false,
    showBoard: true, videoSync: false, devices: [], audioCaptureDeviceId: '', audioDevices: [],
  });
  const [volume, setVolumeState] = useState(isPreview ? preview.volume : 20);
  const [page, setPage] = useState(preview.page === 'settings' || preview.page === 'audio' ? 'settings' : 'devices');
  const [audioPicker, setAudioPicker] = useState(preview.page === 'audio');
  const [scan, setScan] = useState({ active: false, progress: 0, message: '', count: null });
  const [adding, setAdding] = useState(false);
  const [ipInput, setIpInput] = useState('');
  const [hint, setHint] = useState('');
  const [language, setLanguage] = useState(readLanguage);
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
  const t = useCallback((key, values) => translate(language, key, values), [language]);

  const mode = useMemo(() => {
    if (conn.state === 'Streaming') return 'streaming';
    if (conn.state === 'Connecting') return 'connecting';
    if (conn.state === 'Reconnecting') return 'reconnecting';
    if (/连接失败|连接已断开|重连失败|核心进程|connection failed|connection closed|reconnect failed|core process/i.test(conn.status)) return 'failed';
    return 'idle';
  }, [conn]);
  const live = mode === 'streaming';
  const busy = mode === 'connecting' || mode === 'reconnecting';

  const targets = config.ips?.length ? config.ips : (config.ip ? [config.ip] : []);
  const both = targets.length >= 2;
  const device = config.devices.find((d) => d.ip === config.ip) || config.devices.find((d) => targets.includes(d.ip));
  const deviceName = both ? t('device.both') : (device?.name || (config.ip ? 'HomePod' : t('device.none')));
  // 未成对且只有一台目标：只会从这一台出声。两台目标时各推一路完整声音
  const unpaired = !!(device?.unpaired || config.devices.some((d) => targets.includes(d.ip) && d.unpaired));
  const selectedAudio = config.audioDevices?.find((d) => d.id === config.audioCaptureDeviceId);
  const audioLabel = config.audioCaptureDeviceId
    ? (selectedAudio?.name || t('settings.audioUnavailable'))
    : t('settings.systemDefault');
  const audioOptions = [
    { id: '', name: t('settings.systemDefault'), isDefault: false },
    ...(config.audioDevices || []),
  ];

  useEffect(() => {
    document.documentElement.lang = language;
    document.title = t('app.title');
    window.hps?.setTrayLanguage?.(language);
  }, [language, t]);

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
          if (ev.phase === 'start') setScan({ active: true, progress: 0, message: '', count: null });
          else if (ev.phase === 'progress') setScan((s) => ({ ...s, active: true, progress: ev.progress }));
          else if (ev.phase === 'done') setScan({ active: false, progress: 100, message: '', count: ev.count });
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
      if (audioPicker) { setAudioPicker(false); return; }
      if (adding) { setAdding(false); return; }
      if (window.hps) setPhase((p) => (p === 'open' ? 'closing' : p));
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [adding, audioPicker]);

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
        if (!config.ip) { setHint(t('tip.selectDevice')); return; }
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
    if (isPreview) { setScan({ active: false, progress: 100, message: '', count: 2 }); return; }
    setScan({ active: true, progress: 0, message: '', count: null });
    try { await window.hps.call('scan'); }
    catch (e) { setScan({ active: false, progress: 0, message: e.message, count: null }); }
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

  async function openAudioPicker() {
    setHint('');
    setAudioPicker(true);
    if (isPreview || !window.hps) return;
    try {
      const r = await window.hps.call('refreshAudioDevices');
      if (r?.config) setConfig(r.config);
    } catch (e) {
      setHint(e.message);
    }
  }

  async function pickAudioDevice(id) {
    setHint('');
    if (isPreview) {
      setConfig((c) => ({ ...c, audioCaptureDeviceId: id }));
      setAudioPicker(false);
      return;
    }
    try {
      const r = await window.hps.call('setAudioDevice', { deviceId: id });
      if (r?.config) setConfig(r.config);
      setAudioPicker(false);
      setHint(t('status.audioChanged'));
      clearTimeout(hintTimer.current);
      hintTimer.current = setTimeout(() => setHint(''), 4000);
    } catch (e) {
      setHint(e.message);
    }
  }

  function cycleLatency() {
    const i = LATENCIES.indexOf(config.latencyMs);
    const ms = LATENCIES[(i - 1 + LATENCIES.length) % LATENCIES.length];
    setConfig((c) => ({ ...c, latencyMs: ms }));
    if (!isPreview) window.hps?.call('setLatency', { value: ms }).catch(() => {});
  }

  function cycleLanguage() {
    const next = language === 'zh-TW' ? 'en' : 'zh-TW';
    setLanguage(persistLanguage(next));
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
  const displayHint = localizeMessage(language, hint);
  const displayStatus = localizeStatus(language, conn.status);
  let sub = t('status.idle');
  let subClass = '';
  if (!loaded) sub = t('status.starting');
  else if (hint) { sub = displayHint; subClass = 'warn'; }
  else if (live) {
    sub = both
      ? t('status.bothStreaming', { time: formatTime(elapsed) })
      : unpaired
        ? t('status.oneStreaming', { time: formatTime(elapsed) })
        : t('status.realtime', { time: formatTime(elapsed) });
    subClass = unpaired && !both ? 'warn' : '';
  }
  else if (mode === 'connecting') sub = both ? t('status.connectingBoth') : t('status.connecting');
  else if (mode === 'reconnecting') sub = t('status.reconnecting');
  else if (mode === 'failed') { sub = t('status.connectionFailed'); subClass = 'warn'; }
  else if (both) sub = t('status.both');
  else if (unpaired) { sub = t('status.unpaired'); subClass = 'warn'; }

  const lit = Math.round((volume / 100) * LADDER);
  const scanLabel = scan.active
    ? t('common.scanning', { progress: scan.progress || 0 })
    : scan.count != null
      ? t(scan.count ? 'common.found' : 'common.notFound', { count: scan.count })
      : scan.message
        ? localizeMessage(language, scan.message)
        : t('common.scan');
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
              <button className="mini mini-right" aria-label={t('settings.open')} title={t('settings.open')} onClick={() => { setAudioPicker(false); setPage('settings'); }}><GearIcon size={13} /></button>
              <DotText lines={['AIR', 'PLAY']} pitch={5} dot={3.6} className="headline" />
              <div className="rows">
                <div className="device-scroll">
                  {config.devices.length === 0 && (
                    <div className="row ghost"><span className="row-label dim">{t('common.noDevices')}</span></div>
                  )}
                  {config.devices.map((d) => {
                    const on = targets.includes(d.ip);
                    return (
                      <button className={`row${on ? ' on' : ''}${d.role === '成员' ? ' member' : ''}`} key={d.ip} onClick={() => pickDevice(d)}
                        title={both && d.unpaired ? t('tip.bothPlayback') : d.unpaired ? t('tip.unpairedPlayback') : d.role === '成员' ? t('tip.stereoMember') : d.ip}>
                        <span className={`check${on ? ' on' : ''}`}>{on && <CheckIcon />}</span>
                        <span className="row-label">{d.name || 'HomePod'}</span>
                        {d.role && <span className="row-tag mono">{localizeRole(language, d.role)}</span>}
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
                      aria-label={t('common.manualIp')}
                    />
                    <button className="row-chip" onClick={addIp}>{t('common.add')}</button>
                  </div>
                ) : (
                  <div className="row action">
                    <button className="row-main" onClick={doScan} disabled={scan.active}>
                      <span className={`check plus${scan.active ? ' spin' : ''}`}><PlusIcon /></span>
                      <span className="row-label">{scanLabel}</span>
                    </button>
                    <button className="row-chip mono" onClick={() => setAdding(true)} title={t('common.manualIp')}>IP</button>
                  </div>
                )}
              </div>
            </div>

            <div className="face face-back" aria-hidden={page !== 'settings'} inert={page !== 'settings'}>
              <button
                className="mini mini-left"
                aria-label={t('common.back')}
                title={t('common.back')}
                onClick={() => {
                  if (audioPicker) setAudioPicker(false);
                  else { setAudioPicker(false); setPage('devices'); }
                }}
              ><BackIcon size={13} /></button>
              {!audioPicker && (
                <button
                  className="mini mini-right language-mini mono"
                  aria-label={language === 'zh-TW' ? t('settings.switchToEnglish') : t('settings.switchToTraditional')}
                  title={language === 'zh-TW' ? t('settings.switchToEnglish') : t('settings.switchToTraditional')}
                  onClick={cycleLanguage}
                >
                  {language === 'zh-TW' ? 'EN' : '繁中'}
                </button>
              )}
              <DotText
                lines={audioPicker ? ['AU', 'DIO'] : ['SET', 'UP']}
                pitch={4.2}
                dot={3.1}
                className="headline headline-sm"
              />
              {audioPicker ? (
                <div className="rows rows-audio" role="group" aria-label={t('settings.audioPickerTitle')}>
                  <div className="audio-scroll">
                    {audioOptions.map((option) => {
                      const selected = option.id === (config.audioCaptureDeviceId || '');
                      return (
                        <button
                          key={option.id || 'system-default'}
                          data-audio-device={option.id}
                          className={`row${selected ? ' on' : ''}`}
                          onClick={() => pickAudioDevice(option.id)}
                          title={option.name}
                        >
                          <span className={`check${selected ? ' on' : ''}`}>{selected && <CheckIcon />}</span>
                          <span className="row-label">{option.name}</span>
                        </button>
                      );
                    })}
                  </div>
                </div>
              ) : (
                <div className="rows rows-set">
                  <button className="row" data-setting="audio" onClick={openAudioPicker} title={t('tip.audioOutput')}>
                    <span className="check ring"><i /></span>
                    <span className="row-label">{t('settings.audioOutput')}</span>
                    <span className="row-tag mono audio-tag" title={audioLabel}>{audioLabel}</span>
                  </button>
                  <button className={`row${config.autoReconnect ? ' on' : ''}`} onClick={() => setOption('autoReconnect')}>
                    <span className={`check${config.autoReconnect ? ' on' : ''}`}>{config.autoReconnect && <CheckIcon />}</span>
                    <span className="row-label">{t('settings.autoReconnect')}</span>
                  </button>
                  <button className={`row${config.autoConnectOnStart ? ' on' : ''}`} onClick={() => setOption('autoConnectOnStart')}>
                    <span className={`check${config.autoConnectOnStart ? ' on' : ''}`}>{config.autoConnectOnStart && <CheckIcon />}</span>
                    <span className="row-label">{t('settings.autoConnect')}</span>
                  </button>
                  <button className="row" data-setting="latency" onClick={cycleLatency} title={t('tip.latency')}>
                    <span className="check ring"><i /></span>
                    <span className="row-label">{t('settings.latency')}</span>
                    <span className="row-tag mono">{formatLatency(config.latencyMs)}</span>
                  </button>
                  <button className={`row${config.videoSync ? ' on' : ''}`} data-setting="video-sync" onClick={() => setOption('videoSync')}
                    title={t('tip.videoSync')}>
                    <span className={`check${config.videoSync ? ' on' : ''}`}>{config.videoSync && <CheckIcon />}</span>
                    <span className="row-label">{t('settings.videoSync')}</span>
                    {config.videoSync && <span className="row-tag mono">{formatLatency(config.latencyMs)}</span>}
                  </button>
                  <button className={`row${showBoard ? ' on' : ''}`} data-setting="board" onClick={() => setOption('showBoard')} title={t('tip.showBoard')}>
                    <span className={`check${showBoard ? ' on' : ''}`}>{showBoard && <CheckIcon />}</span>
                    <span className="row-label">{t('settings.showBoard')}</span>
                  </button>
                </div>
              )}
            </div>
          </div>
        </section>

        {/* ---------- 右上：正在播放（光球本体在板外层，见下方 orb-btn） ---------- */}
        <section className="card now glass">
          <div className="now-text">
            <div className="now-title" title={both ? targets.map((ip) => config.devices.find((d) => d.ip === ip)?.name || ip).join(' · ') : config.ip}>{deviceName}</div>
            <div className={`now-sub mono ${subClass}`} title={hint ? displayHint : mode === 'failed' ? displayStatus : both ? t('tip.bothPlayback') : unpaired ? t('tip.unpairedPlayback') : undefined}>{sub}</div>
          </div>
        </section>

        {/* ---------- 右下：音量 ---------- */}
        <section className="card vol glass" onWheel={onVolumeWheel}>
          <div
            className="ladder" ref={ladderRef} role="slider" aria-label={t('common.volume')} aria-valuemin={0} aria-valuemax={100} aria-valuenow={volume}
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
            <button className="round" aria-label={t('common.mute')} title={t('common.mute')} onClick={toggleMute}>
              <SpeakerIcon size={17} muted={volume < 0.5} />
            </button>
            <span className="vol-label">{t('common.volume')}</span>
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
        aria-label={live || mode === 'reconnecting' ? t('connection.disconnect') : t('connection.start')}
        title={live || mode === 'reconnecting' ? t('connection.disconnect') : t('connection.startHomePod')}
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
