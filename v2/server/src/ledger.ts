import type { Settings } from './settings';
import { dayOf, nextDay } from './time';

/** Never backfill more than this many missed days in one go. */
const MAX_BACKFILL_DAYS = 62;

/**
 * Opens every calendar day up to and including today that has not been opened yet:
 * adds the daily allowance, then trims the game balance to the carry-over cap.
 * Idempotent and safe to call concurrently; returns today's date.
 */
export async function ensureDays(db: D1Database, settings: Settings, now: number): Promise<string> {
  const today = dayOf(now, settings.timeZone);
  const last = await db.prepare('SELECT MAX(day) AS d FROM days').first<{ d: string | null }>();
  if (last?.d && last.d >= today) return today;

  const pending: string[] = [];
  if (last?.d) {
    for (let d = nextDay(last.d); d <= today; d = nextDay(d)) pending.push(d);
  } else {
    pending.push(today);
  }
  for (const day of pending.slice(-MAX_BACKFILL_DAYS)) {
    await openDay(db, settings, day, now);
  }
  return today;
}

async function openDay(db: D1Database, settings: Settings, day: string, now: number): Promise<void> {
  const capSeconds = settings.maxGameBalanceMinutes * 60;
  const allowanceSeconds = settings.dailyGameMinutes * 60;
  // balance = min(cap, balance + allowance). A D1 batch runs as one transaction,
  // and the NOT EXISTS guards make a second run for the same day a no-op.
  await db.batch([
    db
      .prepare(
        `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
         SELECT 'game', ?1, 'daily_allowance', ?2, 'system', NULL, ?3
         WHERE ?2 > 0 AND NOT EXISTS (SELECT 1 FROM days WHERE day = ?1)`,
      )
      .bind(day, allowanceSeconds, now),
    db
      .prepare(
        `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
         SELECT 'game', ?1, 'cap', ?2 - bal, 'system', 'carry-over cap', ?3
         FROM (SELECT COALESCE(SUM(seconds), 0) AS bal FROM ledger WHERE bucket = 'game')
         WHERE bal > ?2 AND NOT EXISTS (SELECT 1 FROM days WHERE day = ?1)`,
      )
      .bind(day, capSeconds, now),
    db.prepare('INSERT INTO days (day, opened_at) VALUES (?1, ?2) ON CONFLICT (day) DO NOTHING').bind(day, now),
  ]);
}

/** Game seconds not yet spent or prepaid. */
export async function gameBalance(db: D1Database): Promise<number> {
  const row = await db
    .prepare(`SELECT COALESCE(SUM(seconds), 0) AS s FROM ledger WHERE bucket = 'game'`)
    .first<{ s: number }>();
  return row!.s;
}

/** Game seconds spent on `day` (sessions are attributed to the day they started). */
export async function gameUsedOn(db: D1Database, day: string): Promise<number> {
  const row = await db
    .prepare(`SELECT COALESCE(-SUM(seconds), 0) AS s FROM ledger WHERE bucket = 'game' AND kind = 'usage' AND day = ?1`)
    .bind(day)
    .first<{ s: number }>();
  return row!.s;
}

/** Extra screen seconds granted for `day`. */
export async function screenExtraOn(db: D1Database, day: string): Promise<number> {
  const row = await db
    .prepare(`SELECT COALESCE(SUM(seconds), 0) AS s FROM ledger WHERE bucket = 'screen' AND day = ?1`)
    .bind(day)
    .first<{ s: number }>();
  return row!.s;
}

export interface GrantInput {
  bucket: 'game' | 'screen';
  seconds: number;
  by: string;
  note?: string | null;
}

export interface GrantResult {
  /** Ledger row, or null if nothing changed (e.g. removing time when none is left). */
  id: number | null;
  /** Seconds actually added (negative = removed); can be less than asked when removing. */
  seconds: number;
}

/**
 * Parent grant (positive) or correction (negative). Removing never takes a balance below zero:
 * game time stops at 0, and today's screen-time limit stops at 0.
 */
export async function addGrant(db: D1Database, settings: Settings, input: GrantInput, now: number): Promise<GrantResult> {
  const day = await ensureDays(db, settings, now);
  if (input.seconds >= 0) {
    if (input.seconds === 0) return { id: null, seconds: 0 };
    const row = await db
      .prepare(
        `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
         VALUES (?1, ?2, 'grant', ?3, ?4, ?5, ?6) RETURNING id`,
      )
      .bind(input.bucket, day, input.seconds, input.by, input.note ?? null, now)
      .first<{ id: number }>();
    return { id: row!.id, seconds: input.seconds };
  }

  if (input.bucket === 'game') {
    // Clamped in SQL, so two parents removing time at the same moment can't push the balance below zero.
    const row = await db
      .prepare(
        `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
         SELECT 'game', ?1, 'adjustment', s, ?3, ?4, ?5
         FROM (SELECT MAX(?2, -MAX(0, COALESCE(SUM(seconds), 0))) AS s FROM ledger WHERE bucket = 'game')
         WHERE s < 0
         RETURNING id, seconds`,
      )
      .bind(day, input.seconds, input.by, input.note ?? null, now)
      .first<{ id: number; seconds: number }>();
    return row ? { id: row.id, seconds: row.seconds } : { id: null, seconds: 0 };
  }

  // Screen time: today's limit is the daily limit plus today's extra; it can't go below zero.
  if (settings.dailyScreenMinutes === 0) return { id: null, seconds: 0 }; // no limit: nothing to take
  const limit = settings.dailyScreenMinutes * 60 + (await screenExtraOn(db, day));
  const seconds = Math.max(input.seconds, -Math.max(0, limit));
  if (seconds === 0) return { id: null, seconds: 0 };
  const row = await db
    .prepare(
      `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
       VALUES ('screen', ?1, 'adjustment', ?2, ?3, ?4, ?5) RETURNING id`,
    )
    .bind(day, seconds, input.by, input.note ?? null, now)
    .first<{ id: number }>();
  return { id: row!.id, seconds };
}
