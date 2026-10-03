export interface Env {
  DB: D1Database;
  ASSETS?: Fetcher;
  /** Which routes this deployment serves: 'parent', 'device', or 'all' (local dev / tests). */
  ROLE: 'parent' | 'device' | 'all';
  /** e.g. "myteam.cloudflareaccess.com" */
  ACCESS_TEAM_DOMAIN?: string;
  /** Audience tag of the Cloudflare Access application. */
  ACCESS_AUD?: string;
  /** Comma-separated list of parent emails allowed in (checked in addition to the Access policy). */
  PARENT_EMAILS?: string;
  /** Local development only (.dev.vars): treat requests to localhost as this parent. */
  DEV_PARENT_EMAIL?: string;
  DISCORD_TEXT_WEBHOOK?: string;
  DISCORD_IMAGE_WEBHOOK?: string;
}

/** Timing contract shared with the PC client. */
export const HEARTBEAT_SECONDS = 30;
/** Length of a prepaid game lease. The PC returns to School mode if it is not renewed in time. */
export const LEASE_SECONDS = 90;
/** A PC that was active and has not called home for this long triggers a "PC silent" alert. */
export const SILENT_ALERT_MINUTES = 15;
