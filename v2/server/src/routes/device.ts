import { Hono } from 'hono';
import { z } from 'zod';
import type { AppVars } from '../auth';
import { requireDevice } from '../auth';
import { enroll } from '../devices';
import { minutes, notify, sendImage } from '../discord';
import type { Env } from '../env';
import { logEvent } from '../events';
import { closeExpired, openSession, renewSession, startSession, stopSession } from '../game';
import type { GameSession } from '../game';
import { ensureDays, gameBalance } from '../ledger';
import { recordHeartbeat } from '../screen';
import { announceClosed } from '../sessions-notify';
import { loadSettings } from '../settings';
import { deviceState } from '../state';
import { clock } from '../time';

const app = new Hono<{ Bindings: Env; Variables: AppVars }>();

const EnrollBody = z.object({ code: z.string().min(4).max(40) });
const HeartbeatBody = z.object({
  sessionId: z.string().max(64).nullish(),
  screenActive: z.boolean(),
  /** Screen-in-use seconds measured on the PC since the last acknowledged heartbeat. */
  screenSeconds: z.number().min(0).max(86_400).optional().default(0),
  clientVersion: z.string().max(40).nullish(),
});
const EventsBody = z.object({
  events: z
    .array(z.object({ type: z.string().min(1).max(50), detail: z.unknown().optional() }))
    .max(100),
});
/** Device event types that are also posted to Discord. */
const NOTIFY_TYPES = new Set(['client_started', 'tamper', 'shutdown', 'agent_missing']);
const MAX_SCREENSHOT_BYTES = 8 * 1024 * 1024;

async function body<T extends z.ZodTypeAny>(req: Request, schema: T): Promise<z.infer<T> | null> {
  const json = await req.json().catch(() => null);
  const parsed = schema.safeParse(json);
  return parsed.success ? parsed.data : null;
}

app.post('/enroll', async (c) => {
  const now = c.get('now');
  const input = await body(c.req.raw, EnrollBody);
  if (!input) return c.json({ error: 'bad_request' }, 400);
  const res = await enroll(c.env.DB, input.code, now);
  if (!res) {
    await logEvent(c.env.DB, now, { type: 'enroll_failed' });
    return c.json({ error: 'invalid_code' }, 400);
  }
  await logEvent(c.env.DB, now, { type: 'enrolled', deviceId: res.device.id });
  c.executionCtx.waitUntil(notify(c.env, `🖥️ PC **${res.device.name}** enrolled.`));
  return c.json({ deviceId: res.device.id, deviceName: res.device.name, token: res.token });
});

app.post('/heartbeat', requireDevice, async (c) => {
  const now = c.get('now');
  const device = c.get('device');
  const input = await body(c.req.raw, HeartbeatBody);
  if (!input) return c.json({ error: 'bad_request' }, 400);
  const db = c.env.DB;
  const stored = await loadSettings(db);
  const day = await ensureDays(db, stored.settings, now);

  await recordHeartbeat(db, device, day, {
    screenActive: input.screenActive,
    screenSeconds: input.screenSeconds,
    clientVersion: input.clientVersion ?? null,
  }, now);

  let session: GameSession | null = null;
  if (input.sessionId) {
    const r = await renewSession(db, device.id, input.sessionId, now);
    if (r.ok) session = r.session;
  }
  // A session the PC no longer knows about (e.g. agent restarted) is ended and refunded.
  const stray = await openSession(db, device.id);
  if (stray && stray.id !== session?.id && stray.paid_until > now) {
    await stopSession(db, stray, now, 'abandoned', `device:${device.id}`);
  }
  const closed = await closeExpired(db, now, device.id);
  const names = new Map([[device.id, device.name]]);
  c.executionCtx.waitUntil(announceClosed(c.env, closed, now, names));

  return c.json(await deviceState(db, stored, device, day, session, now));
});

app.post('/game/start', requireDevice, async (c) => {
  const now = c.get('now');
  const device = c.get('device');
  const db = c.env.DB;
  const stored = await loadSettings(db);
  const day = await ensureDays(db, stored.settings, now);

  const res = await startSession(db, device.id, day, now);
  if (!res.ok) {
    return c.json({ error: res.error, state: await deviceState(db, stored, device, day, null, now) }, 409);
  }
  if (res.created) {
    const balance = (await gameBalance(db)) + Math.floor((res.session.paid_until - now) / 1000);
    await logEvent(db, now, { type: 'game_start', deviceId: device.id, detail: { sessionId: res.session.id, balanceSeconds: balance } });
    c.executionCtx.waitUntil(
      notify(c.env, `🎮 **Gaming started** on ${device.name} at ${clock(now, stored.settings.timeZone)}. Balance ${minutes(balance)}.`),
    );
  }
  return c.json(await deviceState(db, stored, device, day, res.session, now));
});

app.post('/game/stop', requireDevice, async (c) => {
  const now = c.get('now');
  const device = c.get('device');
  const db = c.env.DB;
  const stored = await loadSettings(db);
  const day = await ensureDays(db, stored.settings, now);

  const s = await openSession(db, device.id);
  if (s) {
    const closed = await stopSession(db, s, now, 'stopped', `device:${device.id}`);
    if (closed) {
      c.executionCtx.waitUntil(announceClosed(c.env, [closed], now, new Map([[device.id, device.name]])));
    }
  }
  return c.json(await deviceState(db, stored, device, day, null, now));
});

app.get('/config', requireDevice, async (c) => {
  const { settings, version } = await loadSettings(c.env.DB);
  return c.json({
    version,
    timeZone: settings.timeZone,
    allowedApps: settings.allowedApps,
    allowedSites: settings.allowedSites,
    blockedTitles: settings.blockedTitles,
    screenshotIntervalMinutes: settings.screenshotIntervalMinutes,
    offlineBudgetMinutes: settings.offlineBudgetMinutes,
  });
});

app.post('/events', requireDevice, async (c) => {
  const now = c.get('now');
  const device = c.get('device');
  const input = await body(c.req.raw, EventsBody);
  if (!input) return c.json({ error: 'bad_request' }, 400);
  const toNotify: string[] = [];
  for (const e of input.events) {
    const detail = e.detail === undefined ? undefined : JSON.stringify(e.detail).slice(0, 2000);
    await logEvent(c.env.DB, now, { type: `pc:${e.type}`, deviceId: device.id, detail });
    if (NOTIFY_TYPES.has(e.type)) toNotify.push(`🖥️ ${device.name}: **${e.type}** ${detail ?? ''}`);
  }
  if (toNotify.length) c.executionCtx.waitUntil(notify(c.env, toNotify.join('\n')));
  return c.json({ ok: true, stored: input.events.length });
});

app.post('/screenshot', requireDevice, async (c) => {
  const now = c.get('now');
  const device = c.get('device');
  const type = (c.req.header('content-type') ?? '').split(';')[0].trim();
  if (type !== 'image/jpeg' && type !== 'image/png') return c.json({ error: 'unsupported_type' }, 415);
  const image = await c.req.arrayBuffer();
  if (image.byteLength === 0 || image.byteLength > MAX_SCREENSHOT_BYTES) return c.json({ error: 'bad_size' }, 413);

  const { settings } = await loadSettings(c.env.DB);
  const s = await openSession(c.env.DB, device.id);
  const mode = s && s.paid_until > now ? '🎮 gaming' : '🔵 school';
  const caption = `📸 ${device.name} · ${mode} · ${clock(now, settings.timeZone)}`;
  c.executionCtx.waitUntil(sendImage(c.env, image, type, caption).then(() => undefined));
  return c.json({ ok: true });
});

export default app;
