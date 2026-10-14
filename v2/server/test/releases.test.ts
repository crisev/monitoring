import { env, exports as workerExports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import type { Device } from '../src/devices';
import type { Env } from '../src/env';
import {
  applyUpdateEvent,
  checkReleases,
  compareVersions,
  listReleases,
  RELEASE_CHECK_MS,
  requestUpdate,
  updateOffer,
} from '../src/releases';
import { db, makeDevice, resetDb, T0 } from './helpers';

const SHA = 'ab'.repeat(32);
const worker = (workerExports as unknown as { default: Fetcher }).default;

/** A fake GitHub: the releases list plus the .sha256 files. */
function fakeGitHub(versions: string[], opts: { status?: number; extra?: object[] } = {}) {
  const calls: string[] = [];
  const fetcher = (async (input: RequestInfo | URL) => {
    const url = String(input);
    calls.push(url);
    if (url.includes('/releases?')) {
      if (opts.status) return new Response('{}', { status: opts.status });
      const list = versions.map((v) => ({
        tag_name: `client-v${v}`,
        draft: false,
        prerelease: false,
        published_at: '2026-10-05T08:00:00Z',
        body: `Release ${v}`,
        assets: [
          { name: `MonitorV2-${v}.zip`, size: 1234, browser_download_url: `https://github.com/x/y/releases/download/client-v${v}/MonitorV2-${v}.zip` },
          { name: `MonitorV2-${v}.zip.sha256`, size: 90, browser_download_url: `https://dl/${v}.sha256` },
        ],
      }));
      return Response.json([...list, ...(opts.extra ?? [])]);
    }
    if (url.endsWith('.sha256')) return new Response(`${SHA.toUpperCase()}  MonitorV2.zip\n`);
    return new Response('nope', { status: 404 });
  }) as typeof fetch;
  return { fetcher, calls };
}

const testEnv = () => env as unknown as Env;
const loadDevice = async (id: string, clientVersion: string): Promise<Device> => ({
  ...(await db().prepare('SELECT * FROM devices WHERE id = ?1').bind(id).first<Device>())!,
  client_version: clientVersion,
});

beforeEach(resetDb);

describe('versions', () => {
  it('compares numerically', () => {
    expect(compareVersions('0.1.10', '0.1.9')).toBeGreaterThan(0);
    expect(compareVersions('0.2.0', '0.10.0')).toBeLessThan(0);
    expect(compareVersions('1.0.0+abc', '1.0.0')).toBe(0);
    expect(compareVersions('0.1.0', null)).toBeGreaterThan(0);
    expect(compareVersions('fake-1', '0.0.1')).toBeLessThan(0);
  });
});

describe('release check', () => {
  it('stores client releases from GitHub, once an hour unless forced', async () => {
    const gh = fakeGitHub(['0.1.5', '0.1.12'], {
      extra: [
        { tag_name: 'v1.0.40', draft: false, prerelease: false, published_at: null, body: '', assets: [] }, // the original app
        { tag_name: 'client-v0.1.13', draft: false, prerelease: false, published_at: null, body: '', assets: [] }, // assets not uploaded yet
      ],
    });
    const first = await checkReleases(testEnv(), T0, { fetcher: gh.fetcher });
    expect(first).toEqual({ checked: true, added: ['0.1.12', '0.1.5'], error: null });
    const stored = await listReleases(db());
    expect(stored.map((r) => r.version)).toEqual(['0.1.12', '0.1.5']);
    expect(stored[0]).toMatchObject({ sha256: SHA, size: 1234, tag: 'client-v0.1.12' });

    expect((await checkReleases(testEnv(), T0 + 60_000, { fetcher: gh.fetcher })).checked).toBe(false);
    expect((await checkReleases(testEnv(), T0 + RELEASE_CHECK_MS, { fetcher: gh.fetcher })).added).toEqual([]);
    expect((await checkReleases(testEnv(), T0 + RELEASE_CHECK_MS + 1, { fetcher: gh.fetcher, force: true })).checked).toBe(true);
  });

  it('records GitHub errors without throwing', async () => {
    const res = await checkReleases(testEnv(), T0, { fetcher: fakeGitHub([], { status: 403 }).fetcher });
    expect(res.error).toMatch(/403/);
  });
});

describe('update offer', () => {
  it('is offered only after a parent asks, and only while newer', async () => {
    await checkReleases(testEnv(), T0, { fetcher: fakeGitHub(['0.1.12']).fetcher });
    let device = await makeDevice();
    device = { ...device, client_version: '0.1.5' };
    expect(await updateOffer(db(), device)).toBeNull();

    await requestUpdate(db(), device.id, '0.1.12', T0 + 1000);
    const asked = await loadDevice(device.id, '0.1.5');
    expect(await updateOffer(db(), asked)).toEqual({
      id: `0.1.12@${T0 + 1000}`,
      version: '0.1.12',
      url: 'https://github.com/x/y/releases/download/client-v0.1.12/MonitorV2-0.1.12.zip',
      sha256: SHA,
      size: 1234,
    });
    expect(await updateOffer(db(), { ...asked, client_version: '0.1.12' })).toBeNull();
  });

  it('stops offering after a failure until the parent asks again', async () => {
    await checkReleases(testEnv(), T0, { fetcher: fakeGitHub(['0.1.12']).fetcher });
    const device = await makeDevice();
    await requestUpdate(db(), device.id, '0.1.12', T0);
    const msg = await applyUpdateEvent(db(), { ...device, name: 'Desktop' }, 'update_failed', { to: '0.1.12', reason: 'rolled back' }, T0 + 5);
    expect(msg).toContain('failed: rolled back');
    const load = () => loadDevice(device.id, '0.1.5');
    expect((await load()).update_status).toBe('failed');
    expect(await updateOffer(db(), await load())).toBeNull();

    await requestUpdate(db(), device.id, '0.1.12', T0 + 10);
    expect((await updateOffer(db(), await load()))?.id).toBe(`0.1.12@${T0 + 10}`);
  });
});

describe('http', () => {
  const call = (base: string, path: string, init: { json?: unknown; token?: string } = {}) =>
    worker.fetch(
      new Request(base + path, {
        method: init.json !== undefined ? 'POST' : 'GET',
        headers: {
          ...(init.json !== undefined ? { 'content-type': 'application/json' } : {}),
          ...(init.token ? { authorization: `Bearer ${init.token}` } : {}),
        },
        body: init.json !== undefined ? JSON.stringify(init.json) : undefined,
      }),
    );
  const LOCAL = 'http://localhost';

  it('parent requests an update; the heartbeat offers it; events report back', async () => {
    await checkReleases(testEnv(), T0, { fetcher: fakeGitHub(['0.1.12']).fetcher });
    const created = await call(LOCAL, '/api/parent/devices', { json: { name: 'Desktop' } });
    const { code, deviceId } = (await created.json()) as { code: string; deviceId: string };
    const { token } = (await (await call(LOCAL, '/api/device/enroll', { json: { code } })).json()) as { token: string };
    const beat = async (clientVersion: string) =>
      (await (await call(LOCAL, '/api/device/heartbeat', { json: { screenActive: true, clientVersion }, token })).json()) as {
        update: { version: string } | null;
      };

    expect((await beat('0.1.5')).update).toBeNull();
    expect((await call(LOCAL, `/api/parent/devices/${deviceId}/update`, { json: { version: '9.9.9' } })).status).toBe(400);
    expect((await call(LOCAL, `/api/parent/devices/${deviceId}/update`, { json: { version: '0.1.12' } })).status).toBe(200);
    expect((await beat('0.1.5')).update?.version).toBe('0.1.12');

    await call(LOCAL, '/api/device/events', { json: { events: [{ type: 'updated', detail: { from: '0.1.5', to: '0.1.12' } }] }, token });
    expect((await beat('0.1.12')).update).toBeNull();

    const list = (await (await call(LOCAL, '/api/parent/devices')).json()) as {
      items: { update: { status: string } | null; clientVersion: string }[];
      latestRelease: { version: string };
    };
    expect(list.latestRelease.version).toBe('0.1.12');
    expect(list.items[0]).toMatchObject({ clientVersion: '0.1.12', update: { status: 'done' } });
    // Already on that version: asking again is refused.
    expect((await call(LOCAL, `/api/parent/devices/${deviceId}/update`, { json: { version: '0.1.12' } })).status).toBe(400);
  });
});
