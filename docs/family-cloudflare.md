# The family instance behind Cloudflare — tunnel + Access

How the family site runs: the same ShelfAware publish that has served the tailnet since
July stays exactly where it is (`C:\Users\Jorcu\ShelfAware-server`, port 5179, started
at boot by the "ShelfAware Server" scheduled task), and a **Cloudflare Tunnel** makes it
reachable at **https://family.shelfaware.net** from anywhere — behind **Cloudflare
Access**, so only allow-listed emails ever see the login page. Set up 2026-08-12,
verified end to end the same night.

```
family's browser ──HTTPS──▶ Cloudflare edge ──▶ Access wall (email one-time code)
                                   │                    only 2 emails may pass
                                   ▼
                            tunnel (outbound from the PC — no ports opened,
                                    home IP never published)
                                   ▼
                     cloudflared (Windows service) ──▶ http://localhost:5179
                                                        ShelfAware.Web.exe
```

Why this shape:

- **No client software for family.** The tailnet door requires Tailscale on every
  device; this one needs a browser and an inbox. (The tailnet publish still works and
  stays — two doors, one app.)
- **Nothing exposed.** `cloudflared` dials *out*; no router ports, no DDNS, no public
  IP. An anonymous visitor gets Cloudflare's wall, not Kestrel.
- **Zero app changes.** The app already binds loopback only, trusts
  `X-Forwarded-For`/`-Proto` from a loopback proxy (cloudflared is one — same shape as
  Tailscale Serve), has registration locked (`Auth:AllowRegistration: false` — invite
  joins still work), and runs managed keys with daily quotas. Probed before setup;
  nothing needed touching.
- **$0.** Tunnel and Access are free at this scale (Access free tier covers 50 users).

## As-built setup (Cloudflare's 2026 "Tunnels & Mesh" dashboard)

The dashboard's names changed out from under the docs recently; these are the names
that actually worked. Everything below is in the Cloudflare One / Zero Trust dashboard.

1. **cloudflared**, installed with winget in an elevated PowerShell:

   ```powershell
   winget install --id Cloudflare.cloudflared
   ```

2. **The tunnel** — Networks → Tunnels → Create a tunnel → *Cloudflared* → named
   `shelfaware-family`. The connector page shows a Windows command ending in a long
   token: run it in a fresh elevated PowerShell (`cloudflared service install <token>`).
   The token is a credential — never paste it into a chat, a file, or a commit.
   Connector shows "Connected" within seconds; the service starts at boot, matching the
   app's own boot task.

