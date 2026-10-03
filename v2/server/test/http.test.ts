import { exports as workerExports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { resetDb } from './helpers';

const worker = (workerExports as unknown as { default: Fetcher }).default;
const LOCAL = 'http://localhost'; // dev parent bypass applies only to localhost
const REMOTE = 'https://monitor.example.workers.dev';

function call(base: string, path: string, init: RequestInit & { json?: unknown; token?: string } = {}) {
  const headers = new Headers(init.headers);
  if (init.json !== undefined) headers.set('content-type', 'application/json');
  if (init.token) headers.set('authorization', `Bearer ${init.token}`);
  return worker.fetch(
    new Request(base + path, {
      method: init.method ?? (init.json !== undefined ? 'POST' : 'GET'),
      headers,
      body: init.json !== undefined ? JSON.stringify(init.json) : init.body,
    }),
  );
}

async function enrollPc(): Promise<string> {
  const created = await call(LOCAL, '/api/parent/devices', { json: { name: 'Desktop' } });
  const { code } = (await created.json()) as { code: string };
  const res = await call(REMOTE, '/api/device/enroll', { json: { code } });
  expect(res.status).toBe(200);
  return ((await res.json()) as { token: string }).token;
}

beforeEach(resetDb);

describe('http api', () => {
  it('health', async () => {
    expect(await (await call(REMOTE, '/api/health')).json()).toMatchObject({ ok: true });
  });

  it('device routes need a valid token', async () => {
    expect((await call(REMOTE, '/api/device/heartbeat', { json: { screenActive: true } })).status).toBe(401);
    expect((await call(REMOTE, '/api/device/heartbeat', { json: { screenActive: true }, token: 'nope' })).status).toBe(401);
  });

  it('parent routes need a Cloudflare Access login when not on localhost', async () => {
    expect((await call(REMOTE, '/api/parent/today')).status).toBe(401);
    const forged = await call(REMOTE, '/api/parent/today', { headers: { 'cf-access-jwt-assertion': 'x.y.z' } });
    expect(forged.status).toBe(401);
  });

  it('rejects cross-site form posts to parent routes', async () => {
    const res = await call(LOCAL, '/api/parent/grant', {
      method: 'POST',
      headers: { 'content-type': 'text/plain', origin: 'https://evil.example' },
      body: JSON.stringify({ bucket: 'game', minutes: 600 }),
    });
    expect(res.status).toBe(403);
  });

  it('enrollment codes work once', async () => {
    const created = await call(LOCAL, '/api/parent/devices', { json: { name: 'Desktop' } });
    const { code } = (await created.json()) as { code: string };
    expect((await call(REMOTE, '/api/device/enroll', { json: { code: code.toLowerCase() } })).status).toBe(200);
    expect((await call(REMOTE, '/api/device/enroll', { json: { code } })).status).toBe(400);
  });

  it('full flow: grant, play, parent ends gaming', async () => {
    const token = await enrollPc();

    let hb = await call(REMOTE, '/api/device/heartbeat', { json: { screenActive: true, clientVersion: '2.0.0' }, token });
    expect(await hb.json()).toMatchObject({ mode: 'school', session: null });

    const denied = await call(REMOTE, '/api/device/game/start', { method: 'POST', token });
    expect(denied.status).toBe(409);
    expect(await denied.json()).toMatchObject({ error: 'no_balance' });

    const grant = await call(LOCAL, '/api/parent/grant', { json: { bucket: 'game', minutes: 30, note: 'homework done' } });
    expect(grant.status).toBe(200);
    expect(await grant.json()).toMatchObject({ game: { balanceSeconds: 1800 } });

    const started = await call(REMOTE, '/api/device/game/start', { method: 'POST', token });
    const state = (await started.json()) as { mode: string; session: { id: string }; game: { balanceSeconds: number } };
    expect(state.mode).toBe('gaming');
    expect(state.game.balanceSeconds).toBe(1800);

    hb = await call(REMOTE, '/api/device/heartbeat', { json: { screenActive: true, sessionId: state.session.id }, token });
    expect(await hb.json()).toMatchObject({ mode: 'gaming', session: { id: state.session.id } });

    const today = (await (await call(LOCAL, '/api/parent/today')).json()) as { gaming: unknown[] };
    expect(today.gaming).toHaveLength(1);

    await call(LOCAL, '/api/parent/end-gaming', { json: { note: 'dinner' } });
    hb = await call(REMOTE, '/api/device/heartbeat', { json: { screenActive: true, sessionId: state.session.id }, token });
    expect(await hb.json()).toMatchObject({ mode: 'school', session: null });

    const ledger = (await (await call(LOCAL, '/api/parent/ledger')).json()) as { items: { kind: string; by: string }[] };
    expect(ledger.items.map((i) => i.kind)).toEqual(['usage', 'grant']);
    expect(ledger.items[1].by).toBe('parent@example.com');
  });

  it('settings are validated and versioned', async () => {
    const bad = await call(LOCAL, '/api/parent/settings', { method: 'PUT', json: { dailyGameMinutes: -5 } });
    expect(bad.status).toBe(400);
    const ok = await call(LOCAL, '/api/parent/settings', { method: 'PUT', json: { dailyGameMinutes: 45 } });
    expect(await ok.json()).toMatchObject({ version: 1, settings: { dailyGameMinutes: 45, maxGameBalanceMinutes: 300 } });

    const token = await enrollPc();
    const cfg = (await (await call(REMOTE, '/api/device/config', { token })).json()) as { version: number; allowedSites: string[] };
    expect(cfg.version).toBe(1);
    expect(cfg.allowedSites).toContain('pbinfo.ro');
  });
});
