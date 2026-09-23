# Kokoro voice: the read-aloud that runs inside the app

ShelfAware's read-aloud (recipe steps, the chat's spoken confirmations) can run on
[Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) — Apache-2.0, so free even commercially —
**in the app's own process**, through [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx). Raw text
in, audio samples out. There is **no per-character cost, no key, and nothing to meter**, and there is
no second service: no sidecar, no Docker, no Python, and no system `espeak-ng` — the phonemizer data
ships inside the model archive.

That last part is the whole reason this shape was chosen over the HTTP sidecar it replaced. A sidecar
is a second thing to install, start, keep running, keep patched and keep off the internet, and the app
is useless if it is down. The model is a directory.

> **Status:** built, CI-green, and **running on the family box (Windows) since 2026-09-20** — recipes
> read aloud there for $0. On **linux-x64** the publish and the model have been verified on a build box
> (`dotnet publish -r linux-x64 --self-contained` carries both native libraries; `tools/VoiceCheck`
> loads the model and speaks the test sentence), so nothing platform-shaped is left to discover. What
> has **not** happened is a run on **the droplet itself** — the first deploy there is the first test of
> *that box's* CPU and RAM, and the CPU note below is the reason that is not a formality. Run the check
> in step 3 on the droplet before flipping the app over.

This is the MOUTH. The ear — push-to-talk, the assistant, the cook-along's "next" — has a sibling
that runs the same way, on the same package, for $0: [docs/deploy-moonshine.md](deploy-moonshine.md).
The two are chosen separately (`Speech:Provider` and `Speech:Ear`), so a box can move one before the
other; with both on it needs no ElevenLabs key at all.

## What talks to what

```
browser ──plays WAV──► ShelfAware app ──in-process──► Kokoro (sherpa-onnx + ONNX Runtime)
                          (server-side)                 ~150 MB of model files on disk
```

Nothing leaves the box, and nothing listens on a port. The browser is served the audio by the app,
same-origin, exactly as with ElevenLabs — **no browser CSP change** is needed. A **cache hit needs no
model at all** (clips are content-addressed on disk), so seeded/demo recipes read even before the model
has ever been loaded.

## Requirements

- **RAM: ~600 MB resident while the model is loaded**, on top of the app. On a 2 GB droplet that is
  workable but not roomy, so **add a 2 GB swap file** (step 1). On a 1 GB box, keep ElevenLabs.
  The model loads on the **first read-aloud**, not at boot, and stays loaded after that — a box that
  never reads a recipe never pays the RAM.
- **CPU: synthesis is roughly real-time on a multi-core box, and about 2.4× real time on one core.**
  Measured on a 4-core development box with the int8 model: **1.39× real time at one thread, 1.08× at
  two, 0.96× at four**. Re-measured on **linux-x64** at the shipped default of two threads: **1.4× on
  four cores, 2.4× pinned to a single core** (7.4 s of audio in 17.6 s, model load excluded). So on a
  **1-vCPU droplet, budget roughly 2–2.5× real time**: a ten-second step takes about twenty-five
  seconds the first time it is read. The narration streams (the intro plays while later steps
  synthesize) and every clip is cached forever, so this is a first-read cost per step, not a per-read
  one.

  ⚠️ **On a public demo box that amortization does not happen.** The clip cache is per household and a
  visitor arrives with an empty one, so *every* visitor pays the full first-read cost on *every* step
  they hear — the ElevenLabs case ("slow once, then instant for everyone") is the family box's case,
  not the demo's. On a 1-vCPU droplet that is a visibly laggy first read, which is the honest trade for
  $0 and no key; a 2-vCPU box roughly halves it, and is the cheaper fix than going back to a metered
  voice.
- **Disk: ~152 MB** for the quantized model (below), or ~330 MB for full precision.
- **No runtime dependencies to install.** The native library rides in the app's own publish output —
  `libonnxruntime.so` (26 MB) and `libsherpa-onnx-c-api.so` (5 MB), so **~31 MB on the publish**. A
  RID-specific publish (which both deploy scripts do) carries only the platform it is building for,
  not the other eight.

## 1. Add swap first

