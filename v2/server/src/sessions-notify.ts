import { minutes, notify } from './discord';
import type { Env } from './env';
import { logEvent } from './events';
import type { ClosedSession } from './game';
import { sessionUsedSeconds } from './game';
import { gameBalance } from './ledger';

const REASONS: Record<string, string> = {
  stopped: 'stopped on the PC',
  parent: 'ended by parent',
  lease_expired: 'lease expired (balance empty, PC offline, or asleep)',
  abandoned: 'PC lost track of the session',
};

/** Logs and announces sessions that just ended. Returns the Discord promises for waitUntil. */
export async function announceClosed(env: Env, closed: ClosedSession[], now: number, names: Map<string, string>): Promise<void> {
  if (closed.length === 0) return;
  const balance = await gameBalance(env.DB);
  for (const s of closed) {
    const used = await sessionUsedSeconds(env.DB, s.id);
    await logEvent(env.DB, now, {
      type: 'game_end',
      deviceId: s.device_id,
      detail: { sessionId: s.id, reason: s.end_reason, usedSeconds: used },
    });
    await notify(
      env,
      `🔵 **Gaming ended** on ${names.get(s.device_id) ?? 'PC'}: ${REASONS[s.end_reason] ?? s.end_reason}. ` +
        `Used ${minutes(used)}, balance left ${minutes(balance)}.`,
    );
  }
}
