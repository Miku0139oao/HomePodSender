const STORAGE_KEY = 'hps.language';

export const MESSAGES = {
  'zh-TW': {
    'app.title': 'HomePod 推流',
    'common.back': '返回',
    'common.add': '加入',
    'common.manualIp': '手動輸入 IP',
    'common.scan': '掃描裝置',
    'common.scanning': '掃描中 {progress}%',
    'common.found': '找到 {count} 台',
    'common.notFound': '沒有找到',
    'common.noDevices': '尚無裝置',
    'common.volume': '音量',
    'common.mute': '靜音 / 恢復',
    'role.leader': '組長',
    'role.member': '成員',
    'device.none': '未選擇裝置',
    'device.both': '兩台一起',
    'settings.open': '設定',
    'settings.audioOutput': '音訊',
    'settings.audioPickerTitle': '音訊來源',
    'settings.systemDefault': '跟隨系統預設',
    'settings.audioUnavailable': '裝置已離線',
    'settings.autoReconnect': '斷線自動重連',
    'settings.autoConnect': '啟動時自動連線',
    'settings.latency': '播放延遲',
    'settings.videoSync': '對齊畫面',
    'settings.showBoard': '背景卡片',
    'settings.switchToEnglish': '切換至 English',
    'settings.switchToTraditional': '切換至繁體中文',
    'tip.selectDevice': '請先在左側選擇裝置',
    'tip.audioOutput': '選擇要回環擷取的 Windows 播放裝置。跟隨系統預設會隨輸出切換；指定裝置則固定抓取該聲卡。播放中切換會自動重新連線。',
    'tip.latency': '延遲越低越跟手，但越容易受網路抖動影響；0 秒為實驗值，出現卡頓時請提高。下次連線生效。對齊畫面用的就是這個值。',
    'tip.videoSync': '外掛只會延後 Chrome 網頁畫面，聲音仍會立即送出；口型按上面的播放延遲對齊。自動安裝會彈出一次管理員確認；若取消，請依提示手動載入外掛。開啟後保持本程式執行，並完全結束再開啟 Chrome。',
    'tip.showBoard': '關閉後三張卡片會各自懸浮在桌面上',
    'tip.bothPlayback': '每台 HomePod 播放同一路完整聲音。左右聲道請在「家庭」App 裡設定成立體聲對。',
    'tip.unpairedPlayback': '兩台 HomePod 沒有組成同一組立體聲對，只會從目前裝置出聲。',
    'tip.stereoMember': '立體聲對的成員，點選後會自動改選組長',
    'status.idle': '輕點光球 · 開始推流',
    'status.starting': '正在啟動…',
    'status.realtime': '{time} · 即時',
    'status.bothStreaming': '{time} · 兩台同時',
    'status.oneStreaming': '{time} · 僅此一台',
    'status.connectingBoth': '正在連線兩台…',
    'status.connecting': '正在連線…',
    'status.reconnecting': '重新連線中…',
    'status.connectionFailed': '連線失敗',
    'status.both': '兩台同時',
    'status.unpaired': '未組成同一組立體聲對',
    'status.notConnected': '未連線',
    'status.connectedPlaying': '已連線，正在播放系統聲音',
    'status.added': '已加入：{name}',
    'status.audioChanged': '已切換音訊來源',
    'connection.start': '連線',
    'connection.disconnect': '中斷連線',
    'connection.startHomePod': '連線 HomePod',
  },
  en: {
    'app.title': 'HomePod Sender',
    'common.back': 'Back',
    'common.add': 'Add',
    'common.manualIp': 'Enter IP manually',
    'common.scan': 'Scan devices',
    'common.scanning': 'Scanning {progress}%',
    'common.found': 'Found {count}',
    'common.notFound': 'Nothing found',
    'common.noDevices': 'No devices yet',
    'common.volume': 'Volume',
    'common.mute': 'Mute / Unmute',
    'role.leader': 'Leader',
    'role.member': 'Member',
    'device.none': 'No device selected',
    'device.both': 'Both together',
    'settings.open': 'Settings',
    'settings.audioOutput': 'Audio',
    'settings.audioPickerTitle': 'Audio source',
    'settings.systemDefault': 'Follow system default',
    'settings.audioUnavailable': 'Device unavailable',
    'settings.autoReconnect': 'Reconnect automatically',
    'settings.autoConnect': 'Connect at startup',
    'settings.latency': 'Playback delay',
    'settings.videoSync': 'Sync video',
    'settings.showBoard': 'Background board',
    'settings.switchToEnglish': 'Switch to English',
    'settings.switchToTraditional': '切換至繁體中文',
    'tip.selectDevice': 'Choose a device on the left first',
    'tip.audioOutput': 'Choose the Windows playback device to capture. Follow system default tracks output changes; a specific device stays bound to that sound card. Switching during playback reconnects automatically.',
    'tip.latency': 'Lower values feel more responsive but are more sensitive to network jitter. Zero is experimental; raise it if audio stutters. Takes effect on the next connection. Video Sync uses this value.',
    'tip.videoSync': 'The extension delays only the Chrome video, not the audio. Lip sync uses the playback delay above. Automatic installation asks for administrator approval once. If cancelled, follow the prompt to load it manually. Keep this app running, then fully quit and reopen Chrome.',
    'tip.showBoard': 'When disabled, the three cards float independently on the desktop',
    'tip.bothPlayback': 'Each HomePod receives the complete audio stream. Configure left and right channels as a stereo pair in the Home app.',
    'tip.unpairedPlayback': 'These HomePods are not grouped as a stereo pair, so audio will play only on the selected device.',
    'tip.stereoMember': 'Stereo pair member; selecting it switches to the leader',
    'status.idle': 'Tap the orb · Start streaming',
    'status.starting': 'Starting…',
    'status.realtime': '{time} · Live',
    'status.bothStreaming': '{time} · Two devices',
    'status.oneStreaming': '{time} · This device',
    'status.connectingBoth': 'Connecting both…',
    'status.connecting': 'Connecting…',
    'status.reconnecting': 'Reconnecting…',
    'status.connectionFailed': 'Connection failed',
    'status.both': 'Both together',
    'status.unpaired': 'Not configured as a stereo pair',
    'status.notConnected': 'Not connected',
    'status.connectedPlaying': 'Connected, playing system audio',
    'status.added': 'Added: {name}',
    'status.audioChanged': 'Audio source changed',
    'connection.start': 'Connect',
    'connection.disconnect': 'Disconnect',
    'connection.startHomePod': 'Connect to HomePod',
  },
};

