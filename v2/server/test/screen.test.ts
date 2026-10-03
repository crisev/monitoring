import { beforeEach, describe, expect, it } from 'vitest';
import type { Device } from '../src/devices';
import { addGrant, ensureDays } from '../src/ledger';
import { recordHeartbeat, screenStatus } from '../src/screen';
import type { Settings } from '../src/settings';
import { DAY, db, makeDevice, MIN, resetDb, SEC, T0, useSettings } from './helpers';

let settings: Settings;

async function beat(pc: Device, now: number, screenSeconds: number, screenActive = true) {
  const fresh = (await db().prepare('SELECT * FROM devices WHERE id = ?1').bind(pc.id).first<Device>())!;
  const day = await ensureDays(db(), settings, now);
  await recordHeartbeat(db(), fresh, day, { screenActive, screenSeconds, clientVersion: null }, now);
  return day;
}

beforeEach(async () => {
  await resetDb();
  settings = (await useSettings({ dailyScreenMinutes: 180 })).settings;
});

describe('screen time', () => {
  it('counts the seconds the PC measured', async () => {
    const pc = await makeDevice();
    let day = await beat(pc, T0, 0);
    for (let t = 30; t <= 600; t += 30) day = await beat(pc, T0 + t * SEC, 30);
    const s = await screenStatus(db(), settings, day);
    expect(s.usedSeconds).toBe(600);
    expect(s.remainingSeconds).toBe(180 * 60 - 600);
  });

  it('never counts more than the server-side elapsed time (+5s latency slack)', async () => {
    const pc = await makeDevice();
    await beat(pc, T0, 0);
    const day = await beat(pc, T0 + 30 * SEC, 5000); // a buggy or tampered client
    expect((await screenStatus(db(), settings, day)).usedSeconds).toBe(35);
  });

  it('adds time the PC counted while offline', async () => {
    const pc = await makeDevice();
    await beat(pc, T0, 0);
    // Offline for 2 hours, of which 50 minutes with the screen in use.
    const day = await beat(pc, T0 + 120 * MIN, 50 * 60);
    expect((await screenStatus(db(), settings, day)).usedSeconds).toBe(50 * 60);
  });

  it('extra school time applies to today only', async () => {
    const pc = await makeDevice();
    await addGrant(db(), settings, { bucket: 'screen', seconds: 30 * 60, by: 'mom' }, T0);
    const today = await beat(pc, T0, 0);
    expect((await screenStatus(db(), settings, today)).limitSeconds).toBe(210 * 60);
    const tomorrow = await beat(pc, T0 + DAY, 0);
    expect(await screenStatus(db(), settings, tomorrow)).toMatchObject({ limitSeconds: 180 * 60, usedSeconds: 0 });
  });

  it('0 means no limit', async () => {
    settings = (await useSettings({ dailyScreenMinutes: 0 })).settings;
    const pc = await makeDevice();
    const day = await beat(pc, T0, 0);
    expect(await screenStatus(db(), settings, day)).toMatchObject({ limitSeconds: null, remainingSeconds: null });
  });
});
