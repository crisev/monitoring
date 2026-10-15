import { LEASE_SECONDS } from './env';
import { gameBalance } from './ledger';

export interface GameSession {
  id: string;
  device_id: string;
  day: string;
  started_at: number;
  paid_until: number;
  ended_at: number | null;
  end_reason: string | null;
  ended_by: string | null;
}

export interface ClosedSession {
  id: string;
  device_id: string;
  started_at: number;
  ended_at: number;
  end_reason: string;
}

/** Closes sessions whose prepaid lease ran out without being renewed. */
export async function closeExpired(db: D1Database, now: number, deviceId?: string): Promise<ClosedSession[]> {
  const res = await db
    .prepare(
      `UPDATE game_sessions SET ended_at = paid_until, end_reason = 'lease_expired', ended_by = 'system'
       WHERE ended_at IS NULL AND paid_until <= ?1 AND (?2 IS NULL OR device_id = ?2)
       RETURNING id, device_id, started_at, ended_at, end_reason`,
    )
    .bind(now, deviceId ?? null)
    .all<ClosedSession>();
  return res.results;
}

export async function openSession(db: D1Database, deviceId: string): Promise<GameSession | null> {
  return db
    .prepare('SELECT * FROM game_sessions WHERE device_id = ?1 AND ended_at IS NULL')
    .bind(deviceId)
    .first<GameSession>();
}

export async function openSessions(db: D1Database): Promise<GameSession[]> {
  const res = await db.prepare('SELECT * FROM game_sessions WHERE ended_at IS NULL').all<GameSession>();
  return res.results;
}

export async function getSession(db: D1Database, id: string): Promise<GameSession | null> {
  return db.prepare('SELECT * FROM game_sessions WHERE id = ?1').bind(id).first<GameSession>();
}

export type StartResult =
  | { ok: true; session: GameSession; created: boolean }
  | { ok: false; error: 'no_balance' };

/**
 * Starts a gaming session and debits the first lease up front.
 * Idempotent: returns the already open session if there is one.
 */
export async function startSession(db: D1Database, deviceId: string, day: string, now: number): Promise<StartResult> {
  await closeExpired(db, now, deviceId);
  const existing = await openSession(db, deviceId);
  if (existing) return { ok: true, session: existing, created: false };

  const balance = await gameBalance(db);
  if (balance <= 0) return { ok: false, error: 'no_balance' };

  const lease = Math.min(LEASE_SECONDS, balance);
  const id = crypto.randomUUID();
  try {
    await db.batch([
      db
        .prepare('INSERT INTO game_sessions (id, device_id, day, started_at, paid_until) VALUES (?1, ?2, ?3, ?4, ?5)')
        .bind(id, deviceId, day, now, now + lease * 1000),
      db
        .prepare(
          `INSERT INTO ledger (bucket, day, kind, seconds, session_id, by, created_at)
           VALUES ('game', ?1, 'usage', ?2, ?3, ?4, ?5)`,
        )
        .bind(day, -lease, id, `device:${deviceId}`, now),
    ]);
  } catch (e) {
    // Lost a race against a concurrent start (unique open-session index).
    const raced = await openSession(db, deviceId);
    if (raced) return { ok: true, session: raced, created: false };
    throw e;
  }
  return { ok: true, session: (await getSession(db, id))!, created: true };
}

export type RenewResult =
  | { ok: true; session: GameSession }
  | { ok: false; session: GameSession | null };

/**
 * Extends the lease of an open session to `now + LEASE_SECONDS`, debiting exactly the extension,
 * limited by the remaining balance. Returns ok:false if the session is no longer running.
 */
export async function renewSession(db: D1Database, deviceId: string, sessionId: string, now: number): Promise<RenewResult> {
  let s = await getSession(db, sessionId);
  if (!s || s.device_id !== deviceId) return { ok: false, session: null };
  if (s.ended_at !== null) return { ok: false, session: s };
  // Lease already ran out: not renewable. The caller closes it via closeExpired().
  if (s.paid_until <= now) return { ok: false, session: s };

  const wanted = Math.floor((now + LEASE_SECONDS * 1000 - s.paid_until) / 1000);
  const ext = Math.min(wanted, Math.max(0, await gameBalance(db)));
  if (ext > 0) {
    await db.batch([
      // Debit only while the session is still open (a concurrent stop may have closed it).
      db
        .prepare(
          `UPDATE ledger SET seconds = seconds - ?1 WHERE session_id = ?2
           AND EXISTS (SELECT 1 FROM game_sessions WHERE id = ?2 AND ended_at IS NULL)`,
        )
        .bind(ext, sessionId),
      db
        .prepare('UPDATE game_sessions SET paid_until = paid_until + ?1 * 1000 WHERE id = ?2 AND ended_at IS NULL')
        .bind(ext, sessionId),
    ]);
    s = (await getSession(db, sessionId))!;
  }
  return { ok: s.ended_at === null, session: s };
}

/** Ends a session now and refunds the unused part of the prepaid lease. */
export async function stopSession(
  db: D1Database,
  session: GameSession,
  now: number,
  reason: string,
  by: string,
): Promise<ClosedSession | null> {
  if (session.ended_at !== null) return null;
  if (session.paid_until <= now) {
    const closed = await closeExpired(db, now, session.device_id);
    return closed.find((c) => c.id === session.id) ?? null;
  }
  const refund = Math.floor((session.paid_until - now) / 1000);
  const endAt = session.paid_until - refund * 1000;
  const [, upd] = await db.batch<ClosedSession>([
    db
      .prepare(
        `UPDATE ledger SET seconds = seconds + ?1 WHERE session_id = ?2
         AND EXISTS (SELECT 1 FROM game_sessions WHERE id = ?2 AND ended_at IS NULL)`,
      )
      .bind(refund, session.id),
    db
      .prepare(
        `UPDATE game_sessions SET ended_at = ?1, paid_until = ?1, end_reason = ?2, ended_by = ?3
         WHERE id = ?4 AND ended_at IS NULL
         RETURNING id, device_id, started_at, ended_at, end_reason`,
      )
      .bind(endAt, reason, by, session.id),
  ]);
  return upd.results[0] ?? null;
}

/** Seconds actually spent in a session (from its ledger row). */
export async function sessionUsedSeconds(db: D1Database, sessionId: string): Promise<number> {
  const row = await db.prepare('SELECT -seconds AS s FROM ledger WHERE session_id = ?1').bind(sessionId).first<{ s: number }>();
  return row?.s ?? 0;
}
