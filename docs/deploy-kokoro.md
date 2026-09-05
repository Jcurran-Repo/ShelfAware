# Kokoro voice: the self-hosted read-aloud sidecar

ShelfAware's read-aloud (recipe steps, the chat's spoken confirmations) can run on a **local,
self-hosted TTS sidecar** instead of ElevenLabs. It's [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)
— Apache-2.0, so free even commercially — served behind an OpenAI-compatible HTTP API on the box. The
app just POSTs text and gets audio back, so there's **no per-character cost, no key, and nothing to
meter.** That is why the managed demo box uses it.

The app change is one setting (`Speech:Provider=Local`); this doc is the other half — standing up the
sidecar. The two are independent: flip the setting only once the sidecar answers.

> **Status:** the app side (provider seam, `LocalTextToSpeech`, tests) is built and CI-green. The
> **sidecar steps below are reasoned from Kokoro-FastAPI's documented setup and have not yet been run on
> a live box** — verify the first time you deploy one, and correct anything here that drifted (the image
> tag especially). Same honesty flag as any not-yet-observed deploy step.

## What talks to what

```
browser ──plays MP3──► ShelfAware app ──POST /v1/audio/speech──► Kokoro sidecar (127.0.0.1:8880)
                          (server-side)
```

The **browser never touches the sidecar** — the app calls it server-side and returns the bytes. So:
- Bind the sidecar to **loopback only**. It takes arbitrary text and needs no auth; it must not be
  reachable from the internet. (The unit and the config below both do this.)
- **No browser CSP change** is needed — the audio is same-origin (served by the app), exactly as with
  ElevenLabs today.
- A **cache hit needs no sidecar at all** (clips are content-addressed on disk), so seeded/demo recipes
  read even before the sidecar warms up.

## Requirements

- **~2 GB RAM.** Kokoro-82M is a ~300 MB model plus the runtime; it's CPU-capable (no GPU needed). The
  demo droplet is 2 GB, which fits. On a 1 GB box, keep ElevenLabs.
- Docker (recommended) **or** a Python venv. Docker bundles the phonemizer/espeak-ng dependencies the
  pip path otherwise needs, so it's the simpler path on a fresh droplet.

## Option A — Docker (recommended)

1. **Pin an image tag.** Check the [Kokoro-FastAPI releases](https://github.com/remsky/Kokoro-FastAPI)
   for the current CPU image and pin it — don't ship `:latest` (not reproducible). Edit
   [`deploy/kokoro.service`](../deploy/kokoro.service) to replace `:latest` with that tag.

2. **Install the unit** (it runs the container on `127.0.0.1:8880`, systemd-managed, restart-on-crash):

   ```bash
   sudo cp deploy/kokoro.service /etc/systemd/system/kokoro.service
   # (edit the image tag first, per step 1)
   sudo systemctl daemon-reload
   sudo systemctl enable --now kokoro
   ```

   The first start pulls the image and loads the model — give it a minute. Watch it:
   `journalctl -u kokoro -f`.

3. **Prove the sidecar answers** (still on the box):

   ```bash
   curl -s http://127.0.0.1:8880/v1/audio/speech \
     -H 'content-type: application/json' \
     -d '{"model":"kokoro","input":"Shelf Aware is talking.","voice":"af_heart","response_format":"mp3"}' \
     --output /tmp/kokoro-test.mp3
   # expect a non-empty MP3:
   ls -l /tmp/kokoro-test.mp3 && file /tmp/kokoro-test.mp3
   ```

## Option B — Python venv (no Docker)

Follow Kokoro-FastAPI's own "running without Docker" instructions (a venv + `uvicorn` on `127.0.0.1:8880`),
then wrap that command in a systemd unit modelled on `deploy/kokoro.service` (drop the `docker` lines,
point `ExecStart` at the venv's uvicorn, add `User=`/`WorkingDirectory=`). More moving parts (Python
version, espeak-ng) — prefer Docker unless you can't.

## Point the app at it

In the box's env file (`/etc/shelfaware/env` — see [`deploy/env.example`](../deploy/env.example)):

```
Speech__Provider=Local
Speech__Local__BaseUrl=http://127.0.0.1:8880
Speech__Local__Voice=af_heart
Speech__Local__Speed=0.9
```

Then restart the app: `sudo systemctl restart shelfaware`. Everything else (`Model=kokoro`,
`Format=mp3`, no `ApiKey`) uses the defaults. Pick a different [Kokoro voice](https://github.com/remsky/Kokoro-FastAPI)
(`af_bella`, `am_michael`, …) with `Speech__Local__Voice`.

> **Changing the voice retires old clips automatically.** The voice/model/speed/format are in the cache
> fingerprint, so switching any of them re-synthesizes rather than serving yesterday's voice. You do
> **not** need to clear `tts-cache`.

## Verify end to end

1. Open a recipe → **Read it to me**. The first read synthesizes (watch `journalctl -u kokoro -f`); a
   re-read is instant (cache).
2. `journalctl -u shelfaware -f` should show `Synthesizing … via local TTS (kokoro, voice af_heart)` and
   then `Synthesized N bytes of audio/mpeg` — no ElevenLabs call.

## Security recap

- Loopback bind only (`-p 127.0.0.1:8880:8880`). Never publish the port.
- If you must run a **shared** sidecar (one box serving several), put it behind auth and set
  `Speech__Local__ApiKey=<token>` — the app then sends `Authorization: Bearer <token>`. A single-box
  loopback sidecar needs none.
- The sidecar has no persistence and sees only the recipe/step text the app sends it — no household data,
  no keys.

## Rolling back to ElevenLabs

Remove `Speech__Provider=Local` (or set it to `ElevenLabs`), make sure `ElevenLabs__ApiKey` is present,
and restart the app. Stop the sidecar if unused: `sudo systemctl disable --now kokoro`.
