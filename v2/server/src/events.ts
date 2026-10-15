export async function logEvent(
  db: D1Database,
  now: number,
  e: { type: string; deviceId?: string | null; detail?: unknown; by?: string | null },
): Promise<void> {
  const detail = e.detail === undefined ? null : typeof e.detail === 'string' ? e.detail : JSON.stringify(e.detail);
  await db
    .prepare('INSERT INTO events (at, device_id, type, detail, by) VALUES (?1, ?2, ?3, ?4, ?5)')
    .bind(now, e.deviceId ?? null, e.type, detail, e.by ?? null)
    .run();
}
