# Monitor v2 — Setup guide (Phase 0)

This gets the server and the parent web app running on Cloudflare. Everything here is on Cloudflare's **free plan**.

| Free-plan limit | Monitor needs (1 PC) |
|---|---|
| Workers: 100,000 requests/day | ~3,000–5,000 |
| D1: 5 GB, 5M rows read/day, 100k rows written/day | a few MB, well under the limits |
| Zero Trust (Access): 50 users | 2 (you + his mother) |

You end up with two addresses:

| Address | Who uses it | Protection |
|---|---|---|
| `https://monitor-parent.<your-subdomain>.workers.dev` | you and his mother (web app) | Cloudflare Access login (Google), only your two emails |
| `https://monitor-device.<your-subdomain>.workers.dev` | the PC | per-PC device token |

---

## 1. Tools on your computer

1. Install **Node.js LTS** from <https://nodejs.org> and **Git**.
2. Get the code and install dependencies:
   ```sh
   git clone https://github.com/crisev/monitoring.git
   cd monitoring/v2/server
   npm install
   ```

## 2. (Recommended) Try it locally first

No Cloudflare account needed. Data lives in `v2/server/.wrangler/`.

```sh
cd v2/server
npm run db:migrate:local
echo "DEV_PARENT_EMAIL=you@example.com" > .dev.vars   # local only: skips the login on localhost
npm run dev                                          # http://localhost:8787
```

Open <http://localhost:8787>, go to **PCs → Add PC**, and copy the code. Then, in another terminal, play the PC:

```sh
cd v2/tools
node fake-device.mjs --url http://localhost:8787 enroll <CODE>
node fake-device.mjs start        # refused: no game time yet
# in the web app: Today → +15
node fake-device.mjs play 2       # games for 2 minutes, heartbeat every 30s
node fake-device.mjs seed 6       # made-up app/website activity for the last 6 hours
```

Watch **Today**, **Activity** and **History** update. `npm test` runs the automated tests.

## 3. Cloudflare account and Wrangler login

1. Log in at <https://dash.cloudflare.com> (you can use your Google account).
2. Link Wrangler (the Cloudflare CLI) to your account:
   ```sh
   cd v2/server
   npx wrangler login
   ```
3. If you've never used Workers: open **Workers & Pages** in the dashboard once and pick your `workers.dev` subdomain.

## 4. Database

```sh
npx wrangler d1 create monitor
```

It prints a `database_id`. Paste it into `v2/server/wrangler.jsonc` in **all three** places that say `REPLACE_WITH_D1_DATABASE_ID`. Then create the tables:

```sh
npm run db:migrate:remote
```

## 5. First deploy

```sh
npm run deploy
```

This deploys `monitor-parent` and `monitor-device` and prints both URLs. Check the device side:

```sh
curl https://monitor-device.<your-subdomain>.workers.dev/api/health
# {"ok":true,"role":"device"}
```

The parent web app will show *"access_not_configured"* until step 7. That's on purpose: without login protection it refuses to work.

## 6. Discord webhooks (secrets)

**Create new webhooks** (Discord channel → Edit channel → Integrations → Webhooks). The old app keeps its webhook URLs in the PC's registry, where he can read them. Once v2 replaces the old app, delete the old webhooks.

Both Workers send messages, so set the secrets for both. Each command asks for the value:

```sh
npx wrangler secret put DISCORD_TEXT_WEBHOOK  --env parent
npx wrangler secret put DISCORD_TEXT_WEBHOOK  --env device
npx wrangler secret put DISCORD_IMAGE_WEBHOOK --env device
```

Secrets are stored encrypted by Cloudflare and are never in git or on the PC.

## 7. Login with Cloudflare Access (you + his mother)

1. **Zero Trust setup (first time only).**
   - Dashboard → **Zero Trust**.
   - Choose a *team name*; your login domain becomes `<team>.cloudflareaccess.com`.
   - Select the **Free** plan. It may ask for a payment method even though it costs $0.
2. **Login method.** Zero Trust → **Settings → Authentication → Login methods**.
   - **One-time PIN** (a code sent by email) works for any address without setup.
   - For **Sign in with Google**: Add new → Google, and follow the on-screen steps. You create a free OAuth client in Google Cloud Console with the redirect URL `https://<team>.cloudflareaccess.com/cdn-cgi/access/callback`.
3. **Protect the parent Worker.**
   - Dashboard → **Workers & Pages → monitor-parent → Settings → Domains & Routes**.
   - On the `workers.dev` row, enable **Cloudflare Access**, then open **Manage Cloudflare Access**.
   - Edit the policy so it **includes exactly two emails**: yours and his mother's.
   - Note the **Audience (AUD) tag** it shows.
   - If you don't see that option: Zero Trust → **Access → Applications → Add an application → Self-hosted**, domain `monitor-parent.<your-subdomain>.workers.dev`, and a policy *Allow → Include → Emails* with the two addresses. The AUD tag is on the application's overview page.
   - **Do not** put `monitor-device` behind Access: the PC can't log in with Google.
4. **Tell the Worker.** In `v2/server/wrangler.jsonc`, under `env.parent.vars`, set:
   ```jsonc
   "ACCESS_TEAM_DOMAIN": "<team>.cloudflareaccess.com",
   "ACCESS_AUD": "<the AUD tag>",
   "PARENT_EMAILS": "sevescu.cristian@gmail.com,<her email>"
   ```
   These aren't secrets, so they can be committed. The Worker checks the Access login signature and the email list on every request, as a second lock behind the Access policy.
5. `npm run deploy` again.
6. Open the parent URL in a private window. You should get the Cloudflare login, then the app. On your phones, use the browser's **Add to Home Screen** to get an app icon.

**Adding or removing a parent later** means changing both places: the Access policy *and* `PARENT_EMAILS`.

## 8. Add the PC

Web app → **PCs → Add PC** gives a code valid for 30 minutes, for one use.

Until the Windows client v2 exists (Phase 2), you can test the real deployment with the fake PC:

```sh
node v2/tools/fake-device.mjs --url https://monitor-device.<your-subdomain>.workers.dev enroll <CODE>
node v2/tools/fake-device.mjs play 3
```

Remove the fake PC afterwards (**PCs → Remove**).

## 9. (Optional) Automatic deploys from GitHub

The workflow `.github/workflows/v2-server.yml` runs the tests on every push that touches `v2/server`. It also deploys on pushes to `main` once these repository secrets exist (GitHub → Settings → Secrets and variables → Actions):

| Secret | Where to get it |
|---|---|
| `CLOUDFLARE_API_TOKEN` | Dashboard → My Profile → API Tokens → Create token → template **Edit Cloudflare Workers**, and add **Account → D1 → Edit** |
| `CLOUDFLARE_ACCOUNT_ID` | Dashboard → Workers & Pages, right-hand column |

Without them, the deploy step is skipped and only the tests run.

## Where settings live afterwards

| What | Where |
|---|---|
| Game allowance, carry-over cap, screen-time limit, offline budget, screenshot interval, allowed apps/sites, blocked titles | Web app → **Settings** |
| +game / +school time, end gaming | Web app → **Today** |
| PCs (add, re-enroll, remove) | Web app → **PCs** |
| Who can log in | Cloudflare Zero Trust Access policy + `PARENT_EMAILS` |
| Discord webhooks | `wrangler secret put …` (step 6) |