A 2 GB droplet needs the cushion; skip if you already have swap or ≥ 4 GB RAM.

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab   # survive reboots
free -h   # confirm Swap: 2.0Gi
```

## 2. Unpack a model

The models are sherpa-onnx's own packaging of Kokoro — the archive already contains the ONNX weights,
the voice embeddings, the token table and the `espeak-ng-data` directory.

Steps 1–5 are the **droplet**. The family box is Windows and keeps its files somewhere else — see
[The family box (Windows)](#the-family-box-windows) below, which is the same steps in its idiom.

As root, which is what the droplet's SSH session is. ⚠️ **Paste the block whole** — every step is
joined to the next with `&&`, so an archive that fails its checksum is deleted and nothing after it runs.
The same shape, and the reasons for each part, as [deploy-moonshine.md](deploy-moonshine.md) step 1.

```bash
# A minimal Ubuntu image ships no bzip2, and GNU tar shells out to it to read a .tar.bz2.
# Checked against the hash measured for this archive -- the same one the CI bootstrap checks
# (.github/workflows/deploy-droplet.yml) -- because it sits on a mutable release tag and is unpacked
# as root. Downloaded and unpacked in a fresh root-only directory, moved in only once verified, whole
# and root-owned; why each of those matters is in deploy-moonshine.md step 1.
{ command -v bzip2 >/dev/null || { apt-get update && apt-get install -y bzip2; }; } \
  && M=/var/lib/shelfaware/models && V=kokoro-int8-en-v0_19 \
  && mkdir -p "$M" && cd -P "$M" \
  && { { [ "$(readlink "/proc/$$/cwd")" = "$M" ] && [ "$(stat -c %u .)" = 0 ]; } \
       || { echo "$M is not a root-owned directory at that path; not installing into it."; false; }; } \
  && { { [ ! -e "./$V" ] && [ ! -L "./$V" ]; } || { echo "$V is already installed in $M."; false; }; } \
  && T=$(mktemp -d) \
  && curl -fsSL --proto '=https' -o "$T/$V.tar.bz2" \
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$V.tar.bz2" \
  && { echo "c9f0dd393615805b0bab050c340834d5e684e732aec91c0e860cd30e982c08bd  $T/$V.tar.bz2" | sha256sum -c - \
       || { rm -rf "$T"; false; }; } \
  && tar xjf "$T/$V.tar.bz2" -C "$T" --no-same-owner --no-same-permissions \
  && chown -R root:root "$T/$V" && chmod -R a+rX "$T/$V" \
  && mv -T "$T/$V" "./$V" \
  && rm -rf "$T" \
  && ls "./$V"
