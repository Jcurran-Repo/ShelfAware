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

**4. Config.** Copy [`deploy/env.example`](../deploy/env.example) to
`/etc/shelfaware/env`, edit it, and `chmod 600` it. The committed default is the
**demo posture**: `Llm__KeyMode=Byok`, no keys on the server, visitors paste their own
in Settings. The managed / family variant sits in the same file, commented out.

> **Free read-aloud voice (optional):** set `Speech__Provider=Kokoro` to voice recipes
> with a model running inside the app instead of ElevenLabs — $0 per call, no key, and no
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
for seconds, and it keeps the previous build at `/opt/shelfaware.prev` — roll back by
moving that back and `systemctl start shelfaware`. Data is untouched either way: it
lives in `/var/lib/shelfaware`, not the app directory. (The publish output lands in
`src/ShelfAware.Web/bin/publish/linux-x64` locally, which is gitignored.)

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

Run it from Actions → *Deploy to the droplet* → **Run workflow**, choosing the branch. Tick
**bootstrap** the first time — and again whenever a new voice becomes the default: it adds the 2 GB
swap file and unpacks the voice models (the Piper voices and Kokoro) and Moonshine, idempotently, so a
rebuilt droplet is one dispatch away rather than an afternoon with this page. Every archive is checked
against a recorded sha256 before anything is unpacked — they are fetched as root onto a box holding
real data, and a release tag is mutable.

⚠️ **The models live in `/opt/shelfaware-models`, not in the app's home, and that is deliberate.**
They used to sit in `/var/lib/shelfaware/models`. The bootstrap pinned that directory by inode while it
ran, so nothing the app re-pointed could change where root *wrote* — but `/var/lib/shelfaware` is the
app's own home, so **between** deploys a compromised app could rename `models/` away and put its own in
its place, and the app would load its model by that name. The next bootstrap would refuse loudly; an
ordinary deploy would never notice. `/opt` is root's and writable by nobody else, so the app now has
nowhere to put a directory of its own and no way to make the name lead to one. The pin stays, and the
bootstrap checks the parent too — the location is only worth anything if it is *true* on the box, and
an `/opt` someone had made group-writable would hand the exposure straight back.

**Moving a Linux box that is already running** (the demo droplet; the family box keeps its models under
its own user profile and is not affected) takes three steps. ⚠️ **Do them in this order.** Each one
leaves the box in a state it can run in, so nothing here is a moment you have to get through quickly —
the reverse order points the app at a directory that does not exist yet, and the first restart after
that is a box that will not boot.

1. **Fill the new location first.** Re-run the deploy with **bootstrap** ticked. It creates
   `/opt/shelfaware-models` and downloads every model into it, hash-checked, exactly as it would on a
   fresh box. The box carries on running off the old directory throughout — its settings have not
   changed yet — so a bootstrap that fails costs nothing but a re-run. Nothing *moves* the old
   directory: `mv`-ing a directory *out of* one the app controls, as root, is the very race this
   location exists to end, and re-downloading has no such question in it.
2. **Then point the box at it, and restart.** In `/etc/shelfaware/env`, rewrite each
   `Speech__*__ModelDirectory` line to read `/opt/shelfaware-models/...` instead of
   `/var/lib/shelfaware/models/...` — ⚠️ **change those lines, do not add new ones.** A second
   definition of the same key is how this box last refused to boot, and it is easy to do by pasting.
   One `sed` does all of them without opening an editor:

   ```bash
   sudo sed -i 's|/var/lib/shelfaware/models|/opt/shelfaware-models|' /etc/shelfaware/env
   grep ModelDirectory /etc/shelfaware/env
   sudo systemctl restart shelfaware
   ```

   Settings are read at startup, so the restart is what makes the change real. If the app refuses to
   boot it says which setting and which files it could not find; the way back is the same `sed` with
   the two paths swapped, and another restart.
3. **Only once the box is up and speaking, delete the old copy.** `sudo rm -rf /var/lib/shelfaware/models`
   — no trailing slash, so that if the name has become a symlink it removes the link and not whatever it
   points at. Leaving this until last is what makes step 2 reversible.

⚠️ **The workflow only becomes dispatchable once it is on `master`.** GitHub lists a
`workflow_dispatch` workflow from the default branch, so there is no *Run workflow* button — and no way
to trigger it for any branch — until this file has been merged. Merging it deploys nothing by itself.

⚠️ **The env file is not in this**, by design. `/etc/shelfaware/env` holds the box's secrets, lives
only on the box, and a deploy never touches it — which is also why a setting change still needs an ssh
session and a `systemctl restart`.

## The demo posture, spelled out

- **BYOK, enforced by absence.** The box holds no AI keys, so there is nothing to leak
  and nobody's tokens to spend; `Llm__KeyMode=Byok` states explicitly what Auto would
  infer from the missing key. The Settings key panel, the strict CSP, and the
  key-custody story in the README's "Whose keys?" section were built for exactly this
  deployment.
- **Keyless visitors still get a real demo.** The sample pantry, the review grid, and
  prediction/backtest/reports over the seeded catalog all work with no key at all — a
  visitor's own key switches on extraction, chat, and voice. (Recipe narration replays
  keyless *after* a household has synthesized it once — a cache hit needs no key — but
  the cache is per household and starts empty, so narration isn't keyless on day one.)
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

**Read-aloud voice** on the demo is free via Kokoro, which runs in the app's own process — set
`Speech__Provider=Kokoro` *after* unpacking a model ([docs/deploy-kokoro.md](deploy-kokoro.md)).
Until then, leave it commented; chat + receipts don't need it, and read-aloud just fails
soft. Budget ~600 MB of RAM once a recipe has been read, and add swap on a 2 GB box.

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
   — never the whole directory: `models/` must stay root-owned, and the bootstrap refuses to write into
   it otherwise. Copy `shelfaware.db*`, `auth.db*`,
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
you are replacing), restore the `files/` trees, `chown -R shelfaware:shelfaware` the restored files
and directories, start.

Nothing in a restore can reach the models any more: they are in `/opt/shelfaware-models`, which is not
under the data directory a restore or a `chown -R` walks. That used to be a live hazard worth its own
warning here — `models/` sat inside `/var/lib/shelfaware`, so a restore that chowned the home as a
whole handed the models to the app and the next bootstrap refused to touch them. If the bootstrap does
refuse, it names what it found; re-run it with **bootstrap** ticked after fixing that.

DO's droplet snapshots make a fine second layer, not a substitute — they're crash-consistent,
not application-aware.

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
