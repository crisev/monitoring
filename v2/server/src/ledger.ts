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

/** Parent grant (positive) or correction (negative). */
export async function addGrant(db: D1Database, settings: Settings, input: GrantInput, now: number): Promise<number> {
  const day = await ensureDays(db, settings, now);
  const kind = input.seconds >= 0 ? 'grant' : 'adjustment';
  const row = await db
    .prepare(
      `INSERT INTO ledger (bucket, day, kind, seconds, by, note, created_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7) RETURNING id`,
    )
    .bind(input.bucket, day, kind, input.seconds, input.by, input.note ?? null, now)
    .first<{ id: number }>();
  return row!.id;
}
