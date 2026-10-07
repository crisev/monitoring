import { exports as workerExports } from 'cloudflare:workers';
import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { normalizeTeamDomain } from '../src/auth';
import { resetDb } from './helpers';

// Matches the bindings in vitest.config.ts.
const TEAM = 'test.cloudflareaccess.com';
const AUD = 'test-aud';
const worker = (workerExports as unknown as { default: Fetcher }).default;

let privateKey: CryptoKey;
const realFetch = globalThis.fetch;

beforeAll(async () => {
  const pair = await generateKeyPair('RS256', { extractable: true });
  privateKey = pair.privateKey;
  const jwk = { ...(await exportJWK(pair.publicKey)), kid: 'k1', alg: 'RS256', use: 'sig' };
  // The Worker runs in the same isolate as the tests, so this stands in for Cloudflare's certs endpoint.
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const url = input instanceof Request ? input.url : String(input);
    if (url === `https://${TEAM}/cdn-cgi/access/certs`) {
      return new Response(JSON.stringify({ keys: [jwk] }), { headers: { 'content-type': 'application/json' } });
    }
    return realFetch(input, init);
  });
});
afterAll(() => vi.restoreAllMocks());
beforeEach(resetDb);

function token(claims: { email?: string; iss?: string; aud?: string; exp?: string }) {
  return new SignJWT({ email: claims.email ?? 'parent@example.com' })
    .setProtectedHeader({ alg: 'RS256', kid: 'k1' })
    .setIssuer(claims.iss ?? `https://${TEAM}`)
    .setAudience(claims.aud ?? AUD)
    .setIssuedAt()
    .setExpirationTime(claims.exp ?? '1h')
    .sign(privateKey);
}

async function today(jwt?: string) {
  const res = await worker.fetch(
    new Request('https://monitor-parent.example.workers.dev/api/parent/today', {
      headers: jwt ? { 'cf-access-jwt-assertion': jwt } : {},
    }),
  );
  return { status: res.status, body: (await res.json()) as Record<string, unknown> };
}

describe('Cloudflare Access login', () => {
  it('accepts a valid token for an allowed parent', async () => {
    const r = await today(await token({}));
    expect(r.status).toBe(200);
  });

  it('explains a missing token', async () => {
    expect(await today()).toMatchObject({ status: 401, body: { reason: 'no_token' } });
  });

  it('explains a wrong AUD', async () => {
    expect(await today(await token({ aud: 'other-app' }))).toMatchObject({ status: 401, body: { reason: 'audience_mismatch' } });
  });

  it('explains a wrong team domain and names the right one', async () => {
    const r = await today(await token({ iss: 'https://other-team.cloudflareaccess.com' }));
    expect(r).toMatchObject({ status: 401, body: { reason: 'issuer_mismatch' } });
    expect(String(r.body.hint)).toContain('other-team.cloudflareaccess.com');
  });

  it('refuses a valid login that is not in PARENT_EMAILS', async () => {
    const r = await today(await token({ email: 'kid@gmail.com' }));
    expect(r).toMatchObject({ status: 403, body: { error: 'forbidden' } });
  });

  it('accepts the team domain written as a URL', () => {
    expect(normalizeTeamDomain('https://Small-Union-8ae.cloudflareaccess.com/cdn-cgi/access/certs')).toBe('small-union-8ae.cloudflareaccess.com');
    expect(normalizeTeamDomain(' small-union-8ae.cloudflareaccess.com ')).toBe('small-union-8ae.cloudflareaccess.com');
  });
});
