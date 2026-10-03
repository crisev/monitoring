import { Hono } from 'hono';
import { csrf } from 'hono/csrf';
import { z } from 'zod';
import { activityReport } from '../activity';
import type { AppVars } from '../auth';
import { requireParent } from '../auth';
import { deviceView, issueEnrollCode, listDevices, revokeDevice } from '../devices';
import { minutes, notify } from '../discord';
import type { Env } from '../env';
import { logEvent } from '../events';
import { closeExpired, openSessions, stopSession } from '../game';
import { addGrant, ensureDays, gameBalance } from '../ledger';
import { announceClosed } from '../sessions-notify';
import { loadSettings, saveSettings, SettingsSchema } from '../settings';
import { todayView } from '../state';
import { dayOf } from '../time';

const app = new Hono<{ Bindings: Env; Variables: AppVars }>();
app.use('*', csrf());
app.use('*', async (c, next) => {
  // Mutations only accept JSON, which browsers cannot send cross-site without a CORS preflight.
  if (c.req.method !== 'GET' && !(c.req.header('content-type') ?? '').startsWith('application/json')) {
    return c.json({ error: 'json_required' }, 415);
  }
  return next();
});
app.use('*', requireParent);

const GrantBody = z.object({
  bucket: z.enum(['game', 'screen']),
  minutes: z.number().int().min(-1440).max(1440).refine((m) => m !== 0, 'minutes must not be 0'),
  note: z.string().trim().max(200).optional(),
});
const NoteBody = z.object({ note: z.string().trim().max(200).optional() }).default({});
const DeviceBody = z.object({ name: z.string().trim().min(1).max(60) });
const Paging = z.object({
  limit: z.coerce.number().int().min(1).max(500).default(100),
  before: z.coerce.number().int().optional(),
});

const Day = z.string().regex(/^\d{4}-\d{2}-\d{2}$/);
const ActivityQuery = z.object({ from: Day.optional(), to: Day.optional() });
const MAX_ACTIVITY_DAYS = 62;

async function json(c: { req: { raw: Request } }): Promise<unknown> {
  return c.req.raw.json().catch(() => undefined);
}

async function deviceNames(db: D1Database) {
  return new Map((await listDevices(db)).map((d) => [d.id, d.name]));
}

app.get('/me', (c) => c.json({ email: c.get('parentEmail') }));

app.get('/today', async (c) => {
  const now = c.get('now');
  const db = c.env.DB;
  const stored = await loadSettings(db);
  const day = await ensureDays(db, stored.settings, now);
  const closed = await closeExpired(db, now);
  c.executionCtx.waitUntil(announceClosed(c.env, closed, now, await deviceNames(db)));
  return c.json(await todayView(db, stored, day, now));
});

app.post('/grant', async (c) => {
  const now = c.get('now');
  const email = c.get('parentEmail');
  const parsed = GrantBody.safeParse(await json(c));
  if (!parsed.success) return c.json({ error: 'bad_request', issues: parsed.error.issues }, 400);
  const g = parsed.data;
  const db = c.env.DB;
  const stored = await loadSettings(db);
  const id = await addGrant(db, stored.settings, { bucket: g.bucket, seconds: g.minutes * 60, by: email, note: g.note }, now);
  await logEvent(db, now, { type: 'grant', by: email, detail: { ledgerId: id, ...g } });

  const what = g.bucket === 'game' ? 'game time' : 'school/screen time today';
  const tail = g.bucket === 'game' ? ` Game balance now ${minutes(await gameBalance(db))}.` : '';
  const sign = g.minutes > 0 ? '+' : '';
  c.executionCtx.waitUntil(
    notify(c.env, `➕ **${sign}${g.minutes}m ${what}** by ${email}${g.note ? ` — “${g.note}”` : ''}.${tail}`),
  );
  const day = await ensureDays(db, stored.settings, now);
  return c.json(await todayView(db, stored, day, now));
});

app.post('/end-gaming', async (c) => {
  const now = c.get('now');
  const email = c.get('parentEmail');
  const parsed = NoteBody.safeParse(await json(c));
  if (!parsed.success) return c.json({ error: 'bad_request' }, 400);
  const db = c.env.DB;
  const closed = [];
  for (const s of await openSessions(db)) {
    const r = await stopSession(db, s, now, 'parent', email);
    if (r) closed.push(r);
  }
  await logEvent(db, now, { type: 'end_gaming', by: email, detail: { sessions: closed.length, note: parsed.data.note } });
  c.executionCtx.waitUntil(announceClosed(c.env, closed, now, await deviceNames(db)));
  const stored = await loadSettings(db);
  const day = await ensureDays(db, stored.settings, now);
  return c.json(await todayView(db, stored, day, now));
});

