import { z } from 'zod';
import { isValidTimeZone } from './time';

const name = z.string().trim().min(1).max(200);

export const SettingsSchema = z.object({
  timeZone: z.string().refine(isValidTimeZone, 'Unknown time zone').default('Europe/Bucharest'),
  /** Game minutes added at the start of every day. */
  dailyGameMinutes: z.number().int().min(0).max(1440).default(0),
  /** Carry-over cap: at the start of each day, balance = min(cap, balance + daily allowance). */
  maxGameBalanceMinutes: z.number().int().min(0).max(100_000).default(300),
  /** Daily screen-time limit (School + Gaming). 0 = no limit. */
  dailyScreenMinutes: z.number().int().min(0).max(1440).default(180),
  /** How long the PC may stay on (School mode only) without reaching the server. */
  offlineBudgetMinutes: z.number().int().min(0).max(1440).default(60),
  /** 0 = screenshots off. */
  screenshotIntervalMinutes: z.number().int().min(0).max(1440).default(5),
  /** Executable names (without .exe) allowed in School mode, on top of the built-in Windows list. */
  allowedApps: z.array(name).max(500).default([
    'msedge', 'WINWORD', 'Code', 'codeblocks', 'notepad', 'CalculatorApp',
    'cmd', 'powershell', 'pwsh', 'WindowsTerminal', 'wt',
  ]),
  /** Domains allowed in Edge in School mode (subdomains included). */
  allowedSites: z.array(name).max(500).default([
    'wikipedia.org', 'nerdvana.ro', 'pbinfo.ro', 'nerdarena.ro', 'infoarena.ro',
    'codeforces.com', 'kilonova.ro', 'google.com',
  ]),
  /** Window-title fragments that are blocked even on allowed sites (e.g. Google Doodle games). */
  blockedTitles: z.array(name).max(500).default([
    'YouTube', 'Agar.io', 'diep.io', 'Twitch', 'Poki', 'Infinite Craft', 'Play Snake', 'Play PAC-MAN',
    'Google Doodles', 'Play Solitaire', 'Play Minesweeper', 'Play Tic-tac-toe',
  ]),
});

export type Settings = z.infer<typeof SettingsSchema>;

export interface StoredSettings {
  settings: Settings;
  version: number;
  updatedAt: number | null;
  updatedBy: string | null;
}

export async function loadSettings(db: D1Database): Promise<StoredSettings> {
  const row = await db
    .prepare('SELECT json, version, updated_at, updated_by FROM settings WHERE id = 1')
    .first<{ json: string; version: number; updated_at: number; updated_by: string }>();
  if (!row) {
    return { settings: SettingsSchema.parse({}), version: 0, updatedAt: null, updatedBy: null };
  }
  // Re-parse so settings added in later versions get their defaults.
  const parsed = SettingsSchema.safeParse(JSON.parse(row.json));
  return {
    settings: parsed.success ? parsed.data : SettingsSchema.parse({}),
    version: row.version,
    updatedAt: row.updated_at,
    updatedBy: row.updated_by,
  };
}

export async function saveSettings(db: D1Database, settings: Settings, by: string, now: number): Promise<number> {
  const row = await db
    .prepare(
      `INSERT INTO settings (id, json, version, updated_at, updated_by) VALUES (1, ?1, 1, ?2, ?3)
       ON CONFLICT (id) DO UPDATE SET json = excluded.json, version = settings.version + 1,
         updated_at = excluded.updated_at, updated_by = excluded.updated_by
       RETURNING version`,
    )
    .bind(JSON.stringify(settings), now, by)
    .first<{ version: number }>();
  return row!.version;
}