export const SUPPORTED_LANGUAGES = ['zh-TW', 'en'];

export function normalizeLanguage(value) {
  if (typeof value === 'string' && value.toLowerCase().startsWith('zh')) return 'zh-TW';
  if (typeof value === 'string' && value.toLowerCase().startsWith('en')) return 'en';
  return null;
}

export function readLanguage() {
  try {
    return normalizeLanguage(localStorage.getItem(STORAGE_KEY))
      || normalizeLanguage(navigator.language)
      || 'en';
  } catch {
    return 'en';
  }
}

export function persistLanguage(language) {
  const normalized = normalizeLanguage(language) || 'en';
  try { localStorage.setItem(STORAGE_KEY, normalized); } catch { /* storage can be unavailable */ }
  return normalized;
}

export function translate(language, key, values = {}) {
  const table = MESSAGES[normalizeLanguage(language) || 'en'] || MESSAGES.en;
  const template = table[key] ?? MESSAGES.en[key] ?? key;
  return template.replace(/\{(\w+)\}/g, (_, name) => String(values[name] ?? `{${name}}`));
}

export function localizeRole(language, role) {
  if (role === '组长') return translate(language, 'role.leader');
  if (role === '成员') return translate(language, 'role.member');
  return role || '';
}

function localizeDetail(language, detail) {
  const text = String(detail || '');
  if (language === 'en') {
    if (/选择的音频输出设备当前不可用/.test(text)) return 'The selected audio output is currently unavailable.';
    if (/HomePod 无响应/.test(text)) return 'HomePod did not respond. Check the IP address and network.';
    const timeout = text.match(/流 SETUP 等待响应超时 \(?(\d+)ms\)?/i);
    if (timeout) return `Stream SETUP timed out (${timeout[1]} ms)`;
    return text;
  }
  return text
    .replace(/无响应/g, '無回應')
    .replace(/请检查/g, '請檢查')
    .replace(/网络/g, '網路')
    .replace(/连接/g, '連線')
    .replace(/超时/g, '逾時')
    .replace(/流 SETUP/g, '串流 SETUP')
    .replace(/重连/g, '重新連線')
    .replace(/未连接/g, '未連線')
    .replace(/已连接/g, '已連線')
    .replace(/无法/g, '無法')
    .replace(/设备/g, '裝置')
    .replace(/音频/g, '音訊')
    .replace(/输出/g, '輸出')
    .replace(/音乐/g, '音樂');
}

