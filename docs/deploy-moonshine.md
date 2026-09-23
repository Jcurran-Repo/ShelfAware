# Moonshine: the ear that runs inside the app

ShelfAware's speech recognition — push-to-talk, the roaming assistant, the cook-along's "next" and
"stop" — can run on [Moonshine](https://github.com/moonshine-ai/moonshine) (MIT) **in the app's own
process**, through [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx). Samples in, text out. There is
**no per-utterance cost, no key, and nothing to meter**, and nothing leaves the box.

This is the ear's half of the bargain [Kokoro](deploy-kokoro.md) made for the mouth, and it is the same
shape for the same reasons: no sidecar, no Docker, no Python, nothing to install beside the app. The
model is a directory. The two are chosen **separately** (`Speech:Ear` and `Speech:Provider`), so a box
can move one before the other.

> **Status:** verified on linux-x64 through `tools/MoonshineCheck`, which is the app's own transcription
> path — `SherpaMoonshineEngine` loading the model and `MoonshineSpeechToText` turning a WAV into text.
> It has **not yet run on the droplet or the family box**; run the check in step 3 there before flipping
> the app over.

## Why Moonshine rather than Whisper

Measured, not assumed (2026-09-21, linux-x64, int8, two threads; "one core" is `taskset -c 0`, standing
in for a 1-vCPU droplet):

| model | 1.5 s command | 7.4 s sentence | one core: command / sentence | peak RSS | on disk |
|---|---|---|---|---|---|
| **Moonshine tiny en** | 30 ms | 218 ms | **162 ms / 689 ms** | 237 MB | 119 MB |
| Whisper tiny.en | 207 ms | 639 ms | 458 ms / 1439 ms | 287 MB | 99 MB |

Faster everywhere, and more accurate on the sentences this app actually hears: Moonshine transcribed
"Sear the chicken 6-7 minutes per side" where Whisper heard "See her the chicken". Moonshine is built
for short-form speech, which is what a cook says to a microphone.

**The ear is far cheaper than the mouth.** The same box needs ~17 s to *say* that sentence with Kokoro
and 0.7 s to *hear* it. If you are budgeting a small box, synthesis is the cost; recognition is a
rounding error.

## Requirements

- **RAM: ~237 MB peak** measured standalone. Most of that is ONNX Runtime, which a box already running
  Kokoro has loaded, so the marginal cost of adding the ear to a Kokoro box is smaller than the number
  suggests — but on a 2 GB droplet budget for both and keep the 2 GB swap file
  ([deploy-kokoro.md](deploy-kokoro.md) step 1).
- **CPU: a fraction of real time**, even on one core (table above). Unlike synthesis, this is not a
  user-visible wait.
- **Disk: ~119 MB** for the int8 archive.
- **No runtime dependencies to install.** The native library is already in the publish output for
  Kokoro (`libonnxruntime.so`, `libsherpa-onnx-c-api.so`) — the ear adds **nothing** to it, because it
  is the same package.
- **The model loads on the first spoken word**, not at boot, and stays loaded. A box nobody talks to
  never pays the RAM.

## 1. Unpack a model

As root, which is what the droplet's SSH session is. ⚠️ **Paste the block whole.** Every step is joined
to the next with `&&`, and that is load-bearing, not style: `sha256sum -c` on a line of its own prints
`FAILED` and returns 1, and a pasted block runs the next line anyway — so an archive that failed its
check would be unpacked, as root, with the only sign a few lines back up the scrollback. The deploy's
`bootstrap` exits on a mismatch; this does the same.

```bash
# A minimal Ubuntu image ships no bzip2, and GNU tar shells out to it to read a .tar.bz2.
# Release assets sit on a mutable tag, so the archive is checked against the hash measured on
# 2026-09-21 -- the same one the CI bootstrap checks (.github/workflows/deploy-droplet.yml).
# Downloaded and unpacked in a fresh root-only directory (mktemp -d), and moved into the models
# directory only once it is verified, whole, and root-owned: /var/lib/shelfaware is the service
# account's home, so root writes nothing there that the app could have prepared a path for, and "is
# the directory there" -- the check everything else makes -- never sees a half-extracted model.
# That same check stops a re-run up front, where mv would otherwise nest a second copy in the first.
# Root-owned, world-readable: the app READS its model and never rewrites it -- install.sh's posture
# for the binaries, for the same reason.
{ command -v bzip2 >/dev/null || { apt-get update && apt-get install -y bzip2; }; } \
  && M=/var/lib/shelfaware/models && V=sherpa-onnx-moonshine-tiny-en-int8 \
  && { { [ ! -e "$M/$V" ] && [ ! -L "$M/$V" ]; } || { echo "$V is already installed in $M."; false; }; } \
  && mkdir -p "$M" && T=$(mktemp -d) \
  && curl -fsSL --proto '=https' -o "$T/$V.tar.bz2" \
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$V.tar.bz2" \
  && { echo "d5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2  $T/$V.tar.bz2" | sha256sum -c - \
       || { rm -rf "$T"; false; }; } \
  && tar xjf "$T/$V.tar.bz2" -C "$T" --no-same-owner --no-same-permissions \
  && chown -R root:root "$T/$V" && chmod -R a+rX "$T/$V" \
  && mv "$T/$V" "$M/" \
  && rm -rf "$T" \
  && ls "$M/$V"
# preprocess.onnx  encode.int8.onnx  uncached_decode.int8.onnx  cached_decode.int8.onnx  tokens.txt
```

`-f` matters as much as the hash: without it curl writes a GitHub error page to the archive's name and
exits 0. The hash would still catch that — but only because it is now joined to what follows it.

The `sherpa-onnx-moonshine-base-en-int8` archive is the larger sibling (~400 MB) — more accurate, and
not worth it on a small box for "next" and "stop".

> ⚠️ **A wrong path does not produce an error message — it kills the process.** Exactly as with Kokoro:
> the native library answers a missing model file by printing one line to stderr and exiting with a
> SIGSEGV. The app therefore checks all five parts **at startup** and refuses to boot with a message
> naming what's missing. Don't work around that check.

## 2. On the family box (Windows)

Same model, and the same rule about *where*: under `app-data`, because that is the one thing
`publish-family.ps1` carries across the folder swap.

Checked against the same hash, for the same reason — the family box holds real households' data too.
⚠️ **Paste the block whole**: it is one `& { … }` script block so that a `throw` stops *all* of it. Pasted
as loose lines, a failed check would stop only its own line and the next one would unpack the archive
anyway — the PowerShell form of the trap the Linux block avoids with `&&`.

```powershell
& {
  $ErrorActionPreference = 'Stop'
  $models  = "$env:USERPROFILE\ShelfAware-server\app-data\models"
  $name    = 'sherpa-onnx-moonshine-tiny-en-int8'
  $archive = "$models\$name.tar.bz2"
  $staging = "$models\.staging"
  # Already there means already done -- and moving a second copy onto it would nest it inside instead.
  if (Test-Path "$models\$name") { throw "$name is already installed in $models." }
  New-Item -ItemType Directory -Path $models -Force | Out-Null
  # -f so an HTTP error page is a failure, not a file; --proto so a redirect cannot leave HTTPS.
  curl.exe -fL --proto '=https' -o $archive "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$name.tar.bz2"
  if ($LASTEXITCODE -ne 0) { throw "The download failed (curl exit $LASTEXITCODE)." }
  if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne 'd5fe6ec4334fef36255b2a4010412cad4c007e33103fec62fb5d17cad88086f2') {
    Remove-Item $archive
    throw "$name did not match its recorded sha256 -- not unpacked."
  }
  # Staged, then moved whole, so a half-extracted directory can never read as installed.
  # Windows' own tar (bsdtar, which reads .bz2) by full path: with Git's Unix tools on the PATH a bare
  # `tar` can be GNU tar, which reads "C:\..." as a remote host named C and fails.
  Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $staging | Out-Null
  & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $staging
  if ($LASTEXITCODE -ne 0) { throw "tar could not unpack $archive." }
  Move-Item "$staging\$name" "$models\$name"
  Remove-Item $staging, $archive -Recurse -Force
  Get-ChildItem "$models\$name"
}
```

⚠️ `curl.exe`, with the extension — in Windows PowerShell `curl` is an alias for `Invoke-WebRequest`,
which has no `-L`. Same trap as the Kokoro doc's step 2.

## 3. Prove it hears, before pointing the app at it

```powershell
dotnet run --project tools/MoonshineCheck -- `
  "$env:USERPROFILE\ShelfAware-server\app-data\models\sherpa-onnx-moonshine-tiny-en-int8" `
  "$env:TEMP\kokoro-check.wav"
```

It prints the load time, how long the transcription took, and **what it heard**. Hand it the WAV
`tools/VoiceCheck` wrote and the two tools together test the whole voice stack: the mouth says a
sentence, the ear reads it back.

On the **droplet** there is no .NET SDK on purpose, so publish the check and send it up — the same
recipe as [deploy-kokoro.md](deploy-kokoro.md) step 3, with `tools\MoonshineCheck` in place of
`tools\VoiceCheck`.

## 4. Point the app at it

```
Speech__Ear=Moonshine
Speech__Moonshine__ModelDirectory=/var/lib/shelfaware/models/sherpa-onnx-moonshine-tiny-en-int8
Speech__Moonshine__NumThreads=2
```

Then restart: `sudo systemctl restart shelfaware`. On the family box the `Speech` block goes in the
**box's own** `appsettings.json`, not the repo's copy, and **after** the publish — both traps are
written up in [deploy-kokoro.md](deploy-kokoro.md).

Unset, `Speech:Ear` is `ElevenLabs`, so no existing deployment changes on upgrade.

## 5. Verify end to end

1. `journalctl -u shelfaware -f` → speak to the assistant. The log shows
   `Loaded Moonshine from … : 2 thread(s)`, then `Transcribing N.NNs of audio at 16000 Hz with
   Moonshine` and `Transcribed N character(s)` — and **no ElevenLabs call**.
2. A cook-along "next" should advance the step. That is the whole feature in one word.

## What this does and doesn't replace

- ✅ **Push-to-talk, the roaming assistant, cook-along commands** — all of them go through
  `ISpeechToText`, so all of them move together.
- ❌ **The ElevenLabs realtime "Live agent"** (`Voice:LiveAgentEnabled`, default off) is a different
  thing: a conversational agent hosted by ElevenLabs, billed per minute, which does its own listening.
  It is unaffected and stays off.
- With `Speech:Provider=Kokoro` **and** `Speech:Ear=Moonshine`, a box needs **no ElevenLabs key at
  all** — `ElevenLabs__ApiKey` can be removed entirely.

## The browser sends PCM now

Worth knowing if you are debugging a capture: the three capture scripts
(`voice.js`, `cooklisten.js`, `conversation.js`) convert a recording to **16 kHz mono WAV** via
[`pcm.js`](../src/ShelfAware.Web/wwwroot/js/pcm.js) before handing it to the server. A model in this
process has no codec, and decoding opus server-side would mean ffmpeg — a second thing to install,
which is the whole property this shape exists to keep. A browser can always decode what it just
recorded, so the conversion is free where it happens.

If that conversion fails, the original compressed bytes are sent instead: a cloud ear still reads them,
and the local ear answers with "Couldn't read that audio" and a log line naming the container. So a
box seeing that message has a capture path that didn't convert, not a broken model.
