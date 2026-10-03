export interface Device {
  id: string;
  name: string;
  token_hash: string | null;
  enroll_code_hash: string | null;
  enroll_expires_at: number | null;
  created_at: number;
  last_heartbeat_at: number | null;
  last_screen_active: number;
  silent_alerted_at: number | null;
  client_version: string | null;
  revoked: number;
}

const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // no 0/O/1/I
const CODE_LENGTH = 10; // 50 bits
export const ENROLL_CODE_TTL_MS = 30 * 60 * 1000;

export async function sha256Hex(text: string): Promise<string> {
  const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function randomCode(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
  return [...bytes].map((b) => CODE_ALPHABET[b % CODE_ALPHABET.length]).join('');
}

function randomToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function normalizeCode(code: string): string {
  return code.toUpperCase().replace(/[^A-Z0-9]/g, '');
}

/** Creates a device (or resets an existing one) and returns a one-time enrollment code. */
export async function issueEnrollCode(
  db: D1Database,
  now: number,
  opts: { name: string } | { deviceId: string },
): Promise<{ deviceId: string; code: string; expiresAt: number } | null> {
  const code = randomCode();
  const codeHash = await sha256Hex(code);
  const expiresAt = now + ENROLL_CODE_TTL_MS;
  if ('name' in opts) {
    const deviceId = crypto.randomUUID();
    await db
      .prepare(
        `INSERT INTO devices (id, name, enroll_code_hash, enroll_expires_at, created_at) VALUES (?1, ?2, ?3, ?4, ?5)`,
      )
      .bind(deviceId, opts.name, codeHash, expiresAt, now)
      .run();
    return { deviceId, code, expiresAt };
  }
  // Re-enrolling invalidates the old token immediately.
  const res = await db
    .prepare(
      `UPDATE devices SET token_hash = NULL, enroll_code_hash = ?1, enroll_expires_at = ?2, revoked = 0 WHERE id = ?3`,
    )
    .bind(codeHash, expiresAt, opts.deviceId)
    .run();
  return res.meta.changes ? { deviceId: opts.deviceId, code, expiresAt } : null;
}

/** Exchanges a valid enrollment code for a device token. The code can be used once. */
export async function enroll(db: D1Database, code: string, now: number): Promise<{ device: Device; token: string } | null> {
  const codeHash = await sha256Hex(normalizeCode(code));
  const token = randomToken();
  const device = await db
    .prepare(
      `UPDATE devices SET token_hash = ?1, enroll_code_hash = NULL, enroll_expires_at = NULL
       WHERE enroll_code_hash = ?2 AND enroll_expires_at > ?3 AND revoked = 0
       RETURNING *`,
    )
    .bind(await sha256Hex(token), codeHash, now)
    .first<Device>();
  return device ? { device, token } : null;
}

export async function deviceByToken(db: D1Database, token: string): Promise<Device | null> {
  if (!token) return null;
  return db
    .prepare('SELECT * FROM devices WHERE token_hash = ?1 AND revoked = 0')
    .bind(await sha256Hex(token))
    .first<Device>();
}

export async function revokeDevice(db: D1Database, deviceId: string): Promise<boolean> {
  const res = await db
    .prepare('UPDATE devices SET revoked = 1, token_hash = NULL, enroll_code_hash = NULL WHERE id = ?1')
    .bind(deviceId)
    .run();
  return res.meta.changes > 0;
}

export async function listDevices(db: D1Database): Promise<Device[]> {
  return (await db.prepare('SELECT * FROM devices ORDER BY created_at').all<Device>()).results;
}

/** Public view of a device (no hashes). */
export function deviceView(d: Device, now: number) {
  return {
    id: d.id,
    name: d.name,
    createdAt: d.created_at,
    lastHeartbeatAt: d.last_heartbeat_at,
    online: d.last_heartbeat_at !== null && now - d.last_heartbeat_at < 90_000,
    screenActive: d.last_screen_active === 1,
    clientVersion: d.client_version,
    enrolled: d.token_hash !== null,
    enrollPending: d.enroll_code_hash !== null && (d.enroll_expires_at ?? 0) > now,
    revoked: d.revoked === 1,
  };
}