# model.int8.onnx  voices.bin  tokens.txt  espeak-ng-data/  README.md  LICENSE
```

| Archive | Download | On disk | Voices | Notes |
|---|---|---|---|---|
| `kokoro-int8-en-v0_19` | 103 MB | 152 MB | 11 | **The default.** English, quantized — the one worth running on a small box. |
| `kokoro-en-v0_19` | 320 MB | ~330 MB | 11 | Same voices, full precision. Set `Speech__Kokoro__ModelFile=model.onnx`. |
| `kokoro-int8-multi-lang-v1_1` | 147 MB | ~200 MB | 50+ | Kokoro v1.1, many languages. **Not wired up** — it needs lexicon and dictionary paths this app does not pass. Don't point at it expecting it to work. |

> ⚠️ **A wrong path does not produce an error message — it kills the process.** The native library
> answers a missing model file by printing one line to stderr and exiting with a SIGSEGV. The app
> therefore checks all four parts **at startup** and refuses to boot with a message naming what's
> missing, which is the failure you want. Don't work around that check.

## 3. Prove it speaks, before pointing the app at it

Run the app's own synthesis path against the model directory:

```bash
dotnet run --project tools/VoiceCheck -- kokoro /var/lib/shelfaware/models/kokoro-int8-en-v0_19 /tmp/kokoro-check.wav
```

⚠️ **That line wants an SDK and a checkout, and the droplet is deliberately given neither** — the app
ships self-contained precisely so the box needs no .NET install ([deploy.ps1](../deploy/deploy.ps1)).
Don't install one to run a smoke test. Publish the check the same way the app is published, from the
machine you deploy from, and send it up (~122 MB, delete it afterwards):

```powershell
dotnet publish tools\VoiceCheck -c Release -r linux-x64 --self-contained -o $env:TEMP\kcheck
tar -czf $env:TEMP\kcheck.tar.gz -C $env:TEMP\kcheck .
scp $env:TEMP\kcheck.tar.gz root@<droplet>:/tmp/
```

```bash
mkdir -p /tmp/kcheck && tar -xzf /tmp/kcheck.tar.gz -C /tmp/kcheck && chmod +x /tmp/kcheck/VoiceCheck
/tmp/kcheck/VoiceCheck kokoro /var/lib/shelfaware/models/kokoro-int8-en-v0_19 /tmp/kokoro-check.wav
rm -rf /tmp/kcheck /tmp/kcheck.tar.gz      # it carries its own copy of the 26 MB runtime
```

It is the same binary the app uses, so what it proves about the box is what the app will do. `scp` the
WAV back to listen to it — a droplet has no sound card.

It prints the load time, the cache fingerprint and how long the synthesis took, and writes a WAV.
**Play it.** The voice should read "Shelf Aware is talking. Sear the chicken six to seven minutes per
side, then roast at three hundred and fifty degrees Fahrenheit" — if the numbers and `°F` come out
spelled like that, `SpeechText` is reaching the model too.

Pass a voice index as a third argument to audition the others: `… /tmp/v3.wav 3`. The archive ships no
name table, so the voices are numbered 0 to 10 rather than named; listen and pick.

## 4. Point the app at it

In the box's env file (`/etc/shelfaware/env` — see [`deploy/env.example`](../deploy/env.example)):

```
Speech__Provider=Kokoro
Speech__Kokoro__ModelDirectory=/var/lib/shelfaware/models/kokoro-int8-en-v0_19
Speech__Kokoro__SpeakerId=0
Speech__Kokoro__Speed=0.9
Speech__Kokoro__NumThreads=2
```

Then restart the app: `sudo systemctl restart shelfaware`.

> **Changing the voice retires old clips automatically.** The archive, the ONNX file, the voice and the
> speed are all in the cache fingerprint, so switching any of them re-synthesizes rather than serving
> yesterday's voice. You do **not** need to clear `tts-cache`. Where the model is *unpacked* is
> deliberately **not** in the fingerprint — moving the folder doesn't change how it sounds, and
> re-synthesizing a household's whole cookbook because a path changed would be a real cost for nothing.

> ⚠️ **Clips are WAV, which is about ten times the size of the MP3 the sidecar returned.** 16-bit mono
> at 24 kHz is ~48 KB per spoken second, so the default `Speech__CacheMegabytes=256` holds roughly 90
> minutes of speech per household rather than fifteen hours. Raise it if a household's cookbook is
> large; the trim is per household and runs at startup.

## 5. Verify end to end

1. Open a recipe → **Read it to me**. The first read loads the model and synthesizes; a re-read is
   instant (cache).
2. `journalctl -u shelfaware -f` should show `Loaded Kokoro from … : 11 voice(s) at 24000 Hz`, then
   `Synthesizing N character(s) with Kokoro (voice 0)` and `Synthesized N.Ns of audio` — and no
   ElevenLabs call.

## The family box (Windows)

Same model, same settings, different furniture: no systemd, no `/var/lib`, and a publish script that
rebuilds the server folder from scratch every time. **Where the model goes is therefore not a matter
of taste** — put it in the wrong place and the next publish moves it out from under the app.

### Where it goes, and why there

```
C:\Users\Jorcu\ShelfAware-server\app-data\models\kokoro-int8-en-v0_19
```

Under `app-data`, not beside it. [`deploy/publish-family.ps1`](../deploy/publish-family.ps1) renames the
live folder aside and lays a fresh publish down, and **`app-data` is the one thing it moves across**;
every other item at the server root that the new publish doesn't account for is swept into
`ShelfAware-server-attic`. So a `models\` folder at the root would survive exactly one deploy: the next
publish would attic it, the app would then refuse to boot on a model directory that isn't there, and the
script's 90-second poll would report the deploy as failed. Inside `app-data` it rides along, the way the
databases and the speech cache do.

The same reasoning gives the answer for a **development checkout**: `src\ShelfAware.Web\app-data\models\`
— `app-data` is gitignored there, so a 152 MB model can never be committed by accident.

### Unpack it

Windows 10 and 11 ship both `curl.exe` and `tar` (bsdtar), so there is nothing to install:

⚠️ **Paste the block whole**: it is one `& { … }` script block so that a `throw` stops *all* of it —
pasted as loose lines, a failed checksum would stop only its own line and the next would unpack the
archive anyway. Why each part is there: [deploy-moonshine.md](deploy-moonshine.md) step 2.

```powershell
& {
  $ErrorActionPreference = 'Stop'
  $models  = "$env:USERPROFILE\ShelfAware-server\app-data\models"
  $name    = 'kokoro-int8-en-v0_19'
  $archive = "$models\$name.tar.bz2"
  $staging = "$models\.staging"
  if (Test-Path "$models\$name") { throw "$name is already installed in $models." }
  New-Item -ItemType Directory -Path $models -Force | Out-Null
  curl.exe -fL --proto '=https' -o $archive "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$name.tar.bz2"
  if ($LASTEXITCODE -ne 0) { throw "The download failed (curl exit $LASTEXITCODE)." }
  if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne 'c9f0dd393615805b0bab050c340834d5e684e732aec91c0e860cd30e982c08bd') {
    Remove-Item $archive
    throw "$name did not match its recorded sha256 -- not unpacked."
  }
  Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $staging | Out-Null
  & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $staging
  if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $archive." }
  Move-Item "$staging\$name" "$models\$name"
  Remove-Item $staging, $archive -Recurse -Force
  Get-ChildItem "$models\$name"   # model.int8.onnx  voices.bin  tokens.txt  espeak-ng-data\
}
```

⚠️ **`curl.exe`, with the extension, not `curl`.** In Windows PowerShell `curl` is an *alias for
`Invoke-WebRequest`*, which is a different program with different switches — `curl -L …` typed at a
PowerShell prompt gets you "A parameter cannot be found that matches parameter name 'L'" rather than a
download. (Step 2's Linux block will not even parse there: Windows PowerShell has no `&&`.) Spelling out
`curl.exe` bypasses the alias and runs the real curl, where `-f`, `-L` (follow the redirect GitHub
answers a release download with) and `-o` mean what they do everywhere else.

`Invoke-WebRequest -OutFile` works too, and needs no `-L` because it follows redirects on its own. If you
use it, set `$ProgressPreference = 'SilentlyContinue'` first — its progress bar re-renders per chunk and
can turn a 103 MB download into a several-minute one. Swap it in for the `curl.exe` line *inside* the
block, so the checksum still stands between the download and the unpack.

The block names `System32\tar.exe` rather than a bare `tar`: with Git's Unix tools on the PATH, `tar`
can resolve to GNU tar, which reads `C:\…` as a remote host named `C` and fails. Windows' own is bsdtar,
and the build shipped with Windows 11 (3.8.8) reads `.bz2`. If an older one turns out not to, 7-Zip
unpacks it in two passes (`.tar.bz2` → `.tar` → the folder) — after the checksum, not instead of it.
Either way what must end up on disk is a `kokoro-int8-en-v0_19` directory containing those four things —
the app checks all four by name and refuses to boot if any is missing.

### Prove it speaks — before the app is told about it

From the repo checkout, against the folder you just unpacked:

```powershell
dotnet run --project tools/VoiceCheck -- kokoro `
  "$env:USERPROFILE\ShelfAware-server\app-data\models\kokoro-int8-en-v0_19" `
  "$env:TEMP\kokoro-check.wav"