export function localizeStatus(language, status) {
  const text = String(status || '').trim();
  if (!text) return '';
  if (/^连接失败[：:](.*)$/i.test(text)) {
    const detail = text.replace(/^连接失败[：:]\s*/i, '');
    const label = language === 'en' ? 'Connection failed' : '連線失敗';
    return `${label}: ${localizeDetail(language, detail)}`;
  }
  if (/^已连接，正在播放系统声音$/i.test(text)) return translate(language, 'status.connectedPlaying');
  if (/^已连接 (\d+) 台，正在播放系统声音$/i.test(text)) {
    const n = text.match(/(\d+)/)?.[1] || '?';
    return language === 'en' ? `Connected to ${n} HomePods, playing system audio` : `已連線 ${n} 台，正在播放系統聲音`;
  }
  if (/^已连接 (\d+)\/(\d+) 台，正在播放系统声音$/i.test(text)) {
    const m = text.match(/(\d+)\/(\d+)/);
    return language === 'en' ? `Connected to ${m[1]} of ${m[2]} HomePods, playing system audio` : `已連線 ${m[1]}/${m[2]} 台，正在播放系統聲音`;
  }
  if (/^正在连接 (\d+) 台…$/i.test(text)) {
    const n = text.match(/(\d+)/)?.[1] || '?';
    return language === 'en' ? `Connecting to ${n} HomePods…` : `正在連線至 ${n} 台 HomePod…`;
  }
  if (/^正在连接 (.+) …$/i.test(text)) {
    const target = text.match(/^正在连接 (.+) …$/)?.[1] || '';
    return language === 'en' ? `Connecting to ${target}…` : `正在連線至 ${target}…`;
  }
  if (/^未连接$/i.test(text)) return translate(language, 'status.notConnected');
  if (/^连接已断开$/i.test(text)) return language === 'en' ? 'Connection closed' : '連線已中斷';
  if (/^重连失败[：:](.*)$/i.test(text)) {
    const detail = text.replace(/^重连失败[：:]\s*/i, '');
    return language === 'en' ? `Reconnect failed: ${localizeDetail(language, detail)}` : `重新連線失敗：${localizeDetail(language, detail)}`;
  }
  return localizeDetail(language, text);
}

export function localizeMessage(language, message) {
  const text = String(message || '').trim();
  if (!text) return '';
  const added = text.match(/^已添加[：:]\s*(.+)$/);
  if (added) return translate(language, 'status.added', { name: added[1] });
  const found = text.match(/^找到 (\d+) 台$/);
  if (found) return translate(language, 'common.found', { count: found[1] });
  if (/^没有找到$/.test(text)) return translate(language, 'common.notFound');
  return localizeStatus(language, text);
}
