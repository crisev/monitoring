import { Hono } from 'hono';
import { HTTPException } from 'hono/http-exception';
import type { AppVars } from './auth';
import { runCron } from './cron';
import type { Env } from './env';
import deviceRoutes from './routes/device';
import parentRoutes from './routes/parent';

const app = new Hono<{ Bindings: Env; Variables: AppVars }>();

app.use('*', async (c, next) => {
  c.set('now', Date.now());
  await next();
  c.header('cache-control', 'no-store');
});

app.get('/api/health', (c) => c.json({ ok: true, role: c.env.ROLE }));

// Each deployment only serves its own half of the API.
app.use('/api/device/*', async (c, next) => (c.env.ROLE === 'parent' ? c.json({ error: 'not_found' }, 404) : next()));
app.use('/api/parent/*', async (c, next) => (c.env.ROLE === 'device' ? c.json({ error: 'not_found' }, 404) : next()));
app.route('/api/device', deviceRoutes);
app.route('/api/parent', parentRoutes);
app.all('/api/*', (c) => c.json({ error: 'not_found' }, 404));

// Everything else is the parent web app (static files), only on the parent deployment.
app.all('*', (c) => (c.env.ASSETS && c.env.ROLE !== 'device' ? c.env.ASSETS.fetch(c.req.raw) : c.json({ error: 'not_found' }, 404)));

app.onError((err, c) => {
  if (err instanceof HTTPException) return err.getResponse();
  console.error(err);
  return c.json({ error: 'internal_error' }, 500);
});

export default {
  fetch: app.fetch,
  async scheduled(_controller: ScheduledController, env: Env, ctx: ExecutionContext) {
    ctx.waitUntil(runCron(env, Date.now()));
  },
} satisfies ExportedHandler<Env>;
