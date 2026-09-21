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
time** — an 8.9-second reply cost 28 seconds of silence before a single word. Piper on the same box
lands near 0.1×.

⚠️ **The threshold that matters is 1.0×, not the ratio.** Below it synthesis outruns playback, so a
reply can start speaking while the rest of it is still being made. Above it, every sentence arrives
later than the one before it finished, and chunking the playback buys a shorter first wait followed by
stuttering. That is why "just stream it" was not the fix on its own.

The cost is warmth: Piper is clear and noticeably flatter. On the demo box that is the right trade,
because the alternative a visitor actually experiences is silence.

## Which box runs which

- **Demo box** — Piper. It is the employer-facing one and it runs on a shared core.
- **Family box** — Kokoro. Real hardware, and the people using it would rather have the better voice.

Both are unpacked by the droplet deploy's `bootstrap` step, so switching is one line in
`/etc/shelfaware/env` and a restart, with no deploy and no download.

## 1. Unpack the model

The droplet deploy does this for you (`.github/workflows/deploy-droplet.yml`, `bootstrap: true`).
By hand:

```bash
cd /var/lib/shelfaware/models
curl -fsSL -o piper.tar.bz2 \
  https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-lessac-medium.tar.bz2
echo "9e3febfacf0abf4270172d2958bcec246032b7e88efc2720840cc80c93de334e  piper.tar.bz2" | sha256sum -c -
tar xjf piper.tar.bz2 --no-same-owner --no-same-permissions && rm piper.tar.bz2
chown -R root:root vits-piper-en_US-lessac-medium
chmod -R a+rX vits-piper-en_US-lessac-medium
```

The checksum is the archive as measured on 2026-09-21. It sits on a mutable release tag, so "the bytes
we measured" and "whatever that URL serves today" are different promises — which is the whole reason
the line is there. `tar` shells out to `bzip2`, which a minimal Ubuntu image does not ship; the deploy
installs it first.

A complete directory holds exactly three things the app cares about:

```
vits-piper-en_US-lessac-medium/
  en_US-lessac-medium.onnx     # the weights, named after the voice
  tokens.txt
  espeak-ng-data/              # a DIRECTORY -- this is what lets "no system espeak-ng" hold
```

There is no `voices.bin`: a Piper archive carries its speakers inside the weights, which is why its
descriptor has three paths where Kokoro's has four.

## 2. Prove it speaks, before pointing the app at it

```bash
dotnet run --project tools/VoiceCheck -- piper \
  /var/lib/shelfaware/models/vits-piper-en_US-lessac-medium /tmp/piper-check.wav
```

It loads the model through the app's own engine, says a sentence with numbers and a unit abbreviation
in it, writes a WAV, and prints **the rate against real time measured on that box** — which is the
number that decides whether streaming can keep up there. Play the WAV.

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
Speech__Piper__ModelDirectory=/var/lib/shelfaware/models/vits-piper-en_US-lessac-medium
```

Then `systemctl restart shelfaware` and check `/healthz`.

`Speech__Piper__ModelFile` defaults to `en_US-lessac-medium.onnx`. Any other voice needs it set,
because Piper names its weights after the voice and there is no name that is right for every archive.

## Choosing a different voice

`en_US-lessac-medium` is the default because it is the steadiest of the American English voices at
medium quality. Two alternatives worth knowing:

- **`vits-piper-en_US-libritts_r-medium`** — one 79 MB archive holding **904 speakers**, selected with
  `Speech__Piper__SpeakerId`. Same speed. Worth it if you want a particular character; it needs a
  `ModelFile` of `en_US-libritts_r-medium.onnx`.
- **`-high` variants** of either — better audio, proportionally slower. Measure with `VoiceCheck` on
  the box that will run it before believing a number from anywhere else, including this table.

⚠️ **Changing the voice retires the cache.** `SpeakerId` and `ModelFile` are both part of the clip
fingerprint, deliberately: a clip voiced one way must never be served for a key that now means
another. Old clips are not deleted, they simply stop being found, and the cache trims them in its own
time.

## What this does not change

- **Nothing leaves the box.** Same as Kokoro: no `HttpClient` on the path, no key, no metering, and
  nothing written per household beyond the clip cache that already existed.
- **The ear is separate.** `Speech:Ear` chooses recognition independently — see
  [deploy-moonshine.md](deploy-moonshine.md). A box can move its mouth without touching its ear.
- **ElevenLabs still works.** `Speech:Provider=ElevenLabs` remains the default for any deployment that
  has not opted in, so nothing changes on upgrade.
