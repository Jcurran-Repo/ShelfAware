# Deploying Shelf Aware to a DigitalOcean droplet

How the public demo box runs: one small Ubuntu droplet, the app as a systemd service on
loopback, Caddy in front for TLS, SQLite on the local disk. The moving parts are in
[`deploy/`](../deploy) — a unit file, a Caddyfile, an env template, the droplet-side
[`install.sh`](../deploy/install.sh), and [`deploy.ps1`](../deploy/deploy.ps1), which
publishes and ships a build from a Windows machine in one command.

Three constraints shape all of it:

- **HTTPS is not optional.** Blazor Server rides a WebSocket, Identity wants secure
  cookies, and the microphone features (voice assistant, cook-along) only exist in a
  secure context — plain HTTP silently kills them. Caddy gets a Let's Encrypt
  certificate automatically, which is why it's the proxy here.
- **The app already expects a loopback proxy.** `Program.cs` honors
  `X-Forwarded-For`/`-Proto` from loopback (`UseForwardedHeaders`), so the HTTPS
  redirect, HSTS, and the per-IP rate limits see the real visitor rather than
  `127.0.0.1`. Nothing to configure — but a proxy that *doesn't* send those headers
  breaks exactly those things (see the Nginx note at the bottom).
- **SQLite means one box.** No horizontal scaling, no external database — the whole
  state is files under one directory, which makes backup a copy and restore a copy.

## What you need

- A droplet: **Ubuntu 24.04 LTS**, 1 GB RAM works, 2 GB is comfortable. Root SSH.
- A domain or subdomain whose **A record already points at the droplet** — certificate
  issuance fails without it, and the mic needs the resulting HTTPS.
- Locally: Windows 10+ (`ssh`, `scp`, and `tar` are built in) with the .NET 10 SDK.

## First-time setup (once, on the droplet)

**1. Service user + data directory.** The app runs as `shelfaware` and writes only
under `/var/lib/shelfaware` (its `DataDir`):

```bash
adduser --system --group --home /var/lib/shelfaware shelfaware
```

```bash
chmod 700 /var/lib/shelfaware
```

**2. Timezone — do not skip this.** Every "today" in the app (purchase dates, signals,
predictions) is server-local by design; on a UTC box an evening "bought today" lands on
tomorrow's date. Set the household's real timezone:

```bash
timedatectl set-timezone America/New_York
```

**3. Firewall:**

```bash
ufw allow OpenSSH && ufw allow 80 && ufw allow 443 && ufw enable
```

**4. Config.** Copy [`deploy/env.example`](../deploy/env.example) to
`/etc/shelfaware/env`, edit it, and `chmod 600` it. The committed default is the
**demo posture**: `Llm__KeyMode=Byok`, no keys on the server, visitors paste their own
in Settings. The managed / family variant sits in the same file, commented out.

**5. Service.** Copy [`deploy/shelfaware.service`](../deploy/shelfaware.service) to
`/etc/systemd/system/shelfaware.service`, then:

```bash
systemctl daemon-reload && systemctl enable shelfaware
```

(Not `--now` — there is nothing to start until the first deploy ships files;
`install.sh` starts it.)

**6. First deploy — from your own machine, at the repo root:**

```bash
powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1 -TargetHost root@<droplet-ip>
```

It publishes self-contained `linux-x64` (so the droplet needs no .NET install), tars,
uploads, and runs `install.sh`, which unpacks to `/opt/shelfaware` and starts the
service. First boot creates the SQLite databases under `/var/lib/shelfaware`
(`EnsureCreated` plus the additive migrations — the pre-v3-file guard only concerns
migrated data, never a fresh box). Watch it come up:

```bash
journalctl -u shelfaware -f
```

**7. Caddy.** Ubuntu 24.04's universe archive carries it (use Caddy's own apt repo if
you want the newest release):

```bash
apt install -y caddy
```

Copy [`deploy/Caddyfile`](../deploy/Caddyfile) over `/etc/caddy/Caddyfile`, put the
real domain in it, and `systemctl reload caddy`. The certificate is fetched on first
use.

**8. Sign in.** Browse to the domain and register — the very first account is always
allowed regardless of the registration setting, and it creates your household.

## Every deploy after that

```bash
powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1 -TargetHost root@<droplet-ip>
```

`install.sh` stages the new build outside the live directory, so the service is down
for seconds, and it keeps the previous build at `/opt/shelfaware.prev` — roll back by
moving that back and `systemctl start shelfaware`. Data is untouched either way: it
lives in `/var/lib/shelfaware`, not the app directory. (The publish output lands in
`src/ShelfAware.Web/bin/publish/linux-x64` locally, which is gitignored.)

