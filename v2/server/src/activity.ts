import { z } from 'zod';
import { dayOf } from './time';

export const SLOT_MS = 5 * 60 * 1000;
const UPSERT_CHUNK = 50;

// Lenient on purpose: values out of range are clamped or shortened rather than rejected, because a
// PC that gets a 400 for a batch keeps resending it, and one odd item must not stop all uploads.
const clamped = (min: number, max: number) =>
  z.number().refine(Number.isFinite).transform((v) => Math.min(max, Math.max(min, Math.round(v))));
const shortened = (max: number) => z.string().transform((s) => s.trim().slice(0, max));

export const ActivityItemSchema = z.object({
  /** How long ago (seconds, PC monotonic clock) this sample period started, relative to sending. */
  secondsAgo: clamped(0, 7 * 86_400),
  mode: z.enum(['school', 'gaming']),
  app: shortened(100).pipe(z.string().min(1)),
  site: shortened(300).nullish(),
  title: shortened(1000).nullish(),
  seconds: clamped(0, 600).default(0),
  audioSeconds: clamped(0, 600).default(0),
  blocked: clamped(0, 1000).default(0),
});
export type ActivityItem = z.output<typeof ActivityItemSchema>;

/** Validates items one by one; unusable items are dropped instead of failing the whole batch. */
export function parseActivityItems(raw: unknown[]): { items: ActivityItem[]; dropped: number } {
  const items: ActivityItem[] = [];
  for (const r of raw) {
    const p = ActivityItemSchema.safeParse(r);
    if (p.success) items.push(p.data);
  }
  return { items, dropped: raw.length - items.length };
}

export function normalizeApp(app: string): string {
  return app.trim().replace(/\.exe$/i, '');
}

/** "https://www.pbinfo.ro/probleme/1" -> "pbinfo.ro"; "WWW.Example.com:8080" -> "example.com". */
export function normalizeSite(site: string | null | undefined): string {
  if (!site) return '';
  let s = site.trim().toLowerCase();
  if (!s) return '';
  try {
    s = new URL(s.includes('://') ? s : `http://${s}`).hostname;
  } catch {
    return s.slice(0, 253);
  }
  return s.replace(/^www\./, '').slice(0, 253);
}

/** Adds a batch of samples, summing them into 5-minute slots. Returns how many items were stored. */
export async function recordActivity(db: D1Database, deviceId: string, timeZone: string, items: ActivityItem[], now: number) {
  const stmts = items
    .filter((i) => normalizeApp(i.app) !== '')
    .filter((i) => i.seconds > 0 || i.audioSeconds > 0 || i.blocked > 0)
    .map((i) => {
      const at = now - i.secondsAgo * 1000;
      const slot = Math.floor(at / SLOT_MS) * SLOT_MS;
      return db
        .prepare(
          `INSERT INTO activity (device_id, day, slot, mode, app, site, title, fg_seconds, audio_seconds, blocked)
           VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
           ON CONFLICT (device_id, slot, mode, app, site, title) DO UPDATE SET
             fg_seconds = fg_seconds + excluded.fg_seconds,
             audio_seconds = audio_seconds + excluded.audio_seconds,
             blocked = blocked + excluded.blocked`,
        )
        .bind(
          deviceId,
          dayOf(slot, timeZone),
          slot,
          i.mode,
          normalizeApp(i.app),
          normalizeSite(i.site),
          (i.title ?? '').trim().slice(0, 200),
          i.seconds,
          i.audioSeconds,
          i.blocked,
        );
    });
  for (let k = 0; k < stmts.length; k += UPSERT_CHUNK) {
    await db.batch(stmts.slice(k, k + UPSERT_CHUNK));
  }
  return stmts.length;
}

interface Totals { seconds: number; school: number; gaming: number; audio: number; blocked: number }

/** Activity summary for days `from`..`to` (inclusive, YYYY-MM-DD). */
export async function activityReport(db: D1Database, from: string, to: string) {
  const range = (sql: string) => db.prepare(sql).bind(from, to);
  const sums = `SUM(fg_seconds) AS seconds,
    SUM(CASE WHEN mode = 'school' THEN fg_seconds ELSE 0 END) AS school,
    SUM(CASE WHEN mode = 'gaming' THEN fg_seconds ELSE 0 END) AS gaming,
    SUM(audio_seconds) AS audio, SUM(blocked) AS blocked`;

  const [totals, apps, sites, titles, perDay, slots] = await db.batch([
    range(`SELECT ${sums} FROM activity WHERE day BETWEEN ?1 AND ?2`),
    range(`SELECT app, ${sums} FROM activity WHERE day BETWEEN ?1 AND ?2 GROUP BY app ORDER BY seconds DESC, blocked DESC LIMIT 50`),
    range(`SELECT site, ${sums} FROM activity WHERE day BETWEEN ?1 AND ?2 AND site != '' GROUP BY site ORDER BY seconds DESC LIMIT 50`),
    range(
      `SELECT app, site, title, ${sums} FROM activity WHERE day BETWEEN ?1 AND ?2 AND title != ''
       GROUP BY app, site, title ORDER BY seconds DESC LIMIT 40`,
    ),
    range(`SELECT day, ${sums} FROM activity WHERE day BETWEEN ?1 AND ?2 GROUP BY day ORDER BY day`),
    // Per-slot breakdown for the single-day timeline.
    db
      .prepare(
        `SELECT slot, mode, app, SUM(fg_seconds) AS seconds FROM activity
         WHERE day = ?1 AND ?1 = ?2 AND fg_seconds > 0 GROUP BY slot, mode, app ORDER BY slot`,
      )
      .bind(from, to),
  ]);

  const t = (totals.results[0] ?? {}) as Partial<Totals>;
  const timeline = new Map<number, { slot: number; seconds: number; gaming: number; topApp: string; topSeconds: number }>();
  for (const r of slots.results as { slot: number; mode: string; app: string; seconds: number }[]) {
    const e = timeline.get(r.slot) ?? { slot: r.slot, seconds: 0, gaming: 0, topApp: r.app, topSeconds: 0 };
    e.seconds += r.seconds;
    if (r.mode === 'gaming') e.gaming += r.seconds;
    if (r.seconds > e.topSeconds) {
      e.topApp = r.app;
      e.topSeconds = r.seconds;
    }
    timeline.set(r.slot, e);
  }

  return {
    from,
    to,
    totals: {
      seconds: t.seconds ?? 0,
      school: t.school ?? 0,
      gaming: t.gaming ?? 0,
      audio: t.audio ?? 0,
      blocked: t.blocked ?? 0,
    },
    apps: apps.results as (Totals & { app: string })[],
    sites: sites.results as (Totals & { site: string })[],
    titles: titles.results as (Totals & { app: string; site: string; title: string })[],
    perDay: perDay.results as (Totals & { day: string })[],
    timeline: [...timeline.values()].map(({ slot, seconds, gaming, topApp }) => ({
      slot,
      seconds,
      mode: gaming * 2 >= seconds ? ('gaming' as const) : ('school' as const),
      topApp,
    })),
    slotMinutes: SLOT_MS / 60_000,
  };
}
