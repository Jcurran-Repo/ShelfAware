# Piper — the fast local voice

The second in-process mouth, and the one a small shared CPU can actually afford. Same shape as
[Kokoro](deploy-kokoro.md): a directory of model files on disk, no key, no port, no sidecar, `$0` per
read. It runs through the same `SherpaTtsEngine` the Kokoro path does — the two differ only in which
files they expect and which block of sherpa-onnx's config names them.

**Read [deploy-kokoro.md](deploy-kokoro.md) first if you haven't.** Everything it says about the
native library, the SIGSEGV on a bad path, and unpacking the model *before* flipping the setting
applies here unchanged, and is not repeated.

## Why this exists

Kokoro is the better voice. It is also, on modest hardware, unusably slow. Measured on the same
sentence, same two threads, same pinned cores:

| | rate | model load | on disk |
|---|---|---|---|
| Kokoro-82M int8 | 1.37× real time | ~1.2 s | 152 MB |
| Piper `en_US-lessac-medium` | **0.05× real time** | ~0.9 s | 65 MB |

On the demo droplet (DigitalOcean *Regular*, 2 vCPU, no AVX-512 VNNI) Kokoro measured **3.1× real
time** — an 8.9-second reply cost 28 seconds of silence before a single word. Piper's *medium* voices
on the same box measure **0.12–0.19×** once the model is loaded (ryan-medium, lessac-medium,
2026-09-23). ⚠️ Its *high* voices do not: ryan-high measured **0.8–0.95×** there — see
[voice-bakeoff.md](voice-bakeoff.md#a-worked-example-the-voice-that-nearly-shipped-at-the-line).

⚠️ **The threshold that matters is 1.0×, not the ratio.** Below it synthesis outruns playback, so a
reply can start speaking while the rest of it is still being made. Above it, every sentence arrives
later than the one before it finished, and chunking the playback buys a shorter first wait followed by
stuttering. That is why "just stream it" was not the fix on its own.

The cost is warmth: Piper is clear and noticeably flatter. On the demo box that is the right trade,
because the alternative a visitor actually experiences is silence.

## Which box runs which

- **Demo box** — Piper, voice `ryan-medium`. It is the employer-facing one and it runs on a shared core.
- **Family box** — Kokoro. Real hardware, and the people using it would rather have the better voice.

Both are unpacked by the droplet deploy's `bootstrap` step, so switching is one line in
`/etc/shelfaware/env` and a restart, with no deploy and no download.

## 1. Unpack the model

The droplet deploy does this for you (`.github/workflows/deploy-droplet.yml`, `bootstrap: true`).
By hand:

⚠️ **Paste the block whole.** Every step is joined to the next with `&&`, and that is load-bearing:
`sha256sum -c` on a line of its own prints `FAILED` and returns 1, and a pasted block runs the next line
anyway — so an archive that failed its check would be unpacked, as root, with the only sign four lines
back up the scrollback. The deploy's `bootstrap` exits on a mismatch; this has to do the same.

```bash
# The same shape as the deploy's bootstrap, and for its reasons (.github/workflows/deploy-droplet.yml):
# /var/lib/shelfaware is the service account's home, so the app can re-point models/ at any moment --
# models/ is pinned by INODE (cd, then the kernel's /proc/$$/cwd must be exactly that path, root-owned),
# and everything after is relative to ".". Downloaded and unpacked in a root-only directory inside it,
# and moved in only once verified, whole and root-owned -- one rename on one filesystem, so a
# half-extracted model can never sit under the name everything else checks.
{ command -v bzip2 >/dev/null || { apt-get update && apt-get install -y bzip2; }; } \
  && M=/var/lib/shelfaware/models && V=vits-piper-en_US-ryan-medium \
  && mkdir -p "$M" && cd -P "$M" \
  && { { [ "$(readlink "/proc/$$/cwd")" = "$M" ] && [ "$(stat -c %u:%a .)" = 0:755 ]; } \
       || { echo "$M is not a root-owned 755 directory at that path; not installing into it."; false; }; } \
  && { { [ ! -e "./$V" ] && [ ! -L "./$V" ]; } || { echo "$V is already installed in $M."; false; }; } \
  && T=$(mktemp -d ./.incoming.XXXXXX) \
  && curl -fsSL --proto '=https' -o "$T/$V.tar.bz2" \
    "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/$V.tar.bz2" \
  && { echo "c546af78b6395b4e7c4ce1ed899438b64426a362f5d4ec5fecd090ded9ad7505  $T/$V.tar.bz2" | sha256sum -c - \
       || { rm -rf "$T"; false; }; } \
  && tar xjf "$T/$V.tar.bz2" -C "$T" --no-same-owner --no-same-permissions \
  && chown -R root:root "$T/$V" && chmod -R a+rX "$T/$V" \
  && mv -T "$T/$V" "./$V" \
  && rm -rf "$T" \
  && { [ "$(readlink "/proc/$$/cwd")" = "$M" ] \
       || { echo "$M was re-pointed during the install; the model went into the directory it used to name."; false; }; } \
  && ls "./$V"
```

The checksum is the archive as measured on 2026-09-23, computed from the download on the droplet
itself. It sits on a mutable release tag, so "the bytes we measured" and "whatever that URL serves
today" are different promises — which is the whole reason the line is there.

A complete directory holds exactly three things the app cares about:

```
vits-piper-en_US-ryan-medium/
  en_US-ryan-medium.onnx       # the weights, named after the voice -- and found by the directory's name
  tokens.txt
  espeak-ng-data/              # a DIRECTORY -- this is what lets "no system espeak-ng" hold
```

There is no `voices.bin`: a Piper archive carries its speakers inside the weights, which is why its
descriptor has three paths where Kokoro's has four.

## 2. Prove it speaks, before pointing the app at it

```bash
dotnet run --project tools/VoiceCheck -- piper \
  /var/lib/shelfaware/models/vits-piper-en_US-ryan-medium /tmp/piper-check.wav
```

It loads the model through the app's own engine, says a sentence with numbers and a unit abbreviation
in it, writes a WAV, and prints **the rate against real time measured on that box** — twice: the first
read, which also loads the model, and a second once it is loaded. ⚠️ **The second is the one to hold
against 1.0×**; on a clip this short the first is mostly load, which is how ryan-high once looked like
a modest step on a desktop. Play the WAV.

That line wants an SDK and a checkout, and the droplet has neither: publish the tool self-contained and
copy it over, as [deploy-kokoro.md](deploy-kokoro.md) step 3 shows. That is how every droplet figure on
this page was measured.

⚠️ Run this **before** step 3, not after. sherpa-onnx answers a missing or unreadable model file by
printing one line to stderr and killing the process with a SIGSEGV: no managed exception, nothing to
catch, nothing of ours in the log. The app refuses to boot against an incomplete directory for exactly
that reason, and a refusal at boot is a much better day than a service that will not start.

## 3. Turn it on

In the box's own environment file (`/etc/shelfaware/env` on the droplet), **not** the repo's
`appsettings.json` — a `Speech` block in the repo's copy binds to nothing on a deployed box and falls
back silently:

```
Speech__Provider=Piper
Speech__Piper__ModelDirectory=/var/lib/shelfaware/models/vits-piper-en_US-ryan-medium
```

Then `systemctl restart shelfaware` and check `/healthz`.

**That one line is the whole voice.** Piper names its weights after the voice, and the app reads that
name off the directory: `vits-piper-<voice>` holds `<voice>.onnx`, and the quantized `-int8`/`-fp16`
builds keep the plain name. `Speech__Piper__ModelFile` is only for an archive that names its weights
some other way — set, it wins. A directory the app cannot read a name from is refused at boot naming
that setting, and so is a worked-out name that is not on disk; neither reaches native code.

⚠️ **Moving a box that already runs Piper to a new voice — two things first.** Run the deploy once
with `bootstrap` ticked, so the new voice's directory exists (a box bootstrapped before it became the
default does not have it). And **delete any `Speech__Piper__ModelFile` line**: an older `env.example`
shipped one naming lessac's file, and left in it wins over the directory — the new directory does not
hold that file, so the app refuses to start, naming it. Both fail loudly at boot, never silently.

