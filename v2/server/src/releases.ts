import type { Device } from './devices';
import { notify } from './discord';
import type { Env } from './env';
import { logEvent } from './events';

/** Where the Windows client is released (public repository: no login needed to download). */
export const DEFAULT_GITHUB_REPO = 'crisev/monitoring';
export const RELEASE_TAG_PREFIX = 'client-v';
/** How often the cron asks GitHub for new releases. */
export const RELEASE_CHECK_MS = 60 * 60 * 1000;

export interface Release {
  version: string;
  tag: string;
  url: string;
  sha256: string;
  size: number;
  published_at: number;
  notes: string | null;
  found_at: number;
}

/** What the PC needs to update itself (part of the device state). */
export interface UpdateOffer {
  /** Changes when a parent clicks Update again, so a PC retries a version that failed before. */
  id: string;
  version: string;
  url: string;
  sha256: string;
  size: number;
}

/** "0.1.12" -> [0, 1, 12]; null if not a plain dotted version. */
export function parseVersion(v: string | null | undefined): number[] | null {
  if (!v) return null;
  const m = /^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$/.exec(v.trim().split('+')[0]);
  return m ? m.slice(1).filter((x) => x !== undefined).map(Number) : null;
}

/** Negative if a < b, 0 if equal, positive if a > b. Unparseable versions sort first. */
export function compareVersions(a: string | null | undefined, b: string | null | undefined): number {
  const pa = parseVersion(a);
  const pb = parseVersion(b);
  if (!pa || !pb) return (pa ? 1 : 0) - (pb ? 1 : 0);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

export async function listReleases(db: D1Database): Promise<Release[]> {
  const rows = (await db.prepare('SELECT * FROM releases').all<Release>()).results;
  return rows.sort((a, b) => compareVersions(b.version, a.version));
}

export async function getRelease(db: D1Database, version: string): Promise<Release | null> {
  return db.prepare('SELECT * FROM releases WHERE version = ?1').bind(version).first<Release>();
}

export async function getMeta(db: D1Database, key: string): Promise<string | null> {
  return (await db.prepare('SELECT value FROM meta WHERE key = ?1').bind(key).first<{ value: string }>())?.value ?? null;
}

async function setMeta(db: D1Database, key: string, value: string): Promise<void> {
  await db
    .prepare('INSERT INTO meta (key, value) VALUES (?1, ?2) ON CONFLICT (key) DO UPDATE SET value = excluded.value')
    .bind(key, value)
    .run();
}

export interface ReleaseCheck {
  at: number | null;
  error: string | null;
}

export async function lastReleaseCheck(db: D1Database): Promise<ReleaseCheck> {
  const at = await getMeta(db, 'release_check_at');
  return { at: at ? Number(at) : null, error: await getMeta(db, 'release_check_error') };
}

interface GitHubAsset {
  name: string;
  size: number;
  browser_download_url: string;
}

interface GitHubRelease {
  tag_name: string;
  draft: boolean;
  prerelease: boolean;
  published_at: string | null;
  body: string | null;
  assets: GitHubAsset[];
}

/**
 * Looks for new client releases on GitHub and stores them. Unless forced, does nothing if GitHub was
 * asked less than an hour ago. Returns the versions added. Never throws: errors are stored for the PCs page.
 */
export async function checkReleases(
  env: Env,
  now: number,
  opts: { force?: boolean; fetcher?: typeof fetch } = {},
): Promise<{ checked: boolean; added: string[]; error: string | null }> {
  const db = env.DB;
  const last = await lastReleaseCheck(db);
  if (!opts.force && last.at !== null && now - last.at < RELEASE_CHECK_MS) return { checked: false, added: [], error: null };
  await setMeta(db, 'release_check_at', String(now));

  const fetcher = opts.fetcher ?? fetch;
  const repo = env.GITHUB_REPO?.trim() || DEFAULT_GITHUB_REPO;
  const headers: Record<string, string> = {
    accept: 'application/vnd.github+json',
    'user-agent': 'monitor-v2-server',
    'x-github-api-version': '2022-11-28',
  };
  if (env.GITHUB_TOKEN) headers.authorization = `Bearer ${env.GITHUB_TOKEN}`;

  const added: string[] = [];
  let error: string | null = null;
  try {
    const res = await fetcher(`https://api.github.com/repos/${repo}/releases?per_page=30`, { headers });
    if (!res.ok) throw new Error(`GitHub answered ${res.status}${res.status === 403 ? ' (rate limit? set the GITHUB_TOKEN secret)' : ''}`);
    const releases = (await res.json()) as GitHubRelease[];
    const known = new Set((await listReleases(db)).map((r) => r.version));

    for (const r of releases) {
      if (r.draft || r.prerelease || !r.tag_name.startsWith(RELEASE_TAG_PREFIX)) continue;
      const version = r.tag_name.slice(RELEASE_TAG_PREFIX.length);
      if (!parseVersion(version) || known.has(version)) continue;
      const zip = r.assets.find((a) => a.name === `MonitorV2-${version}.zip`);
      const shaAsset = r.assets.find((a) => a.name === `MonitorV2-${version}.zip.sha256`);
      if (!zip || !shaAsset) continue; // still uploading, or not a client package

      const shaRes = await fetcher(shaAsset.browser_download_url, { headers: { 'user-agent': headers['user-agent'] } });
      if (!shaRes.ok) throw new Error(`Could not download ${shaAsset.name} (${shaRes.status})`);
      const sha256 = /\b([0-9a-fA-F]{64})\b/.exec(await shaRes.text())?.[1]?.toLowerCase();
      if (!sha256) throw new Error(`${shaAsset.name} does not contain a SHA-256`);

      await db
        .prepare(
          `INSERT OR IGNORE INTO releases (version, tag, url, sha256, size, published_at, notes, found_at)
           VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)`,
        )
        .bind(version, r.tag_name, zip.browser_download_url, sha256, zip.size,
          r.published_at ? Date.parse(r.published_at) : now, (r.body ?? '').slice(0, 2000) || null, now)
        .run();
      added.push(version);
    }
  } catch (e) {
    error = e instanceof Error ? e.message : String(e);
    console.error('release check failed', error);
  }

  if (error) await setMeta(db, 'release_check_error', error);
  else await db.prepare("DELETE FROM meta WHERE key = 'release_check_error'").run();

  if (added.length) {
    const newest = added.sort((a, b) => compareVersions(b, a))[0];
    await logEvent(db, now, { type: 'release_found', detail: { versions: added } });
    await notify(env, `📦 New PC version **${newest}** is available. Install it from the web app: PCs → Update.`);
  }
  return { checked: true, added, error };
}

/** The update this PC should install now, if a parent asked for one and it is not installed yet. */
export async function updateOffer(db: D1Database, device: Device): Promise<UpdateOffer | null> {
  const target = device.target_version;
  if (!target || device.update_status === 'failed' || device.update_status === 'done') return null;
  if (compareVersions(target, device.client_version) <= 0) return null;
  const r = await getRelease(db, target);
  if (!r) return null;
  return { id: `${r.version}@${device.update_requested_at ?? 0}`, version: r.version, url: r.url, sha256: r.sha256, size: r.size };
}

/** A parent asks a PC to install a version (or cancels with null). */
export async function requestUpdate(db: D1Database, deviceId: string, version: string | null, now: number): Promise<boolean> {
  const res = await db
    .prepare(
      `UPDATE devices SET target_version = ?1, update_requested_at = ?2,
         update_status = CASE WHEN ?1 IS NULL THEN NULL ELSE 'requested' END, update_message = NULL, update_at = ?2
       WHERE id = ?3 AND revoked = 0`,
    )
    .bind(version, now, deviceId)
    .run();
  return res.meta.changes > 0;
}

/**
 * Applies an update event reported by the PC. Returns a Discord message for it, if any.
 *   update_started { to }            downloaded and verified, installing now
 *   updated        { from, to }      the new version reached the server
 *   update_failed  { to, reason }    download, check or install failed (rolled back if needed)
 */
export async function applyUpdateEvent(
  db: D1Database,
  device: Device,
  type: string,
  detail: unknown,
  now: number,
): Promise<string | null> {
  const d = (detail && typeof detail === 'object' ? detail : {}) as Record<string, unknown>;
  const to = typeof d.to === 'string' ? d.to.slice(0, 40) : '?';
  const set = (status: string, message: string | null) =>
    db
      .prepare('UPDATE devices SET update_status = ?1, update_message = ?2, update_at = ?3 WHERE id = ?4')
      .bind(status, message, now, device.id)
      .run();

  switch (type) {
    case 'update_started':
      await set('installing', null);
      return null;
    case 'updated':
      await set('done', null);
      return `✅ ${device.name} updated to **${to}**${typeof d.from === 'string' ? ` (from ${d.from.slice(0, 40)})` : ''}.`;
    case 'update_failed': {
      const reason = typeof d.reason === 'string' ? d.reason.slice(0, 300) : 'unknown error';
      await set('failed', reason);
      return `❌ ${device.name}: update to **${to}** failed: ${reason}`;
    }
    default:
      return null;
  }
}
