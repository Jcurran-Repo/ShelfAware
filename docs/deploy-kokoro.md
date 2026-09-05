# Kokoro voice: the self-hosted read-aloud sidecar

ShelfAware's read-aloud (recipe steps, the chat's spoken confirmations) can run on a **local,
self-hosted TTS sidecar** instead of ElevenLabs. It's [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M)
— Apache-2.0, so free even commercially — served behind an OpenAI-compatible HTTP API on the box. The
app just POSTs text and gets audio back, so there's **no per-character cost, no key, and nothing to
meter.** That is why the managed demo box uses it.

The app change is one setting (`Speech:Provider=Local`); this doc is the other half — standing up the
sidecar. The two are independent: flip the setting only once the sidecar answers.

> **Status:** the app side (provider seam, `LocalTextToSpeech`, tests) is built and CI-green. The image
> name (`ghcr.io/remsky/kokoro-fastapi-cpu`), tag (`v0.8.1`), port (8880) and endpoint
> (`/v1/audio/speech`) are **verified against the Kokoro-FastAPI project**, but the end-to-end run has
> **not yet happened on a live box** — the first deploy is the first real test, so watch
> `journalctl -u kokoro -f` and the smoke-test in step 3 before flipping the app over.

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

- **RAM: 2 GB is TIGHT — add swap.** Kokoro-82M is CPU-capable (no GPU), but the model + ONNX runtime +
  Python resident set runs ~1–1.5 GB under load, and Reginald (.NET) + Caddy + the OS want the rest of a
  2 GB droplet. It works, but a synthesis spike can OOM without a cushion, so **add a 2 GB swap file**
  (step 0 below). If the box still struggles, bump the droplet to 4 GB. On a 1 GB box, keep ElevenLabs.
- Docker (recommended) **or** a Python venv. Docker bundles the phonemizer/espeak-ng dependencies the
  pip path otherwise needs, so it's the simpler path on a fresh droplet.

## Option A — Docker (recommended)

**0. Add swap first** (a 2 GB droplet needs the cushion; skip if you already have swap or ≥ 4 GB RAM):

   ```bash
   sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
   sudo mkswap /swapfile && sudo swapon /swapfile
   echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab   # survive reboots
   free -h   # confirm Swap: 2.0Gi
   ```

1. **Install Docker** (skip if it's already there — `docker --version`):

   ```bash
   curl -fsSL https://get.docker.com | sudo sh
   ```

2. **Install the unit** — [`deploy/kokoro.service`](../deploy/kokoro.service) already pins the verified
   image (`ghcr.io/remsky/kokoro-fastapi-cpu:v0.8.1`) and runs it loopback-bound with
   `--cap-drop=ALL --security-opt=no-new-privileges`. Bump the tag when a newer release lands
   ([project releases](https://github.com/remsky/Kokoro-FastAPI/releases)); add `--read-only` (plus a
   writable `--tmpfs`) if you want to harden further. Then:

   ```bash
   sudo cp deploy/kokoro.service /etc/systemd/system/kokoro.service
   sudo systemctl daemon-reload
   sudo systemctl enable --now kokoro
   ```

   The first start pulls the image and auto-downloads the model — give it a minute or two. Watch it:
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
