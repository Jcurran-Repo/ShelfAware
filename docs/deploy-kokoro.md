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

> **Status:** built and CI-green, and verified end to end on a development box — a recipe step was
> synthesized through `KokoroTextToSpeech` and transcribed back to check the words came out. It has
> **not yet run on the droplet or the family box**: the first deploy is the first real test of *those
> boxes'* CPU and RAM. Run the check in step 3 there before flipping the app over.

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
  workable but not roomy, so **add a 2 GB swap file** (step 0). On a 1 GB box, keep ElevenLabs.
  The model loads on the **first read-aloud**, not at boot, and stays loaded after that — a box that
  never reads a recipe never pays the RAM.
- **CPU: synthesis is roughly real-time.** Measured on a 4-core development box with the int8 model:
  **1.39× real time at one thread, 1.08× at two, 0.96× at four** — so a ten-second step takes about ten
  seconds the first time it is read. The narration streams (the intro plays while later steps
  synthesize) and every clip is cached forever, so this is a first-read cost per step, not a per-read
  one. A slower box makes the first read of a long recipe noticeably laggy; that is the honest trade
  for $0.
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

```bash
sudo mkdir -p /var/lib/shelfaware/models && cd /var/lib/shelfaware/models
curl -L -O https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-en-v0_19.tar.bz2
tar xjf kokoro-int8-en-v0_19.tar.bz2 && rm kokoro-int8-en-v0_19.tar.bz2
ls kokoro-int8-en-v0_19   # model.int8.onnx  voices.bin  tokens.txt  espeak-ng-data/  README.md  LICENSE
sudo chown -R shelfaware:shelfaware /var/lib/shelfaware/models
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
dotnet run --project tools/KokoroCheck -- /var/lib/shelfaware/models/kokoro-int8-en-v0_19 /tmp/kokoro-check.wav
```

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
