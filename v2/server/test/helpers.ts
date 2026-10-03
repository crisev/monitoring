import { env } from 'cloudflare:workers';
import type { Device } from '../src/devices';
import { enroll, issueEnrollCode } from '../src/devices';
import type { StoredSettings } from '../src/settings';
import { loadSettings, saveSettings, SettingsSchema } from '../src/settings';

/** 2026-10-05 11:00 in Bucharest (UTC+3). */
export const T0 = Date.UTC(2026, 9, 5, 8, 0, 0);
export const SEC = 1000;
export const MIN = 60 * SEC;
export const DAY = 24 * 60 * MIN;

export const db = () => env.DB;

export async function resetDb(): Promise<void> {
  for (const t of ['ledger', 'events', 'game_sessions', 'devices', 'days', 'settings']) {
    await env.DB.exec(`DELETE FROM ${t}`);
  }
}

export async function useSettings(partial: Record<string, unknown>): Promise<StoredSettings> {
  await saveSettings(env.DB, SettingsSchema.parse(partial), 'test', T0);
  return loadSettings(env.DB);
}

export async function makeDevice(name = 'PC'): Promise<Device> {
  const issued = await issueEnrollCode(env.DB, T0, { name });
  const res = await enroll(env.DB, issued!.code, T0);
  return res!.device;
}