```

Play the WAV. This is step 3 above and it matters more here than on the droplet, because the family box
is the one with a family on it.

### Point the app at it

The family box's settings live in **`C:\Users\Jorcu\ShelfAware-server\appsettings.json`** — the box's
own file, which `publish-family.ps1` carries across every publish *over* the one in the publish output.
Editing the repo's `src/ShelfAware.Web/appsettings.json` does **not** reach it; that copy is overwritten
on arrival. Add to the box's file:

```jsonc
  "Speech": {
    "Provider": "Kokoro",
    "Kokoro": {
      "ModelDirectory": "C:\\Users\\Jorcu\\ShelfAware-server\\app-data\\models\\kokoro-int8-en-v0_19",
      "SpeakerId": 0,
      "Speed": 0.9,
      "NumThreads": 2
    }
  }
```

⚠️ **Backslashes are escaped in JSON** (`\\`), or use forward slashes — `C:/Users/...` works fine and is
harder to get wrong. A path JSON reads as something else is a path the app refuses to boot on.

⚠️ **Unpack first, flip the setting second.** The app refuses to start when it is told to use a model
that isn't on disk — deliberately, because the alternative is a SIGSEGV on the first read-aloud — so a
box that gets the setting before the files is a box that won't come back up, and `publish-family.ps1`
will report the deploy as failed while the site stays down.

Restart is the scheduled task rather than systemd — and ⚠️ **`Stop-ScheduledTask` does not reliably
stop this app.** The boot-launched process outlives the task engine's control of it, so the task reports
stopped while the old exe runs on, still serving the old settings and looking for all the world like the
change didn't take. `publish-family.ps1` force-kills it by path for exactly this reason; do the same by
hand:

```powershell
$exe = "$env:USERPROFILE\ShelfAware-server\ShelfAware.Web.exe"
Stop-ScheduledTask -TaskName 'ShelfAware Server'
# Match on Path, not name: a dev server running from the repo is also ShelfAware.Web.
Get-Process ShelfAware.Web -ErrorAction SilentlyContinue |
  Where-Object { $_.Path -eq $exe } | Stop-Process -Force
