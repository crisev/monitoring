import { beforeEach, describe, expect, it } from 'vitest';
import { LEASE_SECONDS } from '../src/env';
import { closeExpired, openSession, renewSession, sessionUsedSeconds, startSession, stopSession } from '../src/game';
import { addGrant, ensureDays, gameBalance } from '../src/ledger';
import type { Settings } from '../src/settings';
import { db, makeDevice, MIN, resetDb, SEC, T0, useSettings } from './helpers';

let settings: Settings;

async function withBalance(seconds: number) {
  settings = (await useSettings({ dailyGameMinutes: 0 })).settings;
  await ensureDays(db(), settings, T0);
  if (seconds) await addGrant(db(), settings, { bucket: 'game', seconds, by: 'parent' }, T0);
}

beforeEach(resetDb);

describe('game sessions', () => {
  it('refuses to start without balance', async () => {
    await withBalance(0);
    const pc = await makeDevice();
    expect(await startSession(db(), pc.id, '2026-10-05', T0)).toEqual({ ok: false, error: 'no_balance' });
  });

  it('debits the lease up front and is idempotent', async () => {
    await withBalance(30 * 60);
    const pc = await makeDevice();
    const a = await startSession(db(), pc.id, '2026-10-05', T0);
    const b = await startSession(db(), pc.id, '2026-10-05', T0 + 5 * SEC);
    expect(a.ok && b.ok && a.session.id === b.session.id).toBe(true);
    expect(await gameBalance(db())).toBe(30 * 60 - LEASE_SECONDS);
  });

  it('charges exactly the played time when stopped normally', async () => {
    await withBalance(30 * 60);
    const pc = await makeDevice();
    const r = await startSession(db(), pc.id, '2026-10-05', T0);
    if (!r.ok) throw new Error('start failed');
    for (let t = 30; t <= 600; t += 30) {
      expect((await renewSession(db(), pc.id, r.session.id, T0 + t * SEC)).ok).toBe(true);
    }
    await stopSession(db(), (await openSession(db(), pc.id))!, T0 + 600 * SEC, 'stopped', 'pc');
    expect(await sessionUsedSeconds(db(), r.session.id)).toBe(600);
    expect(await gameBalance(db())).toBe(30 * 60 - 600);
  });

  it('a PC that goes silent pays at most one lease', async () => {
    await withBalance(30 * 60);
    const pc = await makeDevice();
    const r = await startSession(db(), pc.id, '2026-10-05', T0);
    if (!r.ok) throw new Error('start failed');
    // No heartbeats: the lease runs out and the session is closed at paid_until.
    expect(await closeExpired(db(), T0 + 10 * MIN)).toHaveLength(1);
    expect(await sessionUsedSeconds(db(), r.session.id)).toBe(LEASE_SECONDS);
    expect(await openSession(db(), pc.id)).toBeNull();
    // A late heartbeat cannot revive it.
    expect((await renewSession(db(), pc.id, r.session.id, T0 + 11 * MIN)).ok).toBe(false);
  });

  it('never extends beyond the balance', async () => {
    await withBalance(100);
    const pc = await makeDevice();
    const r = await startSession(db(), pc.id, '2026-10-05', T0);
    if (!r.ok) throw new Error('start failed');
    expect(r.session.paid_until).toBe(T0 + 90 * SEC);
    const h1 = await renewSession(db(), pc.id, r.session.id, T0 + 30 * SEC);
    expect(h1.session!.paid_until).toBe(T0 + 100 * SEC); // only 10s were left
    const h2 = await renewSession(db(), pc.id, r.session.id, T0 + 60 * SEC);
    expect(h2.ok && h2.session!.paid_until).toBe(T0 + 100 * SEC);
    expect((await renewSession(db(), pc.id, r.session.id, T0 + 101 * SEC)).ok).toBe(false);
    await closeExpired(db(), T0 + 101 * SEC);
    expect(await sessionUsedSeconds(db(), r.session.id)).toBe(100);
    expect(await gameBalance(db())).toBe(0);
  });

  it('a parent stop refunds the unused lease', async () => {
    await withBalance(10 * 60);
    const pc = await makeDevice();
    const r = await startSession(db(), pc.id, '2026-10-05', T0);
    if (!r.ok) throw new Error('start failed');
    const closed = await stopSession(db(), r.session, T0 + 20 * SEC, 'parent', 'mom');
    expect(closed!.end_reason).toBe('parent');
    expect(await sessionUsedSeconds(db(), r.session.id)).toBe(20);
    expect(await gameBalance(db())).toBe(10 * 60 - 20);
    expect((await renewSession(db(), pc.id, r.session.id, T0 + 30 * SEC)).ok).toBe(false);
  });

  it('rejects renewals from another device', async () => {
    await withBalance(10 * 60);
    const pc = await makeDevice('a');
    const other = await makeDevice('b');
    const r = await startSession(db(), pc.id, '2026-10-05', T0);
    if (!r.ok) throw new Error('start failed');
    expect((await renewSession(db(), other.id, r.session.id, T0 + 30 * SEC)).ok).toBe(false);
  });

  it('random play patterns never use more than the balance', async () => {
    let seed = 42;
    const rand = () => ((seed = (seed * 1103515245 + 12345) % 2 ** 31) / 2 ** 31);
    for (let round = 0; round < 15; round++) {
      await resetDb();
      const initial = Math.floor(rand() * 1200);
      await withBalance(initial);
      const pc = await makeDevice();
      let t = T0;
      let sessionId: string | null = null;
      for (let step = 0; step < 60; step++) {
        t += Math.floor(rand() * 120) * SEC; // irregular gaps, sometimes longer than the lease
        const roll = rand();
        if (!sessionId || roll < 0.1) {
          const r = await startSession(db(), pc.id, '2026-10-05', t);
          sessionId = r.ok ? r.session.id : null;
        } else if (roll < 0.2) {
          const s = await openSession(db(), pc.id);
          if (s) await stopSession(db(), s, t, 'stopped', 'pc');
          sessionId = null;
        } else {
          const r = await renewSession(db(), pc.id, sessionId, t);
          if (!r.ok) sessionId = null;
        }
        const balance = await gameBalance(db());
        expect(balance).toBeGreaterThanOrEqual(0);
      }
      await closeExpired(db(), t + 10 * MIN);
      const used = await db()
        .prepare(`SELECT COALESCE(-SUM(seconds), 0) AS s FROM ledger WHERE kind = 'usage'`)
        .first<{ s: number }>();
      expect(used!.s).toBeLessThanOrEqual(initial);
      expect(used!.s + (await gameBalance(db()))).toBe(initial);
    }
  });
});
