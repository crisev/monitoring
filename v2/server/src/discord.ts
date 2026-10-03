import type { Env } from './env';

/** Posts a short text message to the Discord events channel. Never throws. */
export async function notify(env: Env, text: string): Promise<void> {
  if (!env.DISCORD_TEXT_WEBHOOK) return;
  try {
    await fetch(env.DISCORD_TEXT_WEBHOOK, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ content: text.slice(0, 1900), allowed_mentions: { parse: [] } }),
    });
  } catch (e) {
    console.error('discord notify failed', e);
  }
}

/** Forwards a screenshot to the Discord images channel. */
export async function sendImage(env: Env, image: ArrayBuffer, contentType: string, caption: string): Promise<boolean> {
  if (!env.DISCORD_IMAGE_WEBHOOK) return false;
  const ext = contentType === 'image/png' ? 'png' : 'jpg';
  const form = new FormData();
  form.append('payload_json', JSON.stringify({ content: caption.slice(0, 1900), allowed_mentions: { parse: [] } }));
  form.append('files[0]', new Blob([image], { type: contentType }), `screenshot.${ext}`);
  const res = await fetch(env.DISCORD_IMAGE_WEBHOOK, { method: 'POST', body: form });
  return res.ok;
}

export function minutes(seconds: number): string {
  const sign = seconds < 0 ? '-' : '';
  const m = Math.round(Math.abs(seconds) / 60);
  return m >= 60 ? `${sign}${Math.floor(m / 60)}h${String(m % 60).padStart(2, '0')}` : `${sign}${m}m`;
}
