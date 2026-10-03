# Monitor v2

A rewrite of the monitor where **the server, not the PC, owns the time**. See [`../docs/REDESIGN_PLAN.md`](../docs/REDESIGN_PLAN.md) for the design and [`docs/SETUP.md`](docs/SETUP.md) to set it up.

The original app at the repository root is not changed by v2.

| Folder | What | Status |
|---|---|---|
| `server/` | Cloudflare Worker (TypeScript, Hono, D1): time ledger, game leases, device + parent API, Discord proxy, cron | ✅ Phase 1 |
| `server/public/` | Parent web app (plain HTML/CSS/JS, installable on a phone) | ✅ Phase 1 |
| `tools/fake-device.mjs` | Simulates a PC against the server | ✅ |
| `client/` | Windows service + tray agent (C#) | Phase 2 |

## Developing the server

```sh
cd server
npm install
npm run db:migrate:local
echo "DEV_PARENT_EMAIL=you@example.com" > .dev.vars
npm run dev          # http://localhost:8787
npm test             # runs in the real Workers runtime, with a local D1
npm run typecheck
```

## How time is counted (short version)

- **Game time** is a ledger in D1. Every grant, daily allowance, carry-over cap and played session is a row; the balance is their sum.
  - Each morning (server time, Europe/Bucharest): `balance = min(cap, balance + daily allowance)`.
- **Gaming** happens in prepaid 90-second leases:
  - The PC renews every 30 s, and only the extension is charged.
  - Stopping refunds the unused part.
  - If renewals stop (offline, asleep, tampering), the session ends when the lease runs out. That time was already paid, so the PC can never use more than the balance.
- **Screen time**: the PC reports the seconds the screen was in use (measured on a monotonic clock). The server never counts more than the time that actually passed on its own clock. Extra school time is for today only.
- **The PC's clock is never used** for any of this.

## Device API (for the Windows client)

All calls send `Authorization: Bearer <device token>`, except `enroll`.

| Call | Body | Returns |
|---|---|---|
| `POST /api/device/enroll` | `{ code }` | `{ deviceId, deviceName, token }` |
| `POST /api/device/heartbeat` (every 30 s) | `{ sessionId?, screenActive, screenSeconds, clientVersion? }` | state |
| `POST /api/device/game/start` | – | state, or 409 `{ error: "no_balance", state }` |
| `POST /api/device/game/stop` | `{ sessionId? }` | state |
| `GET /api/device/config` | – | `{ version, allowedApps, allowedSites, blockedTitles, screenshotIntervalMinutes, offlineBudgetMinutes, timeZone }` |
| `POST /api/device/events` | `{ events: [{ type, detail? }] }` | `{ ok }` |
| `POST /api/device/screenshot` | raw `image/jpeg` or `image/png` (≤ 8 MB) | `{ ok }`, forwarded to Discord |

**State:**

```jsonc
{
  "serverTime": 1791037907375, "day": "2026-10-05", "deviceId": "…",
  "mode": "gaming",                                  // or "school"
  "session": { "id": "…", "leaseRemainingSeconds": 88 },   // null in school mode
  "game":   { "balanceSeconds": 2640, "usedTodaySeconds": 960 },
  "screen": { "limitSeconds": 10800, "usedSeconds": 4200, "remainingSeconds": 6600, "extraSeconds": 0 }, // null limit = none
  "configVersion": 3,                                // re-fetch /config when it changes
  "timing": { "heartbeatSeconds": 30, "leaseSeconds": 90, "offlineBudgetSeconds": 3600 }
}
```

**What the client must do:**
- Go to School mode as soon as `leaseRemainingSeconds` runs out on its **monotonic** timer without a successful renewal.
- Shut down when `screen.remainingSeconds` reaches 0, after the 10/5/1-minute warnings.
- While offline, keep counting both down and enforce School mode.
