import { SILENT_ALERT_MINUTES } from './env';
import type { Env } from './env';
import type { Device } from './devices';
import { listDevices } from './devices';
import { notify } from './discord';
import { logEvent } from './events';
import { closeExpired } from './game';
import { ensureDays } from './ledger';
import { announceClosed } from './sessions-notify';
import { loadSettings } from './settings';
import { clock } from './time';

/** Runs every few minutes: opens new days, closes expired sessions, flags PCs that went silent. */
export async function runCron(env: Env, now: number): Promise<void> {
  const db = env.DB;
  const { settings } = await loadSettings(db);
  await ensureDays(db, settings, now);

  const names = new Map((await listDevices(db)).map((d) => [d.id, d.name]));
  await announceClosed(env, await closeExpired(db, now), now, names);

  const silent = await db
    .prepare(
      `UPDATE devices SET silent_alerted_at = ?1
       WHERE revoked = 0 AND last_screen_active = 1 AND silent_alerted_at IS NULL
         AND last_heartbeat_at IS NOT NULL AND last_heartbeat_at < ?2
       RETURNING *`,
    )
    .bind(now, now - SILENT_ALERT_MINUTES * 60_000)
    .all<Device>();
  for (const d of silent.results) {
    await logEvent(db, now, { type: 'pc_silent', deviceId: d.id, detail: { lastHeartbeatAt: d.last_heartbeat_at } });
    await notify(
      env,
      `⚠️ **${d.name} went silent** — last contact at ${clock(d.last_heartbeat_at!, settings.timeZone)} while in use. ` +
        `Turned off, offline, or tampered with?`,
    );
  }
}
