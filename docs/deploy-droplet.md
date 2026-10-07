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
  `X-Forwarded-For`/`-Proto` from loopback (`UseForwardedHeaders`), so HSTS, the
  per-IP rate limits, and the URLs the app generates (OAuth callbacks) see the real
  visitor and scheme rather than the proxy's localhost hop. Nothing to configure —
  but a proxy that *doesn't* send those headers breaks exactly those things (see the
  Nginx note at the bottom).
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

Locale is the same gotcha wearing a different hat: a systemd service starts with no
`LANG` at all, and with none set .NET falls back to the invariant culture — every
price renders as `¤3.99` instead of `$3.99`. The env template ships
`LANG=en_US.UTF-8`; edit it if the household's real locale is something else.

**3. Firewall:**

```bash
ufw allow OpenSSH && ufw allow 80 && ufw allow 443 && ufw enable
```

**4. Config.** For the **demo box**, copy
[`deploy/demo-box.env.example`](../deploy/demo-box.env.example) to `/etc/shelfaware/env`,
fill in its `<SET ME>` lines, and `chmod 600` it — the demo runs **managed**, on a dedicated,
spend-capped key, with the box-wide valve and sign-up controls already filled in (the mechanics
are in "[The demo box on YOUR key](#the-demo-box-on-your-key-the-ai-valve--abuse-controls)"
below). For a **self-host** box, start from the annotated full reference
[`deploy/env.example`](../deploy/env.example) instead, whose committed default is BYOK: no keys
on the server, visitors paste their own in Settings.

> **Free read-aloud voice (optional):** set `Speech__Provider=Piper` (the demo's voice — Kokoro is
> the richer, heavier alternative) to voice recipes with a model running inside the app instead of ElevenLabs — $0 per call, no key, and no
> second service. Unpack a model first with [docs/deploy-kokoro.md](deploy-kokoro.md); the
> app refuses to start if it is pointed at one that isn't there.

**5. Service.** Copy [`deploy/shelfaware.service`](../deploy/shelfaware.service) to
`/etc/systemd/system/shelfaware.service`, then:

```bash
systemctl daemon-reload && systemctl enable shelfaware
```

(Not `--now` — there is nothing to start until the first deploy ships files;
`install.sh` starts it.)

**6. First deploy — from your own machine, at the repo root:**

```powershell
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

```powershell
powershell -ExecutionPolicy Bypass -File deploy\deploy.ps1 -TargetHost root@<droplet-ip>
```

`install.sh` stages the new build outside the live directory, so the service is down
for seconds, and it keeps the previous build at `/opt/shelfaware.prev`. **If the new build
does not come up, it rolls back on its own**: the previous build goes back to `/opt/shelfaware`
and is started, the failed build is kept at `/opt/shelfaware.failed` for inspection (nothing
removes it until the next rollback — `rm -rf` it once you have looked), and the script still
exits non-zero so the deploy that ran it goes red. Its output says which of the two happened.
Data is untouched either way: it lives in `/var/lib/shelfaware`, not the app directory. (The
publish output lands in `src/ShelfAware.Web/bin/publish/linux-x64` locally, which is
gitignored.)

The unit carries `StartLimitBurst=5` over `StartLimitIntervalSec=120`, so a build that crashes
on launch stops being relaunched after five tries and the unit reads `failed` rather than
cycling through `activating` forever. The rollback runs `systemctl reset-failed shelfaware`
before starting the previous build because a tripped limit also refuses a manual `start` —
remember that if you ever roll back by hand.

## Deploying from CI, so it doesn't need anyone at a desk

[`deploy/deploy.ps1`](../deploy/deploy.ps1) needs a Windows checkout, an ssh key on the machine, and a
person at it. [`.github/workflows/deploy-droplet.yml`](../.github/workflows/deploy-droplet.yml) is the
same sequence — publish `linux-x64`, ship the tarball, run `install.sh`, check `/healthz` — run by a
GitHub runner instead. It exists because a **project session cannot reach the droplet**: it has no ssh
client and port 22 is unreachable from it, while a runner has both.

**Manual dispatch only, never on push.** A merge to `master` must not deploy anything: what LANDS on
master and what is LIVE are separate decisions and stay separate here. The workflow takes a `ref`, so
an *unmerged* branch can be put on the demo box and tried before it is merged — which is the right
order for a demo box.

Setting it up, once:

1. **A dedicated deploy key**, not a personal one:
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/shelfaware-deploy -C 'github-actions deploy' -N ''
   ssh-copy-id -i ~/.ssh/shelfaware-deploy.pub root@<droplet>     # or append it to authorized_keys
   ssh-keyscan -H <droplet> > /tmp/known_hosts                    # for step 3's third secret
   ```
