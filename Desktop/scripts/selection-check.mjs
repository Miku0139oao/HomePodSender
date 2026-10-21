import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { repo } from './cdp.mjs';

/*
 * 设备勾选检查（离线，不连接、不出声）：
 * 用临时配置启动 sidecar，只发 selectDevice / toggleDevice，看回包里的 config.ips。
 * 覆盖“连接两台设备后取消不了设备”这个回归：
 *  · 未成对的一对默认勾两台；再点已勾选的那台要能取消，只剩另一台；
 *  · 一台都不剩时保留它自己（总得有一台可推）；
 *  · 点同对的另一台能恢复成两台，点别的设备整体切过去；
 *  · 确认成对时点成员仍只连组长，点不动成员。
 * 用法：node scripts/selection-check.mjs
 */
const exe = path.join(repo, 'Sidecar', 'bin', 'Debug', 'net8.0-windows', 'HomePodSidecar.exe');
const A = '192.0.2.10';   // RFC 5737 TEST-NET：保证探测不到，角色不会被现场报告覆盖
const B = '192.0.2.11';
const NON_PAIR = '192.0.2.12';
const PAIR = 'SELECTION-CHECK';

let failed = 0;
const ok = (name, cond, detail = '') => { if (!cond) failed++; console.log(`${cond ? 'PASS' : 'FAIL'}  ${name}${detail ? '  —  ' + detail : ''}`); };

function start(devices) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'hps-sel-'));
  fs.writeFileSync(path.join(dir, 'config.json'), JSON.stringify({
    Devices: devices,
    Ip: devices[0].Ip, VolumePercent: 5, LatencyMs: 1000,
    AutoReconnect: false, AutoConnectOnStart: false, ShowBoard: false, DeviceId: '02:00:00:00:00:02',
  }, null, 2));
  const sc = spawn(exe, [], { env: { ...process.env, HOMEPODSENDER_DATA: dir }, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  sc.stdout.setEncoding('utf8');
  const events = [];
  const waiters = [];
  let buf = '';
  sc.stdout.on('data', (d) => {
    buf += d;
    let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, i).trim();
      buf = buf.slice(i + 1);
      if (!line) continue;
      let m;
      try { m = JSON.parse(line); } catch { continue; }
      events.push(m);
      for (const w of [...waiters]) if (w.test(m)) { waiters.splice(waiters.indexOf(w), 1); w.resolve(m); }
    }
  });
  sc.stderr.setEncoding('utf8');
  sc.stderr.on('data', (d) => process.stderr.write('[sidecar] ' + d));
  const next = (test, ms = 20000) => new Promise((resolve, reject) => {
    const hit = events.find(test);
    if (hit) return resolve(hit);
    waiters.push({ test, resolve });
    setTimeout(() => reject(new Error('等待超时')), ms);
  });
  let id = 0;
  return {
    events, next, dir,
    async call(cmd, payload = {}) {
      const n = ++id;
      sc.stdin.write(JSON.stringify({ id: n, cmd, ...payload }) + '\n');
      return next((m) => m.id === n);
    },
    async stop() {
      try { sc.stdin.write(JSON.stringify({ id: 999, cmd: 'quit' }) + '\n'); } catch { }
      await new Promise((r) => setTimeout(r, 500));
      try { sc.kill(); } catch { }
      fs.rmSync(dir, { recursive: true, force: true });
    },
  };
}

const ips = (r) => r?.payload?.config?.ips;
const show = (v) => JSON.stringify(v);
const same = (v, want) => Array.isArray(v) && v.length === want.length && want.every((x) => v.includes(x));

// 场景 1：两台都自称组长（未成对），也就是本机 HomePod 当前的实际状态
{
  const sc = start([
    { Name: 'A', Ip: A, Role: '组长', PairId: PAIR, Unpaired: true },
    { Name: 'B', Ip: B, Role: '组长', PairId: PAIR, Unpaired: true },
  ]);
  try {
    const ready = await sc.next((m) => m.ev === 'ready');
    ok('未成对的一对：默认两台一起（每只各开一路）', same(ready.config.ips, [A, B]), show(ready.config.ips));

    let r = await sc.call('toggleDevice', { ip: B });
    ok('点已勾选的一台：取消它，只剩另一台', r.ok && same(ips(r), [A]), `ok=${r.ok} ips=${show(ips(r))}`);
    r = await sc.call('toggleDevice', { ip: A });
    ok('最后一台不会被取消（保留它自己）', r.ok && same(ips(r), [A]), show(ips(r)));
    r = await sc.call('toggleDevice', { ip: B });
    ok('点同对的另一台：恢复成两台一起', r.ok && same(ips(r), [A, B]), show(ips(r)));
    r = await sc.call('toggleDevice', { ip: A });
    ok('两台一起时取消其中一台（这次取消 A）', r.ok && same(ips(r), [B]), show(ips(r)));

    r = await sc.call('toggleDevice', { ip: NON_PAIR });
    ok('点别的设备：整体切过去，只勾它', r.ok && same(ips(r), [NON_PAIR]), show(ips(r)));
    r = await sc.call('selectDevice', { ip: A });
    ok('selectDevice（手工加 IP）只选它自己', r.ok && same(ips(r), [A]), show(ips(r)));
    ok('全程没有发起连接', !sc.events.some((m) => m.ev === 'state' && m.state !== 'Disconnected'));
  } catch (e) {
    ok('场景 1 执行', false, e.message);
  } finally {
    await sc.stop();
  }
}

// 场景 2：HomePod 确认成对（组长 / 成员）——点成员仍然只连组长，不能把成员加进来
{
  const sc = start([
    { Name: 'A', Ip: A, Role: '组长', PairId: PAIR, Unpaired: false },
    { Name: 'B', Ip: B, Role: '成员', PairId: PAIR, Unpaired: false },
  ]);
  try {
    await sc.next((m) => m.ev === 'ready');
    let r = await sc.call('toggleDevice', { ip: B });
    ok('成对时点成员：仍只连组长', r.ok && same(ips(r), [A]), show(ips(r)));
    r = await sc.call('toggleDevice', { ip: A });
    ok('成对时最后一台保留', r.ok && same(ips(r), [A]), show(ips(r)));
    ok('全程没有发起连接', !sc.events.some((m) => m.ev === 'state' && m.state !== 'Disconnected'));
  } catch (e) {
    ok('场景 2 执行', false, e.message);
  } finally {
    await sc.stop();
  }
}

process.exit(failed ? 1 : 0);
