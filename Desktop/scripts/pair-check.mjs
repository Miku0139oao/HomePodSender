import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { repo } from './cdp.mjs';

/*
 * 立体声对实机检查（只读，不会出声、不连接）：
 * 用临时配置启动 sidecar（AutoConnectOnStart=false），让它只读探测两台的 /info，看实际报告是否成对；
 * 再选择成员那台：成对时应改连组长，未成对时应保持所选设备（不替它猜）。
 * 用法：node scripts/pair-check.mjs <组长IP> <成员IP>
 */
const [leaderIp = '192.168.50.71', memberIp = '192.168.50.23'] = process.argv.slice(2);
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hps-pair-'));
// 旧版本可能留下的“两台都是组长”配置；新版应以 HomePod 实际报告为准
fs.writeFileSync(path.join(dir, 'config.json'), JSON.stringify({
  Devices: [
    { Name: 'A', Ip: leaderIp, Role: '组长', PairId: 'PAIR-CHECK' },
    { Name: 'B', Ip: memberIp, Role: '组长', PairId: 'PAIR-CHECK' },
  ],
  Ip: memberIp, VolumePercent: 5, LatencyMs: 1000, AutoReconnect: false, AutoConnectOnStart: false, DeviceId: '02:00:00:00:00:01',
}, null, 2));

const exe = path.join(repo, 'Sidecar', 'bin', 'Debug', 'net8.0-windows', 'HomePodSidecar.exe');
const sc = spawn(exe, [], { env: { ...process.env, HOMEPODSENDER_DATA: dir }, stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true });
sc.stdout.setEncoding('utf8');
let buf = '';
const waiters = [];
const events = [];
sc.stdout.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim();
    buf = buf.slice(i + 1);
    if (!line) continue;
    const m = JSON.parse(line);
    events.push(m);
    for (const w of [...waiters]) if (w.test(m)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(m); }
  }
});
const next = (test, ms = 8000) => new Promise((resolve, reject) => {
  const hit = events.find(test);
  if (hit) return resolve(hit);
  waiters.push({ test, resolve });
  setTimeout(() => reject(new Error('等待超时')), ms);
});
const show = (devs) => devs.map((d) => `${d.ip}=${d.role}${d.unpaired ? '（未成对）' : ''}`).join('  ');

let failed = 0;
const ok = (name, cond, detail = '') => { if (!cond) failed++; console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  —  ' + detail : ''}`); };
try {
  const ready = await next((m) => m.ev === 'ready');
  ok('sidecar 启动且未连接', ready.state === 'Disconnected', ready.state);
  // 启动时后台只读探测两台，角色更新后会发 devices 事件
  const dev = await next((m) => m.ev === 'devices' && m.devices.length === 2 && m.devices.every((d) => d.role));
  const leader = dev.devices.find((d) => d.ip === leaderIp);
  const member = dev.devices.find((d) => d.ip === memberIp);
  const paired = leader?.role === '组长' && member?.role === '成员' && !leader.unpaired && !member.unpaired;
  console.log(`  HomePod 实际报告：${show(dev.devices)}  →  ${paired ? '成对' : '未成对'}`);
  sc.stdin.write(JSON.stringify({ id: 1, cmd: 'selectDevice', ip: memberIp }) + '\n');
  const r = await next((m) => m.id === 1);
  const want = paired ? leaderIp : memberIp;
  ok(paired ? '成对：选成员会改连组长' : '未成对：保持所选设备，不替它猜', r.ok && r.payload.ip === want, `选中 ${r.payload?.ip}`);
  ok('全程没有发起连接', !events.some((m) => m.ev === 'state' && m.state !== 'Disconnected'));
} catch (e) {
  ok('检查执行', false, e.message);
  for (const m of events) console.log('  事件', JSON.stringify(m).slice(0, 220));
} finally {
  sc.stdin.write(JSON.stringify({ id: 9, cmd: 'quit' }) + '\n');
  setTimeout(() => { try { sc.kill(); } catch { } fs.rmSync(dir, { recursive: true, force: true }); process.exit(failed ? 1 : 0); }, 800);
}
