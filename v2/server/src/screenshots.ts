import { dayOf } from './time';

/** How long screenshots are kept. */
export const SCREENSHOT_KEEP_MS = 3 * 24 * 60 * 60 * 1000;
/** D1 stores at most ~2 MB per row; larger images still go to Discord but are not kept. */
export const MAX_STORED_SCREENSHOT_BYTES = 1_900_000;

export interface ScreenshotInfo {
  id: number;
  deviceId: string;
  deviceName: string | null;
  at: number;
  day: string;
  mode: 'school' | 'gaming';
  size: number;
}

/** Stores a screenshot. Returns its id, or null if it is too large to keep. */
export async function storeScreenshot(
  db: D1Database,
  input: { deviceId: string; mode: 'school' | 'gaming'; contentType: string; image: ArrayBuffer; timeZone: string },
  now: number,
): Promise<number | null> {
  if (input.image.byteLength > MAX_STORED_SCREENSHOT_BYTES) return null;
  const row = await db
    .prepare(
      `INSERT INTO screenshots (device_id, at, day, mode, content_type, size, image)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7) RETURNING id`,
    )
    .bind(input.deviceId, now, dayOf(now, input.timeZone), input.mode, input.contentType, input.image.byteLength, input.image)
    .first<{ id: number }>();
  return row?.id ?? null;
}

/** Deletes screenshots older than 3 days. Returns how many were removed. */
export async function pruneScreenshots(db: D1Database, now: number): Promise<number> {
  const res = await db.prepare('DELETE FROM screenshots WHERE at < ?1').bind(now - SCREENSHOT_KEEP_MS).run();
  return res.meta.changes ?? 0;
}

/** Screenshots of one day (newest first), without the image data, plus how many each kept day has. */
export async function listScreenshots(db: D1Database, day: string, mode: 'school' | 'gaming' | null, now: number) {
  const since = now - SCREENSHOT_KEEP_MS;
  const [items, days] = await db.batch([
    db
      .prepare(
        `SELECT s.id, s.device_id AS deviceId, d.name AS deviceName, s.at, s.day, s.mode, s.size
         FROM screenshots s LEFT JOIN devices d ON d.id = s.device_id
         WHERE s.day = ?1 AND s.at >= ?2 AND (?3 IS NULL OR s.mode = ?3)
         ORDER BY s.at DESC LIMIT 500`,
      )
      .bind(day, since, mode),
    db
      .prepare(
        `SELECT day, COUNT(*) AS count,
           SUM(CASE WHEN mode = 'school' THEN 1 ELSE 0 END) AS school,
           SUM(CASE WHEN mode = 'gaming' THEN 1 ELSE 0 END) AS gaming
         FROM screenshots WHERE at >= ?1 GROUP BY day ORDER BY day DESC`,
      )
      .bind(since),
  ]);
  return {
    items: items.results as unknown as ScreenshotInfo[],
    days: days.results as { day: string; count: number; school: number; gaming: number }[],
  };
}

/** The image of one screenshot, or null if it doesn't exist (any more). */
export async function getScreenshotImage(db: D1Database, id: number, now: number) {
  const row = await db
    .prepare('SELECT content_type, image FROM screenshots WHERE id = ?1 AND at >= ?2')
    .bind(id, now - SCREENSHOT_KEEP_MS)
    .first<{ content_type: string; image: ArrayBuffer | number[] }>();
  if (!row) return null;
  // D1 returns BLOBs as an array of bytes (older runtimes) or an ArrayBuffer.
  const bytes = row.image instanceof ArrayBuffer ? new Uint8Array(row.image) : Uint8Array.from(row.image);
  return { contentType: row.content_type, bytes };
}