This used to take two lines, with the second defaulting to one voice's file. Changing the default voice
would then have stranded every box whose env named the old directory: the deploy lands, the new default
names a file that directory does not hold, and the app will not start.

## Choosing a different voice

`en_US-ryan-medium` is the demo box's voice because it was picked **by ear** in the bake-off
([voice-bakeoff.md](voice-bakeoff.md)) and then **measured on the droplet** at 0.12–0.14× once loaded —
level with `lessac-medium`, the voice it replaced, which stays unpacked so going back is the one line
above. The bake-off is the way to choose another; two things worth knowing first:

- **`vits-piper-en_US-libritts_r-medium`** — one 79 MB archive holding **904 speakers**, selected with
  `Speech__Piper__SpeakerId`. Medium-model speed. Worth it if you want a particular character; like any
  Piper archive it needs only its directory named.
- ⚠️ **`-high` variants are not "proportionally slower" on the droplet — they are at the line.**
  ryan-high measured 0.8–0.95× there against ryan-medium's 0.12–0.14×. Measure with `VoiceCheck` on the
  box that will run it before believing a number from anywhere else, including this page.

⚠️ **Changing the voice retires the cache.** The archive's name, `ModelFile` (as resolved — the
worked-out name, not the blank setting) and `SpeakerId` are all part of the clip fingerprint, deliberately: a clip voiced one way must never be served for a key that now means
another. Old clips are not deleted, they simply stop being found, and the cache trims them in its own
time.

## What this does not change

- **Nothing leaves the box.** Same as Kokoro: no `HttpClient` on the path, no key, no metering, and
  nothing written per household beyond the clip cache that already existed.
- **The ear is separate.** `Speech:Ear` chooses recognition independently — see
  [deploy-moonshine.md](deploy-moonshine.md). A box can move its mouth without touching its ear.
- **ElevenLabs still works.** `Speech:Provider=ElevenLabs` remains the default for any deployment that
  has not opted in, so nothing changes on upgrade.
