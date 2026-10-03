import type { Device } from './devices';
import { screenExtraOn } from './ledger';
import type { Settings } from './settings';

export interface HeartbeatInput {
  /** Whether the screen is in use right now (not locked / logged out). */
  screenActive: boolean;
  /**
   * Screen-in-use seconds the PC measured (monotonic clock) since its last acknowledged heartbeat,
   * including time it spent offline.
   */
  screenSeconds: number;
  clientVersion: string | null;
}

/** Small allowance for request latency when comparing PC-measured time with server-measured time. */
const LATENCY_SLACK_SECONDS = 5;

/**
 * Records a heartbeat. Screen time = what the PC measured, but never more than the time that
 * actually passed on the server clock since the previous heartbeat.
 */
export async function recordHeartbeat(db: D1Database, device: Device, day: string, input: HeartbeatInput, now: number) {
  const elapsed =
    device.last_heartbeat_at === null ? 86_400 : Math.max(0, Math.floor((now - device.last_heartbeat_at) / 1000)) + LATENCY_SLACK_SECONDS;
  const seconds = Math.max(0, Math.min(Math.floor(input.screenSeconds), elapsed, 86_400));

  await db.batch([
    db.prepare('UPDATE days SET screen_used_seconds = screen_used_seconds + ?1 WHERE day = ?2').bind(seconds, day),
    db
      .prepare(
        `UPDATE devices SET last_heartbeat_at = ?1, last_screen_active = ?2, silent_alerted_at = NULL,
           client_version = COALESCE(?3, client_version)
         WHERE id = ?4`,
      )
      .bind(now, input.screenActive ? 1 : 0, input.clientVersion, device.id),
  ]);
  return seconds;
}

export interface ScreenStatus {
  /** null = no limit */
  limitSeconds: number | null;
  usedSeconds: number;
  remainingSeconds: number | null;
  extraSeconds: number;
}

export async function screenStatus(db: D1Database, settings: Settings, day: string): Promise<ScreenStatus> {
  const used = await db.prepare('SELECT screen_used_seconds AS s FROM days WHERE day = ?1').bind(day).first<{ s: number }>();
  const usedSeconds = used?.s ?? 0;
  const extraSeconds = await screenExtraOn(db, day);
  if (settings.dailyScreenMinutes === 0) {
    return { limitSeconds: null, usedSeconds, remainingSeconds: null, extraSeconds };
  }
  const limitSeconds = Math.max(0, settings.dailyScreenMinutes * 60 + extraSeconds);
  return { limitSeconds, usedSeconds, remainingSeconds: Math.max(0, limitSeconds - usedSeconds), extraSeconds };
}
