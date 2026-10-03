#!/usr/bin/env node
// Plays the role of the Windows PC against the Monitor v2 server, for testing without Windows.
//
//   node fake-device.mjs [--url http://localhost:8787] enroll <CODE>
//   node fake-device.mjs status                 one heartbeat, print state
//   node fake-device.mjs start | stop           GAME ON / GAME OFF
//   node fake-device.mjs play <minutes>         GAME ON, heartbeat every 30s, GAME OFF after <minutes>
//   node fake-device.mjs run                    heartbeat loop (School mode, screen in use) until Ctrl+C
//   node fake-device.mjs config                 print the School-mode config
//
// The device token is stored in .fake-device.json next to this script.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const STATE_FILE = join(dirname(fileURLToPath(import.meta.url)), '.fake-device.json');
const args = process.argv.slice(2);
let url;
const urlIdx = args.indexOf('--url');
if (urlIdx >= 0) [, url] = args.splice(urlIdx, 2);

let saved = {};
try { saved = JSON.parse(readFileSync(STATE_FILE, 'utf8')); } catch {}
url = (url ?? saved.url ?? 'http://localhost:8787').replace(/\/$/, '');

const save = () => writeFileSync(STATE_FILE, JSON.stringify(saved, null, 2));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let lastBeat = Date.now();

async function call(path, body, method = 'POST') {
  const res = await fetch(url + path, {
    method,
    headers: { 'content-type': 'application/json', ...(saved.token ? { authorization: `Bearer ${saved.token}` } : {}) },
    body: method === 'GET' ? undefined : JSON.stringify(body ?? {}),
  });
  const data = await res.json().catch(() => ({}));
  return { status: res.status, data };
}

function show(state) {
  const m = (s) => (s === null ? '∞' : `${Math.floor(s / 60)}m${String(s % 60).padStart(2, '0')}s`);
  console.log(
    `[${new Date().toLocaleTimeString()}] mode=${state.mode}` +
      (state.session ? ` lease=${state.session.leaseRemainingSeconds}s` : '') +
      ` game=${m(state.game.balanceSeconds)} screenLeft=${m(state.screen.remainingSeconds)} config=v${state.configVersion}`,
  );
}

async function heartbeat() {
  const now = Date.now();
  const screenSeconds = Math.round((now - lastBeat) / 1000);
  const r = await call('/api/device/heartbeat', { sessionId: saved.sessionId ?? null, screenActive: true, screenSeconds, clientVersion: 'fake-1' });
  if (r.status !== 200) throw new Error(`heartbeat ${r.status} ${JSON.stringify(r.data)}`);
  lastBeat = now;
  saved.sessionId = r.data.session?.id ?? null;
  save();
  show(r.data);
  return r.data;
}

const [cmd, arg] = args;
switch (cmd) {
  case 'enroll': {
    if (!arg) throw new Error('usage: enroll <CODE>');
    const r = await call('/api/device/enroll', { code: arg });
    if (r.status !== 200) throw new Error(`enroll failed: ${JSON.stringify(r.data)}`);
    saved = { url, token: r.data.token, deviceId: r.data.deviceId, sessionId: null };
    save();
    console.log(`Enrolled as "${r.data.deviceName}" (${r.data.deviceId}). Token saved to ${STATE_FILE}`);
    break;
  }
  case 'status':
    await heartbeat();
    break;
  case 'config':
    console.log(JSON.stringify((await call('/api/device/config', null, 'GET')).data, null, 2));
    break;
  case 'start': {
    const r = await call('/api/device/game/start');
    if (r.status !== 200) { console.log(`Refused: ${r.data.error}`); break; }
    saved.sessionId = r.data.session.id; save(); show(r.data);
    break;
  }
  case 'stop': {
    const r = await call('/api/device/game/stop', { sessionId: saved.sessionId });
    saved.sessionId = null; save(); show(r.data);
    break;
  }
  case 'play': {
    const minutes = Number(arg ?? 5);
    const r = await call('/api/device/game/start');
    if (r.status !== 200) { console.log(`Refused: ${r.data.error}`); break; }
    saved.sessionId = r.data.session.id; save(); show(r.data);
    const end = Date.now() + minutes * 60_000;
    while (Date.now() < end) {
      await sleep(Math.min(30_000, end - Date.now()));
      const s = await heartbeat();
      if (s.mode !== 'gaming') { console.log('Server says School mode — stopping.'); break; }
    }
    if (saved.sessionId) {
      const r2 = await call('/api/device/game/stop', { sessionId: saved.sessionId });
      saved.sessionId = null; save(); show(r2.data);
    }
    break;
  }
  case 'run':
    for (;;) { await heartbeat(); await sleep(30_000); }
  default:
    console.log('commands: enroll <CODE> | status | config | start | stop | play <minutes> | run   [--url URL]');
}