3. **The route** — on the tunnel, the **Published application routes** tab (⚠️ see trap
   1 below): Add → subdomain `family`, domain `shelfaware.net` (a dropdown of your
   zones — that dropdown is how you know you're on the right screen), service `HTTP`,
   URL `localhost:5179`. Catch-all rule left at 404. Saving this is what creates the
   DNS record — a **proxied CNAME** (orange cloud is *correct* for a tunnel; contrast
   the demo droplet's A record, which must stay gray/DNS-only).

4. **The Access application** — Access → Applications → Create → **Self-hosted**,
   **public** hostname (not private — private is the WARP-client world), domain exactly
   `family.shelfaware.net`. Session duration **1 month** (lives under *Experience
   settings* now). *Apply instant authentication* off; *Authenticate with Cloudflare
   One Client* off (WARP again). Authentication tab: *Accept all available identity
   providers* on.

5. **One-time PIN** — added once at the **organization** level (search the dashboard
   for "identity providers" → Add new → One-time PIN tile; there is nothing to
   configure on it). Until this exists, the only login option apps can offer is
   "Cloudflare" SSO — which authenticates whatever Cloudflare account is in the
   browser, useless for family.

6. **The policy** — on the application: Allow · Include → **Emails** → the exact two
   family addresses. Codes are only ever sent to an address that matches a policy —
   deliberately, so the login page can't be used to probe which emails are valid.
   Consequence worth knowing: **a policy mismatch and an undelivered email look
   identical** ("the code never came").

## The traps, each with its tell

Six of them in one evening, all in the dashboard, none in the app.

1. **"Hostname routes" is not the tab you want.** It's the WARP/Gateway routing
   feature; the tell is any mention of *egress* or "traffic must be on-ramped." The
   publish-a-website tab is **Published application routes**. If a screen demands an
   on-ramp method, you are configuring the wrong product.
2. **A `www.` crept into the first route attempt.** The Access app guards the exact
   hostname; `www.family` ≠ `family`. Check what you actually created, not what you
   meant to type.
3. **Session duration hides under "Experience settings"** on the app, not the creation
   wizard's front page. Fine to create the app first and set it after.
4. **One-time PIN is an *identity provider* you must add**, org-level, despite having
   zero configuration. Its absence is why a fresh org's login page shows only the
   (useless here) "Cloudflare" button.
5. **A policy can show as attached in the dashboard while the edge evaluates zero
   policies.** The real Access log told the truth (`Access denied … 0 policies
   evaluated`) while every dashboard screen looked correct. Fix that worked: rebuild
   the policy inline on the application (and re-verify with a real login). Judge
   attach-state by the newest **Access log row**, never by the config screens.
6. **The policy tester lies in a virgin org.** It simulates an *existing* Zero Trust
   user; before anyone has ever logged in there are none, so it fails
   (`access.api.error.invalid_user_id`) and reports "0 policies evaluated" — the
   debris of its own failure, not a statement about the edge. It becomes trustworthy
   exactly one successful login after you stop needing it.

Plus one that isn't Cloudflare's fault: **querying the hostname before the route
existed caches the miss.** Negative answers stick for up to the zone's SOA minimum —
measured 1800s (30 min) here — per resolver (the router, each phone's carrier). During
the countdown, `Resolve-DnsName … -Server 1.1.1.1` shows the truth, a phone on
cellular usually works immediately, and the one real mistake available is re-editing
healthy config to fix what is only a timer.

## Verification signatures

How each layer proves itself, from the outside in:

- **DNS:** `Resolve-DnsName family.shelfaware.net -Server 1.1.1.1` → Cloudflare edge
  IPs (104.21.x / 172.67.x A records + AAAA).
- **Tunnel + Access wall:** `curl.exe -sI https://family.shelfaware.net` → `HTTP/1.1
  302`, `Www-Authenticate: Cloudflare-Access`, `Location:
  https://<team>.cloudflareaccess.com/cdn-cgi/access/login/family.shelfaware.net?…`.
  An anonymous request marched to the login is the whole design in one response.
- **A real login:** newest Access log row (Zero Trust → Logs → Access) reading
  `"connection": "onetimepin", "allowed": true`.
- **The app through the wall:** any click that works proves the Blazor WebSocket
  circuit rode through Cloudflare; data on screen proves the tunnel reached the real
  instance.

All four observed 2026-08-12.

## Operating it

- **Add a family member:** add their email to the app's Allow policy (edge), and hand
  them a household invite code from Settings (app). Two layers, two adds.
- **Remove one:** remove the email from the policy *and* remove the member in Settings
  — the policy stops future logins; the security-stamp bump in member removal kills
  live sessions.
- **Logs/audit:** Zero Trust → Logs → Access is the record of every attempt, allowed
  and denied, with email and IP.
- **Updating cloudflared:** `winget upgrade Cloudflare.cloudflared` in an elevated
  shell now and then; the service survives it.
- **Updating the app itself** is unchanged by any of this — publish → stop the
  "ShelfAware Server" task → replace `ShelfAware-server` → start. (A
  `publish-family.ps1` to script that is planned alongside the next feature arc.)
- **If the site is down:** check the PC is on, the "ShelfAware Server" task is
  running, and the tunnel shows Connected in the dashboard — in that order.

## What this deliberately is not

- Not a move off the PC — the family box is this machine; the site is up when it's on.
- Not a second app instance — same process, same SQLite files, same backups as before;
  the tunnel is a front door, not a deployment.
- Not the demo — `demo.shelfaware.net` (droplet, BYOK, public registration) and
  `family.shelfaware.net` (this PC, managed keys, two emails) share a domain and
  nothing else.