app.get('/activity', async (c) => {
  const q = ActivityQuery.safeParse(c.req.query());
  if (!q.success) return c.json({ error: 'bad_request', issues: q.error.issues }, 400);
  const { settings } = await loadSettings(c.env.DB);
  const today = dayOf(c.get('now'), settings.timeZone);
  const to = q.data.to ?? q.data.from ?? today;
  const from = q.data.from ?? to;
  const span = (Date.parse(to) - Date.parse(from)) / 86_400_000;
  if (span < 0 || span > MAX_ACTIVITY_DAYS) return c.json({ error: 'bad_range' }, 400);
  return c.json({ ...(await activityReport(c.env.DB, from, to)), today, timeZone: settings.timeZone });
});

app.get('/settings', async (c) => c.json(await loadSettings(c.env.DB)));

app.put('/settings', async (c) => {
  const now = c.get('now');
  const email = c.get('parentEmail');
  const parsed = SettingsSchema.safeParse(await json(c));
  if (!parsed.success) return c.json({ error: 'bad_request', issues: parsed.error.issues }, 400);
  const version = await saveSettings(c.env.DB, parsed.data, email, now);
  await logEvent(c.env.DB, now, { type: 'settings', by: email, detail: { version } });
  c.executionCtx.waitUntil(notify(c.env, `⚙️ Settings changed by ${email} (v${version}).`));
  return c.json(await loadSettings(c.env.DB));
});

app.get('/ledger', async (c) => {
  const p = Paging.parse(c.req.query());
  const res = await c.env.DB
    .prepare(
      `SELECT id, bucket, day, kind, seconds, session_id AS sessionId, by, note, created_at AS createdAt
       FROM ledger WHERE (?1 IS NULL OR id < ?1) ORDER BY id DESC LIMIT ?2`,
    )
    .bind(p.before ?? null, p.limit)
    .all();
  return c.json({ items: res.results });
});

app.get('/events', async (c) => {
  const p = Paging.parse(c.req.query());
  const res = await c.env.DB
    .prepare(
      `SELECT e.id, e.at, e.type, e.detail, e.by, e.device_id AS deviceId, d.name AS deviceName
       FROM events e LEFT JOIN devices d ON d.id = e.device_id
       WHERE (?1 IS NULL OR e.id < ?1) ORDER BY e.id DESC LIMIT ?2`,
    )
    .bind(p.before ?? null, p.limit)
    .all();
  return c.json({ items: res.results });
});

app.get('/devices', async (c) => {
  const now = c.get('now');
  return c.json({ items: (await listDevices(c.env.DB)).filter((d) => d.revoked === 0).map((d) => deviceView(d, now)) });
});

app.post('/devices', async (c) => {
  const now = c.get('now');
  const parsed = DeviceBody.safeParse(await json(c));
  if (!parsed.success) return c.json({ error: 'bad_request', issues: parsed.error.issues }, 400);
  const res = await issueEnrollCode(c.env.DB, now, { name: parsed.data.name });
  await logEvent(c.env.DB, now, { type: 'device_added', deviceId: res!.deviceId, by: c.get('parentEmail') });
  return c.json(res);
});

app.post('/devices/:id/reset', async (c) => {
  const now = c.get('now');
  const res = await issueEnrollCode(c.env.DB, now, { deviceId: c.req.param('id') });
  if (!res) return c.json({ error: 'not_found' }, 404);
  await logEvent(c.env.DB, now, { type: 'device_reset', deviceId: res.deviceId, by: c.get('parentEmail') });
  return c.json(res);
});

app.post('/devices/:id/revoke', async (c) => {
  const now = c.get('now');
  const ok = await revokeDevice(c.env.DB, c.req.param('id'));
  if (!ok) return c.json({ error: 'not_found' }, 404);
  await logEvent(c.env.DB, now, { type: 'device_revoked', deviceId: c.req.param('id'), by: c.get('parentEmail') });
  return c.json({ ok: true });
});

export default app;