2. **An environment named `droplet`** (Settings → Environments) with yourself as a required reviewer.
   Nothing reaches the box until you press the button.
3. **Three secrets on THAT ENVIRONMENT** — Settings → Environments → droplet → *Environment secrets*,
   **not** repository secrets: `DROPLET_SSH_KEY` (the contents of the private half), `DROPLET_HOST`
   (`root@<ip>`), and `DROPLET_KNOWN_HOSTS` (that `ssh-keyscan` output, required).

   ⚠️ **The distinction is the whole security of this.** On a `workflow_dispatch`, GitHub runs the
   workflow file *from the branch being dispatched* — so anyone who can push a branch can push a copy
   of `deploy-droplet.yml` with the `environment:` line removed and dispatch that. A **repository**
   secret would be handed to it anyway, no reviewer prompted, and the private half of a key that is
   `root` on the box would be one `echo` away. An **environment** secret would not: no environment, no
   key, no deploy. The approval stops being a line in a file that the next branch can delete.

Run it from Actions → *Deploy to the droplet* → **Run workflow**, choosing the branch.

**It refuses a commit CI has not passed.** Before building anything, the workflow resolves the
chosen ref to a commit and asks GitHub for that commit's `Build & test` check run (ci.yml's job);
anything but a `success` conclusion — no run, still running, failed — stops the deploy with the
reason in the log. The approval button proves a person pressed it, not that the tests did, and a
ref can name a branch pushed a minute ago that has never been built. CI runs on pull requests and
on pushes to `master`, so a branch with no PR has no check: open one (or deploy `master`). For an
emergency only, the **force** input skips the gate; a forced run says so in a warning annotation on
the run and in capitals in the log, so it can never pass for a gated one in the history later.

Tick **bootstrap** the first time — and again whenever a new voice becomes the default: it adds the 2 GB
swap file and unpacks the voice models (the Piper voices and Kokoro) and Moonshine, idempotently, so a
rebuilt droplet is one dispatch away rather than an afternoon with this page. Every archive is checked
against a recorded sha256 before anything is unpacked — they are fetched as root onto a box holding
real data, and a release tag is mutable.

⚠️ **The models live in `/var/lib/shelfaware-models` — root's, and deliberately not in the app's
home.** `/var/lib/shelfaware` is the service account's own (`chmod 700`, above), and anything directly
inside a directory the app owns, the app can rename away and replace: models kept there could be
swapped between deploys for ones it made, and it loads its voice by that name. Under root-owned
`/var/lib` it can do neither. The bootstrap still pins the directory by inode and refuses unless it is a
root-owned 755 directory at exactly that path — defense in depth, costing nothing — and downloads into a
root-only directory inside it, moving in only verified, whole, root-owned models.

**Moving a box that already has models in the old place (`/var/lib/shelfaware/models`)** — no downtime,
in this order:

1. Deploy with **bootstrap** ticked. It fetches fresh, hash-checked copies of **the four models it
   installs** (the two Piper voices, Kokoro and Moonshine) into `/var/lib/shelfaware-models` — it does
   not copy the old ones, which sat where the app could have changed them — and warns that the old
   directory is still there. The app keeps running on the old paths meanwhile. ⚠️ A box running a
   model the bootstrap does not install (Kitten or Matcha, put there by hand from
   [voice-bakeoff.md](voice-bakeoff.md)) needs that pasted install re-run first — its blocks already
   install into the new location.
