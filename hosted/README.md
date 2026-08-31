# GuardPulse Hosted Console — anywhere access

This folder contains a self-contained web page that talks to **Firebase directly from the browser** — no laptop agent required. Sign in with your parent account from any device and control your children's devices just like the phone app.

## How to deploy (free, ~5 minutes)

### 1. Cloudflare Pages (recommended)

```bash
# Install Wrangler (once)
npm install -g wrangler

# Log in to your Cloudflare account (opens a browser)
wrangler login

# Deploy this folder
wrangler pages deploy --project-name guardpulse-console
```

After the first deploy, Wrangler prints a `*.pages.dev` URL. You can optionally configure a custom domain in the Cloudflare Dashboard.

### 2. Any static host (GitHub Pages, Netlify, Vercel, …)

Upload the `index.html` file to any static file host. The page has zero external dependencies — it's a single file.

## What it does

- **Sign in** with your GuardPulse parent email + password (Identity Toolkit, same as the phone app).
- **Device list** — every device paired under your account, with online/offline status, enforcement mode, health, and last-seen.
- **Device console** — full control of any device, exactly like the laptop dashboard:
  - Apps table (every installed app, merged with your policy rules; Allow/Lock switches, daily limits, Reset today)
  - Modes (create/rename/delete, activate/deactivate, per-app rules editor with switches)
  - Safe Mode, daily budget, allowlist, schedule, content filter, blocked websites, device PIN
  - Pending unlock requests (approve One visit / 15 / 30 min / Deny, live)
  - Protection events
- **Live updates** — state refreshes automatically via Firebase RTDB streaming (SSE). If a stream disconnects, it falls back to 8s polling. Always current.
- **Waiting-for-device gating** — after a write, the UI shows a banner until the device acks the exact revision.
- **Works anywhere** — laptop can be off. The page talks directly to Firebase.

## Security

- The Firebase web API key is loaded from a gitignored local file (`firebase-config.js`, see `firebase-config.example.js`) at deploy time; the committed page only ships a placeholder. The key is not a secret — every database read/write is still gated by your Firebase security rules and requires your parent email+password — but keeping it out of the repo avoids hardcoding it in public history.
- The refresh token is stored in your browser's `localStorage` (same as the phone app). Sign out clears it.
- The page is served over HTTPS by Cloudflare Pages (or your host). Name resolution is through Cloudflare's global CDN.
- Keep the URL private and use a strong password. Consider adding [Cloudflare Access](https://www.cloudflare.com/zero-trust/access/) (free for up to 50 users) for an extra login layer in front of the page.

## Files

| File | Purpose |
|---|---|
| `index.html` | The single-file web app. Everything inlined: CSS, icons, all logic. |
| `firebase-config.example.js` | Template for the gitignored `firebase-config.js` (holds the real Firebase API key for deploys). |
| `harness.js` | Functional test suite (31 checks) against a mock Firebase. Run with `node harness.js`. |
| `e2e-live.js` | Live test against the REAL Firebase: signs in, reads devices, does one small reversible write (blocks one allowed app on the first paired device, waits for the device to ack, restores it). Requires `GP_EMAIL` and `GP_PASSWORD` env vars. Run: `GP_EMAIL=you@example.com GP_PASSWORD=... node e2e-live.js`. |
| `README.md` | This file. |
| `deploy.ps1` | PowerShell script that runs `wrangler pages deploy` (PowerShell). |

## Development

The page is a single HTML file. To make changes:

1. Edit `index.html`.
2. Run `node harness.js` to verify the mock-Firebase tests still pass.
3. Run `node --check <(sed -n '/<script>/,/<\/script>/p' index.html)` to syntax-check the inline script.
4. Deploy.

(No build step, no bundler, no npm dependencies.)