import type { Context, MiddlewareHandler } from 'hono';
import { createRemoteJWKSet, decodeJwt, jwtVerify } from 'jose';
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

/** Accepts "team.cloudflareaccess.com", "https://team.cloudflareaccess.com" or the full certs/JWKS URL. */
export function normalizeTeamDomain(value: string): string {
  return value.trim().replace(/^https?:\/\//i, '').replace(/\/.*$/, '').toLowerCase();
}

/** Explains a rejected Access token so a misconfiguration can be fixed (the token itself is never echoed). */
function rejectionHint(e: unknown, token: string, expectedIss: string): { reason: string; hint: string } {
  const code = (e as { code?: string }).code ?? 'unknown';
  const claim = (e as { claim?: string }).claim;
  let tokenIss: unknown;
  try {
    tokenIss = decodeJwt(token).iss;
  } catch {
    return { reason: 'malformed_token', hint: 'The login token is not a valid Cloudflare Access token.' };
  }
  if (claim === 'iss') {
    const strip = (v: unknown) => String(v).replace(/^https:\/\//, '');
    return {
      reason: 'issuer_mismatch',
      hint: `ACCESS_TEAM_DOMAIN does not match the login (expected ${strip(tokenIss)}, configured ${strip(expectedIss)}).`,
    };
  }
  if (claim === 'aud') {
    return { reason: 'audience_mismatch', hint: 'ACCESS_AUD does not match the AUD tag of the Access application protecting this Worker.' };
  }
  if (code.startsWith('ERR_JWKS') || code === 'ERR_JOSE_GENERIC') {
    return { reason: 'keys_unavailable', hint: 'Could not load the signing keys from ACCESS_TEAM_DOMAIN; check that value.' };
  }
  if (code === 'ERR_JWT_EXPIRED') return { reason: 'expired', hint: 'The login has expired; reload the page.' };
  return { reason: code, hint: 'The login token could not be verified.' };
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
  const teamDomain = normalizeTeamDomain(env.ACCESS_TEAM_DOMAIN ?? '');
  const aud = (env.ACCESS_AUD ?? '').trim();
  if (!teamDomain || !aud || teamDomain.includes('replace') || aud.includes('REPLACE')) {
    return c.json({ error: 'access_not_configured' }, 500);
  }
  const token = c.req.header('cf-access-jwt-assertion');
  if (!token) {
    return c.json(
      {
        error: 'unauthenticated',
        reason: 'no_token',
        hint: 'Cloudflare Access is not in front of this request. In the Worker\'s Access tab the scope must be "All traffic".',
      },
      401,
    );
  }

  const issuer = `https://${teamDomain}`;
  let email: string | undefined;
  try {
    const { payload } = await jwtVerify(token, jwksFor(teamDomain), { issuer, audience: aud });
    email = typeof payload.email === 'string' ? payload.email.toLowerCase() : undefined;
  } catch (e) {
    const why = rejectionHint(e, token, issuer);
    console.warn('Access token rejected', why.reason, why.hint);
    return c.json({ error: 'unauthenticated', ...why }, 401);
  }
  const allowed = (env.PARENT_EMAILS ?? '')
    .split(',')
    .map((e) => e.trim().toLowerCase())
    .filter(Boolean);
  if (!email || !allowed.includes(email)) {
    return c.json({ error: 'forbidden', hint: `${email ?? 'This login'} is not in PARENT_EMAILS.` }, 403);
  }

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