2. **Only once that run is green**, change every `Speech__*__ModelDirectory` in `/etc/shelfaware/env`
   from `/var/lib/shelfaware/models/…` to `/var/lib/shelfaware-models/…`, then
   `systemctl restart shelfaware` and check `/healthz`. If it does not come back, the journal
   (`journalctl -u shelfaware -n 50`) names the file it could not find — the app refuses to start on a
   missing model, by design. Put the old lines back and restart; nothing has been removed yet.
3. Remove the old directory: `rm -rf /var/lib/shelfaware/models` (no trailing slash — if the name has
   become a symlink, that removes the link, not what it points at).

⚠️ **The workflow only becomes dispatchable once it is on `master`.** GitHub lists a
`workflow_dispatch` workflow from the default branch, so there is no *Run workflow* button — and no way
to trigger it for any branch — until this file has been merged. Merging it deploys nothing by itself.

⚠️ **The env file is not in this**, by design. `/etc/shelfaware/env` holds the box's secrets, lives
only on the box, and a deploy never touches it — which is also why a setting change still needs an ssh
session and a `systemctl restart`.

## The demo posture, spelled out

- **Managed, on a dedicated and spend-capped key.** The demo box runs `Llm__KeyMode=Managed`
  with its own Anthropic key so a visitor can try extraction, chat and recipe reading with no
  key of their own; the key is bounded in layers — per-household quotas, the box-wide daily
  valve, the account cap and email confirmation, and the spend limit set on the key itself in
  the Anthropic console — all of which the next section spells out. BYOK (`Llm__KeyMode=Byok`,
  no keys on the server, visitors paste their own in Settings) remains the **self-host**
  posture, and the Settings key panel, the strict CSP, and the key-custody story in the README's
  "Whose keys?" section were built for that one.
- **Keyless visitors get the whole demo.** The sample pantry, the review grid, and
  prediction/backtest/reports over the seeded catalog work with no key on any box; on the
  managed demo the AI surfaces work too, until the day's valve closes. Read-aloud is free and
  in-process (Piper, below), so it costs the key nothing either.
- **Registration stays open** (the default). If the open door ever attracts abuse, set
  `Auth__AllowRegistration=false` — invite-code joins and existing accounts keep
  working. The per-IP rate limits on the `/Account` POSTs and the signed-url endpoint
  are already in place, and they see real client IPs because of the forwarded headers.
- **`Llm__AllowCustomEndpoint` stays off.** On a public box, a visitor-supplied base
  URL that the *server* then calls is an SSRF invitation; the option exists for
  self-hosters pointing at their own Ollama.

## The demo box on YOUR key: the AI valve + abuse controls

To let visitors try the AI without bringing a key, run the demo **managed** — the box
uses your key, so it needs bounding. A ready-to-edit env with all of this filled in is
[`deploy/demo-box.env.example`](../deploy/demo-box.env.example); the mechanics:

**Turning the valve on = config + managed + a restart.** The box-wide valve caps the
day's AI calls across *all* households (per-household quotas can't bound the box when
every new sign-up gets its own allowance). It only does anything when **all three** are
true:

1. `Demo__DailyGlobalCallLimit=300` (and optionally `Demo__AlertThreshold=50`) is set —
   unset, the valve is a complete no-op (it never even writes a row).
2. The box is **managed** — `Llm__KeyMode=Managed` + `Llm__ApiKey`. ⚠️ **The valve only
   counts the HOST's key.** On a BYOK box, visitors spend their own keys, so there's
   nothing to cap; a demo box must be managed for the valve to matter.
3. You **restart** the service (`systemctl restart shelfaware`) — `Demo:*` is read once
   at boot, not hot-reloaded.

**What a capped visitor sees:** every AI surface shows *"This demo box is usage-limited
and has hit today's limit — please come back tomorrow"* **before** attempting the call,
so nothing is spent. The counter resets at **midnight, server-local**.

