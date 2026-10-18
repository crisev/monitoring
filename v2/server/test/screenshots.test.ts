import { exports as workerExports } from 'cloudflare:workers';
import { beforeEach, describe, expect, it } from 'vitest';
import { activityReport, recordActivity } from '../src/activity';
import { getScreenshotImage, listScreenshots, pruneScreenshots, SCREENSHOT_KEEP_MS, storeScreenshot } from '../src/screenshots';
import { db, makeDevice, MIN, resetDb, T0 } from './helpers';

const worker = (workerExports as unknown as { default: Fetcher }).default;
const LOCAL = 'http://localhost';
const TZ = 'Europe/Bucharest';
const jpeg = (n: number, fill = 7) => new Uint8Array(n).fill(fill).buffer;

beforeEach(resetDb);

describe('screenshots', () => {
  it('stores, lists and returns the image', async () => {
    const d = await makeDevice('Desktop');
    const id = await storeScreenshot(db(), { deviceId: d.id, mode: 'gaming', contentType: 'image/jpeg', image: jpeg(1000), timeZone: TZ }, T0);
    await storeScreenshot(db(), { deviceId: d.id, mode: 'school', contentType: 'image/jpeg', image: jpeg(10), timeZone: TZ }, T0 + MIN);

    const all = await listScreenshots(db(), '2026-10-05', null, T0 + 2 * MIN);
    expect(all.items.map((s) => s.mode)).toEqual(['school', 'gaming']); // newest first
    expect(all.items[1]).toMatchObject({ id, deviceName: 'Desktop', size: 1000 });
    expect(all.days).toEqual([{ day: '2026-10-05', count: 2, school: 1, gaming: 1 }]);
    expect((await listScreenshots(db(), '2026-10-05', 'gaming', T0 + 2 * MIN)).items).toHaveLength(1);

    const img = await getScreenshotImage(db(), id!, T0 + 2 * MIN);
    expect(img?.contentType).toBe('image/jpeg');
    expect(img?.bytes.length).toBe(1000);
    expect(img?.bytes[999]).toBe(7);
  });

  it('keeps screenshots for 3 days', async () => {
    const d = await makeDevice();
    const id = await storeScreenshot(db(), { deviceId: d.id, mode: 'school', contentType: 'image/jpeg', image: jpeg(10), timeZone: TZ }, T0);
    const later = T0 + SCREENSHOT_KEEP_MS + 1;
    expect(await getScreenshotImage(db(), id!, later)).toBeNull(); // hidden even before the cron runs
    expect(await pruneScreenshots(db(), T0 + SCREENSHOT_KEEP_MS - 1)).toBe(0);
    expect(await pruneScreenshots(db(), later)).toBe(1);
  });

  it('does not keep images too large for the database', async () => {
    const d = await makeDevice();
    expect(await storeScreenshot(db(), { deviceId: d.id, mode: 'school', contentType: 'image/jpeg', image: jpeg(2_000_000), timeZone: TZ }, T0)).toBeNull();
  });

  it('upload from the PC appears in the parent API', async () => {
    const created = await worker.fetch(new Request(`${LOCAL}/api/parent/devices`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ name: 'Desktop' }),
    }));
    const { code } = (await created.json()) as { code: string };
    const enrolled = await worker.fetch(new Request(`${LOCAL}/api/device/enroll`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code }),
    }));
    const { token } = (await enrolled.json()) as { token: string };

    const up = await worker.fetch(new Request(`${LOCAL}/api/device/screenshot`, {
      method: 'POST', headers: { 'content-type': 'image/jpeg', authorization: `Bearer ${token}` }, body: jpeg(500, 9),
    }));
    expect(await up.json()).toEqual({ ok: true, stored: true });

    const list = (await (await worker.fetch(new Request(`${LOCAL}/api/parent/screenshots`))).json()) as {
      items: { id: number; mode: string; deviceName: string }[];
    };
    expect(list.items).toHaveLength(1);
    expect(list.items[0]).toMatchObject({ mode: 'school', deviceName: 'Desktop' });

    const img = await worker.fetch(new Request(`${LOCAL}/api/parent/screenshots/${list.items[0].id}`));
    expect(img.status).toBe(200);
    expect(img.headers.get('content-type')).toBe('image/jpeg');
    expect(img.headers.get('cache-control')).toContain('immutable');
    expect(new Uint8Array(await img.arrayBuffer())[0]).toBe(9);
    expect((await worker.fetch(new Request(`${LOCAL}/api/parent/screenshots/999`))).status).toBe(404);
  });
});

describe('activity filters', () => {
  it('filters the lists by mode and hides the monitor itself', async () => {
    const d = await makeDevice();
    await recordActivity(db(), d.id, TZ, [
      { secondsAgo: 600, mode: 'school', app: 'Code', seconds: 60, audioSeconds: 0, blocked: 0 },
      { secondsAgo: 600, mode: 'gaming', app: 'RobloxPlayerBeta', seconds: 120, audioSeconds: 0, blocked: 0 },
      { secondsAgo: 600, mode: 'school', app: 'Monitor.Agent', title: 'Monitor', seconds: 30, audioSeconds: 0, blocked: 0 },
    ], T0);

    const all = await activityReport(db(), '2026-10-05', '2026-10-05');
    expect(all.apps.map((a) => a.app)).toEqual(['RobloxPlayerBeta', 'Code']);
    expect(all.totals).toMatchObject({ seconds: 180, school: 60, gaming: 120 });

    const school = await activityReport(db(), '2026-10-05', '2026-10-05', 'school');
    expect(school.apps.map((a) => a.app)).toEqual(['Code']);
    expect(school.titles).toEqual([]);
    expect(school.totals).toMatchObject({ school: 60, gaming: 120 }); // totals always show both modes
  });
});
