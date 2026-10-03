import { HEARTBEAT_SECONDS, LEASE_SECONDS } from './env';
import type { Device } from './devices';
import { deviceView, listDevices } from './devices';
import type { GameSession } from './game';
import { openSessions } from './game';
import { gameBalance, gameUsedOn } from './ledger';
import { screenStatus } from './screen';
import type { StoredSettings } from './settings';

function leaseRemaining(s: GameSession | null, now: number): number {
  if (!s || s.ended_at !== null) return 0;
  return Math.max(0, Math.floor((s.paid_until - now) / 1000));
}

/** What the PC needs to enforce: returned by every device call. */
export async function deviceState(
  db: D1Database,
  stored: StoredSettings,
  device: Device,
  day: string,
  session: GameSession | null,
  now: number,
) {
  const lease = leaseRemaining(session, now);
  const gaming = lease > 0;
  return {
    serverTime: now,
    day,
    deviceId: device.id,
    mode: gaming ? ('gaming' as const) : ('school' as const),
    session: gaming ? { id: session!.id, leaseRemainingSeconds: lease } : null,
    game: {
      // Both exclude the prepaid-but-not-yet-played part of the current lease.
      balanceSeconds: (await gameBalance(db)) + lease,
      usedTodaySeconds: Math.max(0, (await gameUsedOn(db, day)) - (session?.day === day ? lease : 0)),
    },
    screen: await screenStatus(db, stored.settings, day),
    configVersion: stored.version,
    timing: {
      heartbeatSeconds: HEARTBEAT_SECONDS,
      leaseSeconds: LEASE_SECONDS,
      offlineBudgetSeconds: stored.settings.offlineBudgetMinutes * 60,
    },
  };
}

export type DeviceState = Awaited<ReturnType<typeof deviceState>>;

/** Overview for the parent "Today" page. */
export async function todayView(db: D1Database, stored: StoredSettings, day: string, now: number) {
  const devices = await listDevices(db);
  const names = new Map(devices.map((d) => [d.id, d.name]));
  const sessions = (await openSessions(db)).filter((s) => s.paid_until > now);
  const lease = sessions.reduce((sum, s) => sum + leaseRemaining(s, now), 0);
  const leaseToday = sessions.filter((s) => s.day === day).reduce((sum, s) => sum + leaseRemaining(s, now), 0);
  return {
    serverTime: now,
    day,
    timeZone: stored.settings.timeZone,
    game: {
      balanceSeconds: (await gameBalance(db)) + lease,
      usedTodaySeconds: Math.max(0, (await gameUsedOn(db, day)) - leaseToday),
      dailyAllowanceSeconds: stored.settings.dailyGameMinutes * 60,
      maxBalanceSeconds: stored.settings.maxGameBalanceMinutes * 60,
    },
    screen: await screenStatus(db, stored.settings, day),
    gaming: sessions.map((s) => ({
      sessionId: s.id,
      deviceId: s.device_id,
      deviceName: names.get(s.device_id) ?? s.device_id,
      startedAt: s.started_at,
      leaseRemainingSeconds: leaseRemaining(s, now),
    })),
    devices: devices.filter((d) => d.revoked === 0).map((d) => deviceView(d, now)),
  };
}
