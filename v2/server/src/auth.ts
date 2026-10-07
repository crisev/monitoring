import type { Context, MiddlewareHandler } from 'hono';
import { createRemoteJWKSet, jwtVerify } from 'jose';
import type { Device } from './devices';
import { deviceByToken } from './devices';
import type { Env } from './env';

export type AppVars = { parentEmail: string; device: Device; now: number };
export type AppContext = Context<{ Bindings: Env; Variables: AppVars }>;

const jwksCache = new Map<string, ReturnType<typeof createRemoteJWKSet>>();

function jwksFor(teamDomain: string) {
  let jwks = jwksCache.get(teamDomain);
  if (!jwks) {
    jwks = createRemoteJWKSet(new URL(`https://${teamDomain}/cdn-cgi/access/certs`));
    jwksCache.set(teamDomain, jwks);
  }
  return jwks;
}

function isLocalhost(url: string): boolean {
  const host = new URL(url).hostname;
  return host === 'localhost' || host === '127.0.0.1' || host === '[::1]';
}

/**
 * Parent routes: require a valid Cloudflare Access JWT whose email is in PARENT_EMAILS.
 * Fails closed if Access is not configured.
 */
export const requireParent: MiddlewareHandler<{ Bindings: Env; Variables: AppVars }> = async (c, next) => {
  const env = c.env;
  if (env.DEV_PARENT_EMAIL && isLocalhost(c.req.url)) {
    c.set('parentEmail', env.DEV_PARENT_EMAIL);
    return next();
  }
  const teamDomain = env.ACCESS_TEAM_DOMAIN;
  const aud = env.ACCESS_AUD;
  if (!teamDomain || !aud || teamDomain.includes('REPLACE') || aud.includes('REPLACE')) {
    return c.json({ error: 'access_not_configured' }, 500);
  }
  const token = c.req.header('cf-access-jwt-assertion');
  if (!token) return c.json({ error: 'unauthenticated' }, 401);

  let email: string | undefined;
  try {
    const { payload } = await jwtVerify(token, jwksFor(teamDomain), {
      issuer: `https://${teamDomain}`,
      audience: aud,
    });
    email = typeof payload.email === 'string' ? payload.email.toLowerCase() : undefined;
  } catch {
    return c.json({ error: 'unauthenticated' }, 401);
  }
  const allowed = (env.PARENT_EMAILS ?? '')
    .split(',')
    .map((e) => e.trim().toLowerCase())
    .filter(Boolean);
  if (!email || !allowed.includes(email)) return c.json({ error: 'forbidden' }, 403);

  c.set('parentEmail', email);
  return next();
};

/** Device routes: `Authorization: Bearer <device token>`. */
export const requireDevice: MiddlewareHandler<{ Bindings: Env; Variables: AppVars }> = async (c, next) => {
  const auth = c.req.header('authorization') ?? '';
  const token = auth.startsWith('Bearer ') ? auth.slice(7).trim() : '';
  const device = await deviceByToken(c.env.DB, token);
  if (!device) return c.json({ error: 'unauthenticated' }, 401);
  c.set('device', device);
  return next();
};