## The demo posture, spelled out

- **BYOK, enforced by absence.** The box holds no AI keys, so there is nothing to leak
  and nobody's tokens to spend; `Llm__KeyMode=Byok` states explicitly what Auto would
  infer from the missing key. The Settings key panel, the strict CSP, and the
  key-custody story in the README's "Whose keys?" section were built for exactly this
  deployment.
- **Keyless visitors still get a real demo.** The sample pantry, the review grid,
  prediction/backtest/reports over the seeded catalog, and the cached recipe narration
  all work with no key at all — a visitor's own key switches on extraction, chat, and
  live voice.
- **Registration stays open** (the default). If the open door ever attracts abuse, set
  `Auth__AllowRegistration=false` — invite-code joins and existing accounts keep
  working. The per-IP rate limits on the `/Account` POSTs and the signed-url endpoint
  are already in place, and they see real client IPs because of the forwarded headers.
- **`Llm__AllowCustomEndpoint` stays off.** On a public box, a visitor-supplied base
  URL that the *server* then calls is an SSRF invitation; the option exists for
  self-hosters pointing at their own Ollama.

## Running it for your own household instead

Same box, three differences, all in `/etc/shelfaware/env`:

1. `Llm__KeyMode=Managed` plus `Llm__ApiKey` (and the ElevenLabs pair for voice). Your
   keys become authoritative and the key panel disappears. The daily quotas
   (`Llm__DailyCallLimit`, …) exist for metering *other* households on your wallet —
   unset means unlimited, the sensible self-host default.
2. `Auth__AllowRegistration=false` once your accounts exist.
3. **Migrating existing data** (say, off a Windows box): stop the app on both ends,
   copy the contents of its `app-data/` into `/var/lib/shelfaware`, and
   `chown -R shelfaware:shelfaware` the result. Copy `shelfaware.db*`, `auth.db*`,
   `receipts/`, and `tts-cache/` — but **not `keys/`**: Windows DataProtection keys
   are DPAPI-encrypted and no Linux box can decrypt them. The droplet mints fresh keys
   on first boot; the only consequence is that everyone signs in again once (accounts
   and password hashes live in `auth.db` and port cleanly). Copying with the app
   stopped is what keeps the SQLite `-wal` files consistent.

## Backups

Everything that matters is under `/var/lib/shelfaware`. A nightly cron as root
(`apt install -y sqlite3` once):

```bash
#!/usr/bin/env bash
set -euo pipefail
d=/root/backups/$(date +%F); mkdir -p "$d"
sqlite3 /var/lib/shelfaware/shelfaware.db ".backup '$d/shelfaware.db'"
sqlite3 /var/lib/shelfaware/auth.db ".backup '$d/auth.db'"
tar -czf "$d/files.tar.gz" -C /var/lib/shelfaware receipts tts-cache keys
```

`sqlite3 .backup` is WAL-safe while the app runs; the folders are plain files. DO's
droplet snapshots make a fine second layer, not a substitute — they're
crash-consistent, not application-aware.

## If you use Nginx instead of Caddy

Everything Caddy does silently you must write yourself, and each omission breaks a
specific thing:

```nginx
# plus the standard "map $http_upgrade $connection_upgrade" block in the http context
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;            # the Blazor circuit is a WebSocket
proxy_set_header Connection $connection_upgrade;
proxy_set_header Host $host;
proxy_set_header X-Forwarded-For $remote_addr;     # per-IP rate limits
proxy_set_header X-Forwarded-Proto $scheme;        # HTTPS redirect + HSTS + cookies
proxy_read_timeout 120s;                           # idle circuits die at the 60s default
```

…plus certbot for the certificate. `X-Forwarded-Proto` matters most: without it the
app sees `http` and `UseHttpsRedirection` loops.

## What's verified and what isn't

The publish path is verified from this repo on Windows: `dotnet publish -r linux-x64
--self-contained` succeeds and the output carries the Linux native SQLite library
(`libe_sqlite3.so`) and the `ShelfAware.Web` apphost the unit file execs. The
droplet-side steps are written to this app's actual behavior (`DataDir`, the
forwarded-headers middleware, the Windows-only DPAPI branch) but have not yet been run
against a live droplet — on the first real deploy, watch `journalctl -u shelfaware -f`
through first boot before calling it done.