Start-ScheduledTask -TaskName 'ShelfAware Server'
```

Then verify as in step 5, reading the app's own log rather than `journalctl`. If the app is **not**
answering afterwards, the settings are the first place to look: a model directory it can't read is a
refusal to boot with the reason on stderr, which on this box means Task Scheduler's history rather than
a console.

### On a development checkout

Nothing about the voice needs committing to try it. `Speech:Provider` and `Speech:Kokoro:ModelDirectory`
bind from user-secrets in Development like any other setting, so the machine-specific path stays on the
machine:

```powershell
dotnet user-secrets --project src/ShelfAware.Web set "Speech:Provider" "Kokoro"
dotnet user-secrets --project src/ShelfAware.Web set "Speech:Kokoro:ModelDirectory" "$PWD/src/ShelfAware.Web/app-data/models/kokoro-int8-en-v0_19"
```

Deliberately not `appsettings.Development.json`: that file is committed, so a provider set there would
stop the app booting for anyone who cloned the repo without first downloading 152 MB of model.

## Upgrading a box that ran the HTTP sidecar

The sidecar is gone: `deploy/kokoro.service`, the `ghcr.io/remsky/kokoro-fastapi-cpu` container and the
whole `Speech__Local__*` section no longer exist.

```bash
sudo systemctl disable --now kokoro          # stop the sidecar
sudo rm /etc/systemd/system/kokoro.service && sudo systemctl daemon-reload
sudo docker rmi ghcr.io/remsky/kokoro-fastapi-cpu:v0.8.1
```

Then replace the settings as in step 4. **The app refuses to start while any `Speech__Local__*` key or
`Speech__Provider=Local` is still set**, and says what to use instead — deliberately, because
configuration binding silently drops keys nothing reads, and a box that kept quietly running on
ElevenLabs while its owner believed it was on the free voice would be a bill nobody chose.

The cache does not need clearing: Kokoro clips were already fingerprinted under `kokoro|…`, and the
fingerprint has changed shape, so the old ones are simply never asked for. They will age out of the
size cap on their own, or `rm -rf app-data/tts-cache` if you want the space back today.

## Security recap

- Nothing listens on a port, so there is nothing to firewall and nothing to leak.
- The model files are read-only inputs; the app never writes to the model directory.
- The text being spoken never leaves the box.