**⚠️ The real ceiling is the spend limit on the key itself** (set it in the Anthropic
console). The valve is the *polite* stop that hands the friendly message first; the
key's spend limit is the *hard* stop for your wallet. The counter is checked and then
reserved in two steps (not one atomic step), so a concurrent burst can slightly overshoot
the cap — bounded, and backstopped by that spend limit. **Set both.**

**The layers, outermost first:** `Llm__DailyCallLimit`/`DailyTokenLimit` (fair-per-visitor)
→ `Demo__DailyGlobalCallLimit` (the box-wide wallet valve) → `Demo__AlertThreshold` (an
early "traffic is arriving" heads-up, logged as a Warning and shown on **/admin → Demo
box usage** as "· reached" — *not* in the error log) → the key's own spend limit (the
hard ceiling).

**Sign-up abuse controls** (a public box wants these): `Auth__RequireEmailConfirmation=true`
(a real inbox must confirm before sign-in — this NEEDS the `Email:` block, or the app
won't boot) and `Auth__DailyAccountCreationLimit=10` (box-wide new-accounts/day). ⚠️ On a
box with **existing** accounts, turning on email confirmation locks them out until you
backfill `sqlite3 auth.db "UPDATE AspNetUsers SET EmailConfirmed = 1;"` — or start from a
fresh DB (no accounts to backfill).

**Read-aloud voice** on the demo is free via Piper (`en_US-ryan-medium`), which runs in the app's own
process — set `Speech__Provider=Piper` *after* the bootstrap has unpacked the models
([docs/deploy-piper.md](deploy-piper.md); Kokoro is the heavier option, [docs/deploy-kokoro.md](deploy-kokoro.md)).
Until then, leave it commented; chat + receipts don't need it, and read-aloud just fails
soft. Piper is small; Kokoro budgets ~600 MB of RAM once a recipe has been read, so add swap on a 2 GB box.

**Payments stays OFF** on the demo (no `Payments` section) — with billing off, the AI
simply works for every fresh household, gated only by the caps above.

## Running it for your own household instead

Same box, three differences, all in `/etc/shelfaware/env`:

1. `Llm__KeyMode=Managed` plus `Llm__ApiKey` (and the ElevenLabs pair for voice). Your
   keys become authoritative and the key panel disappears. The daily quotas
   (`Llm__DailyCallLimit`, …) exist for metering *other* households on your wallet —
   unset means unlimited, the sensible self-host default.
2. `Auth__AllowRegistration=false` once your accounts exist.
3. **Migrating existing data** (say, off a Windows box): stop the app on both ends,
   copy the contents of its `app-data/` into `/var/lib/shelfaware`, and
   `chown -R shelfaware:shelfaware` **just what you copied** (for example
   `cd /var/lib/shelfaware && chown -R shelfaware:shelfaware shelfaware.db* auth.db* receipts tts-cache`)
   — not the whole directory, which may hold things that are not the app's to own. Copy
   `shelfaware.db*`, `auth.db*`,
   `receipts/`, and `tts-cache/` — but **not `keys/`**: Windows DataProtection keys
   are DPAPI-encrypted and no Linux box can decrypt them. The droplet mints fresh keys
   on first boot; the only consequence is that everyone signs in again once (accounts
   and password hashes live in `auth.db` and port cleanly). Copying with the app
   stopped is what keeps the SQLite `-wal` files consistent.

## Backups

Everything that matters is under `/var/lib/shelfaware`. `deploy/backup-droplet.sh` is the
nightly backup; `deploy/install-droplet-backup.sh` puts it on a systemd timer. Both need
`sqlite3` and `rsync` (`apt install -y sqlite3 rsync`).

```bash
# once, as root, from the uploaded deploy/ directory
./install-droplet-backup.sh --keep-days 14

# rehearse it before you trust it — writes nothing, deletes nothing
/usr/local/lib/shelfaware/backup-droplet.sh \
    --data-dir /var/lib/shelfaware --dest /var/backups/shelfaware --keep-days 14 --dry-run
```

What it does, and why each part is the way it is, is written at the top of the script. The
short version: it snapshots both databases with `VACUUM INTO` over a **read-only** connection
(WAL-safe while the app runs, and the copy is a self-contained `.db` with no `-wal`/`-shm`
beside it), runs `PRAGMA integrity_check` **against the copy**, keeps dated snapshots for
`--keep-days`, and holds a rolling mirror of `receipts`, `recipe-images`, `keys` and
`tts-cache`. It writes one line per run to `backup-log.txt`, success or failure.

⚠️ **This is same-disk only until you set up rclone**, which protects you against a bad write
and not at all against losing the droplet. One-time: install rclone, run `rclone config`
(interactive — a human does this once), then re-run the installer with
`--rclone-remote yourremote:shelfaware-backups`. The offsite step then runs inside the nightly
job, with `--backup-dir`, so a bad local night can't erase good offsite copies.

To restore: stop the service, copy the chosen `db-*` snapshot's two `.db` files into
`/var/lib/shelfaware` (deleting any `-wal`/`-shm` beside them — those belong to the database
you are replacing), restore the `files/` trees, `chown -R shelfaware:shelfaware` **the restored files
and directories only**, start. (The models are not in the backup and not in the app's home: they are in
root's `/var/lib/shelfaware-models`, and a bootstrap re-downloads any that are missing.)

If `/var/lib/shelfaware-models` has ended up with the wrong owner or mode (a hand-made `mkdir` under a
tight umask, or a `chmod` aimed at the wrong directory), the next bootstrap refuses to touch it, by design. Recover by removing it —
`rm -rf /var/lib/shelfaware-models` (no trailing slash) — and re-running the deploy with **bootstrap**
ticked, which re-downloads every model into a fresh root-owned directory.

DO's droplet snapshots make a fine second layer, not a substitute — they're crash-consistent,
not application-aware.

Each `db-*` snapshot also holds `config/env` and `config/Caddyfile` (mode 600) — the box's
`/etc/shelfaware/env` and `/etc/caddy/Caddyfile` — so a rebuilt droplet gets its keys, caps and
proxy config back along with its data. ⚠️ That makes every snapshot, and the offsite remote they
sync to, a copy of the box's secrets: choose a remote you would trust with the API key.

## Before inviting testers

Box-side checks, one line each, for the day the link goes to people who are not you. Do them in
order; most take a minute.

1. **Set the spend limit on the dedicated key** — Anthropic console → the demo's key → a daily
   spend limit. This is the hard ceiling; everything in the env file is the polite one.
2. **Register the admin account yourself, first.** `Admin__Emails__0` only grants `/admin` to
   whoever signs in with that address — register it before anyone else can, then open `/admin`
   and confirm the *Demo box usage* panel renders.
3. **Point an external uptime monitor at `https://demo.shelfaware.net/healthz`** — any free
   pinger, alerting on anything but a 200. `/healthz` is anonymous, cached for 5 s, and checks
   both databases; it is the box's own answer to "is it serving", and nothing inside the box can
   tell you the box is down.
4. **Confirm the backup timer is live and do one restore drill.** `systemctl list-timers` must
   show `shelfaware-backup.timer` with a next-run time; then run
   `/usr/local/lib/shelfaware/backup-droplet.sh … --dry-run` (the installer printed the exact
   line), and once for real (`systemctl start shelfaware-backup.service`). Walk the restore steps
   at the top of `deploy/backup-droplet.sh` against a scratch directory at least once — a backup
   nobody has restored from is a hope.
5. **Confirm the clock and the locale**: `timedatectl` shows the household's timezone (or the env
   file carries `TZ=`), and the env file carries `LANG=en_US.UTF-8`. Without them an evening
   purchase files on tomorrow's date and prices render as `¤3.99`.
6. **Set `Auth__InviteCodeLifetimeDays`** (the demo example ships `7`). An invite code admits
   its bearer to a household's whole pantry and bypasses `AllowRegistration` by design; on a box
   whose link will be pasted into group chats, a code must not be permanent.
7. **Keep a copy of `/etc/shelfaware/env` off the box.** The backup now includes it (above), so
   an offsite remote covers this — but if the backup is still same-disk only, `scp` it somewhere
   safe by hand. It is the one file a rebuilt droplet cannot regenerate.
8. **Grant the Actions app the branch-protection bypass on `master`** — GitHub → Settings →
   Branches → the `master` rule → *Allow specified actors to bypass required pull requests* →
   add the GitHub Actions app. Until then the `test-status.json` snapshot that `ci.yml` writes and
   the mutation score that `mutation.yml` writes are refused with `GH006` and land only as a
   warning on the run (run 35926123956 is one), and the `/admin` *Tests & quality* card keeps
   reading a stale file. One setting, owner only.

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
proxy_set_header X-Forwarded-Proto $scheme;        # HSTS + OAuth redirect URIs
proxy_read_timeout 120s;                           # headroom for stalled transports
```

…plus certbot for the certificate. (SignalR's 15-second keep-alives normally outrun
the 60-second `proxy_read_timeout` default — raising it is cheap headroom, not a fix
for a known failure.) `X-Forwarded-Proto` still matters most: without it the app
believes every request is plain `http`, so HSTS never engages and Google sign-in
generates an `http://` redirect URI that Google refuses. (Auth cookies stay `Secure`
either way — production pins that.)

