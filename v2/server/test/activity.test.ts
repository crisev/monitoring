import { beforeEach, describe, expect, it } from 'vitest';
import { activityReport, normalizeSite, recordActivity } from '../src/activity';
import type { ActivityItem } from '../src/activity';
import { db, makeDevice, MIN, resetDb, T0 } from './helpers';

const TZ = 'Europe/Bucharest';
const item = (p: Partial<ActivityItem> & { app: string }): ActivityItem => ({
  secondsAgo: 0, mode: 'school', seconds: 0, audioSeconds: 0, blocked: 0, ...p,
});

beforeEach(resetDb);

describe('activity', () => {
  it('normalizes sites to the bare domain', () => {
    expect(normalizeSite('https://www.pbinfo.ro/probleme/1?x=2')).toBe('pbinfo.ro');
    expect(normalizeSite('WWW.Example.com:8080')).toBe('example.com');
    expect(normalizeSite('ro.wikipedia.org')).toBe('ro.wikipedia.org');
    expect(normalizeSite('')).toBe('');
  });

  it('sums samples per app and site', async () => {
    const pc = await makeDevice();
    await recordActivity(db(), pc.id, TZ, [
      item({ app: 'msedge.exe', site: 'www.pbinfo.ro', title: 'Problema #1', seconds: 30 }),
      item({ app: 'msedge', site: 'pbinfo.ro', title: 'Problema #1', seconds: 30, secondsAgo: 30 }),
      item({ app: 'Code', title: 'main.cpp', seconds: 120 }),
      item({ app: 'RobloxPlayerBeta', mode: 'gaming', seconds: 600, audioSeconds: 600, secondsAgo: 900 }),
      item({ app: 'chrome', blocked: 3 }),
      item({ app: 'idle', seconds: 0 }), // nothing to store
    ], T0);

    const r = await activityReport(db(), '2026-10-05', '2026-10-05');
    expect(r.totals).toMatchObject({ seconds: 780, school: 180, gaming: 600, audio: 600, blocked: 3 });
    expect(r.apps.map((a) => [a.app, a.seconds])).toEqual([['RobloxPlayerBeta', 600], ['Code', 120], ['msedge', 60], ['chrome', 0]]);
    expect(r.sites).toEqual([expect.objectContaining({ site: 'pbinfo.ro', seconds: 60, school: 60 })]);
    expect(r.titles[0]).toMatchObject({ app: 'Code', title: 'main.cpp', seconds: 120 });
  });

  it('places samples in time using secondsAgo and the server clock', async () => {
    const pc = await makeDevice();
    // Sent at T0 (11:00 Bucharest): one sample now, one from 3 hours ago (08:00).
    await recordActivity(db(), pc.id, TZ, [
      item({ app: 'Code', seconds: 60 }),
      item({ app: 'msedge', mode: 'gaming', seconds: 60, secondsAgo: 3 * 3600 }),
    ], T0);
    const r = await activityReport(db(), '2026-10-05', '2026-10-05');
    expect(r.timeline).toEqual([
      { slot: T0 - 180 * MIN, seconds: 60, mode: 'gaming', topApp: 'msedge' },
      { slot: T0, seconds: 60, mode: 'school', topApp: 'Code' },
    ]);
  });

  it('reports a range per day', async () => {
    const pc = await makeDevice();
    await recordActivity(db(), pc.id, TZ, [item({ app: 'Code', seconds: 300 })], T0);
    await recordActivity(db(), pc.id, TZ, [item({ app: 'Code', seconds: 200 })], T0 + 24 * 60 * MIN);
    const r = await activityReport(db(), '2026-10-04', '2026-10-06');
    expect(r.perDay.map((d) => [d.day, d.seconds])).toEqual([['2026-10-05', 300], ['2026-10-06', 200]]);
    expect(r.timeline).toEqual([]); // timeline only for a single day
    expect(r.totals.seconds).toBe(500);
  });
});
