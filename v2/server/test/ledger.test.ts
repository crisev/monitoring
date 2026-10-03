import { beforeEach, describe, expect, it } from 'vitest';
import { addGrant, ensureDays, gameBalance } from '../src/ledger';
import { DAY, db, resetDb, T0, useSettings } from './helpers';

beforeEach(resetDb);

describe('day rollover', () => {
  it('opens today with the daily allowance, once', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 60 });
    expect(await ensureDays(db(), settings, T0)).toBe('2026-10-05');
    await ensureDays(db(), settings, T0 + 1000);
    await Promise.all([1, 2, 3, 4, 5].map(() => ensureDays(db(), settings, T0 + 2000)));
    expect(await gameBalance(db())).toBe(3600);
  });

  it('uses the configured time zone for the day boundary', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 10 });
    // 2026-10-05 23:30 UTC is already 2026-10-06 02:30 in Bucharest.
    expect(await ensureDays(db(), settings, Date.UTC(2026, 9, 5, 23, 30))).toBe('2026-10-06');
  });

  it('carries unused time over, capped at maxGameBalance', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 60, maxGameBalanceMinutes: 300 });
    const balances: number[] = [];
    for (let i = 0; i < 8; i++) {
      await ensureDays(db(), settings, T0 + i * DAY);
      balances.push((await gameBalance(db())) / 60);
    }
    expect(balances).toEqual([60, 120, 180, 240, 300, 300, 300, 300]);
  });

  it('backfills days the server did not see', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 30, maxGameBalanceMinutes: 1000 });
    await ensureDays(db(), settings, T0);
    await ensureDays(db(), settings, T0 + 4 * DAY);
    expect(await gameBalance(db())).toBe(5 * 30 * 60);
  });

  it('keeps granted game time across days when the daily allowance is 0', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 0, maxGameBalanceMinutes: 300 });
    await addGrant(db(), settings, { bucket: 'game', seconds: 120 * 60, by: 'mom' }, T0);
    await ensureDays(db(), settings, T0 + DAY);
    await ensureDays(db(), settings, T0 + 2 * DAY);
    expect(await gameBalance(db())).toBe(120 * 60);
  });

  it('a big grant is usable today and trimmed to the cap from tomorrow', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 0, maxGameBalanceMinutes: 300 });
    await addGrant(db(), settings, { bucket: 'game', seconds: 400 * 60, by: 'dad' }, T0);
    expect(await gameBalance(db())).toBe(400 * 60);
    await ensureDays(db(), settings, T0 + DAY);
    expect(await gameBalance(db())).toBe(300 * 60);
  });

  it('negative grants are recorded as adjustments', async () => {
    const { settings } = await useSettings({ dailyGameMinutes: 60 });
    await addGrant(db(), settings, { bucket: 'game', seconds: -15 * 60, by: 'dad', note: 'oops' }, T0);
    expect(await gameBalance(db())).toBe(45 * 60);
    const row = await db().prepare(`SELECT kind FROM ledger WHERE by = 'dad'`).first<{ kind: string }>();
    expect(row!.kind).toBe('adjustment');
  });
});