One more on this path: **pin the Host.** Caddy only proxies its exact site hostname,
but `proxy_set_header Host $host` forwards whatever the client sent — and the
password-reset email builds its link from that header, so a permissive/default Nginx
server block turns a forged Host into a link-poisoning vector. Either make the Nginx
`server_name` exact (no default catch-all reaching this app), or set
`AllowedHosts=<your-domain>` in `/etc/shelfaware/env` so the app itself refuses
foreign hosts — ideally both.

## Traps

Each of these has cost a real deploy, or was found the night before one would have.

- **`AllowedHosts` turns the loopback health check into a 400.** Once the env file pins
  `AllowedHosts=demo.shelfaware.net`, ASP.NET's host filtering answers `400 Bad Request` to any
  request whose `Host` header is not that name — and a bare `curl http://127.0.0.1:5000/healthz`
  on the box sends `Host: 127.0.0.1:5000`. **The tell:** the site works in a browser, the service
  is `active`, the journal shows nothing (the rejection happens before any logging), and the
  loopback curl returns a 400 with an empty body. The fix is to send the name:
  `curl -H "Host: demo.shelfaware.net" http://127.0.0.1:5000/healthz`. The deploy workflow reads
  the name off the box's own env file for exactly this reason, and an external monitor hits the
  public URL, which carries the right Host already.
- **A systemd service starts with no timezone and no locale** — step 2 of the first-time setup.
  Evening purchases on tomorrow's date, prices as `¤3.99`.
- **A tripped start limit refuses a manual start.** After five crashes in two minutes the unit is
  `failed` and `systemctl start shelfaware` does nothing until `systemctl reset-failed shelfaware`.
  `install.sh`'s rollback does this for you; a hand rollback has to.

## What's verified and what isn't

The publish path is verified from this repo on Windows: `dotnet publish -r linux-x64
--self-contained` succeeds and the output carries the Linux native SQLite library
(`libe_sqlite3.so`) and the `ShelfAware.Web` apphost the unit file execs. The
droplet-side path has now run for real (first live deploy 2026-08-11, Ubuntu 24.04:
publish → ship → `install.sh` → systemd → Caddy certificate → registration). It
surfaced exactly one gap, since folded back into this kit: a systemd service starts
with no locale, so prices rendered with the invariant culture's `¤` until `LANG`
landed in the env file — the template now ships it. On any future first boot, still
watch `journalctl -u shelfaware -f` before calling it done.
