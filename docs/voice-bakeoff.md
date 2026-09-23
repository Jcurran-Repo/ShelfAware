# Picking a voice

Four local TTS families run in this app, all through the same sherpa-onnx engine and all at $0 per read:
**Piper**, **Kokoro**, **Kitten** and **Matcha**. `Speech:Provider` picks one per box. This page is how
you decide which.

It exists because the decision has two halves and only one of them can be reasoned about. *Does it sound
good* has to be heard. *Is it fast enough* has to be measured, **on the box that will run it** — the same
model is 0.05× real time on one machine and 3.1× on another.

## The short version

1. Run the **Voice bake-off** workflow (Actions → Voice bake-off → Run workflow). It renders the same
   sentence through every voice and uploads the clips.
2. Download the `voice-bakeoff` artifact and listen. Pick the one you want.
3. Put that model on the box and run `tools/VoiceCheck` **there**. If it prints under **1.0×**, ship it.
4. If it does not, the voice is too slow for that box whatever it sounds like — see *Why 1.0×* below.

⚠️ **The workflow's rates are the GitHub runner's, not your box's.** A runner is a modern dedicated core.
The demo droplet is a 2 GHz shared core with no AVX-512 VNNI, where Kokoro measured 3.1× against a
fraction of that on a runner. Use the workflow to choose by ear; use `VoiceCheck` on the box to choose by
speed. Never quote a rate that was not measured where it will run.

## Why 1.0× is the threshold, and not a ratio

Below 1.0× synthesis outruns playback: a reply can start speaking while the rest of it is still being
made. Above it, every sentence arrives later than the one before it finished, so chunked playback trades
a shorter first wait for stuttering rather than fixing anything. This is why "Piper is 26× faster than
Kokoro" is the wrong number to optimise — the only question is which side of 1.0× a box lands on.

⚠️ And on a **public** box the per-household clip cache never amortizes: a visitor arrives with an empty
one, so every visitor pays the full first-read cost on every step. That is what makes a slow voice a
demo-box problem and not a family-box one.

## The families

| Family | Shape | Speed | Notes |
|---|---|---|---|
| **Piper** (VITS) | weights + tokens + espeak data | **0.13–0.54×** | The demo box's voice today. Clear, noticeably flatter. The spread is the model, not the speaker: the `medium` builds run about twice as fast as the `high` ones. Weights are named after the voice, so `Speech:Piper:ModelFile` must say which. |
| **Kokoro** | weights + voices.bin + tokens + espeak data | **1.14×** (and **3.1× on a DO-Regular droplet**) | The family box's voice. The warmest — and **the only one of the four still above 1.0× on a fast desktop core**, which is the whole shape of the droplet problem. |
| **Kitten** | Kokoro's four files exactly, under its own config block | **0.24–0.28×** (nano), **0.62×** (mini) | 24 MB for nano, which has 8 voices, 4 male and 4 female. Mini is larger and slower than every Piper here bar one. |
| **Matcha** | acoustic model **+ a separate vocoder** + tokens + espeak data | **0.17×** | ⚠️ The vocoder is published in a *different release* from the voice. A directory holding everything the voice archive shipped still cannot speak. It is in the cache fingerprint, so changing vocoder re-voices the clips rather than serving the old ones. |

## The lineup

The workflow's `voices` input takes `all` (the default) or a space-separated list of these ids:

| id | Model | Voice |
|---|---|---|
| `piper-lessac-medium` | `vits-piper-en_US-lessac-medium` | 0 — today's demo voice, the control |
| `piper-lessac-high` | `vits-piper-en_US-lessac-high` | 0 — same speaker, higher-quality model |
| `piper-ryan-high` | `vits-piper-en_US-ryan-high` | 0 — male, high |
| `piper-amy-medium` | `vits-piper-en_US-amy-medium` | 0 — female, medium |
| `piper-cori-high` | `vits-piper-en_GB-cori-high` | 0 — British English, high |
| `piper-libritts-0` / `-40` / `-109` | `vits-piper-en_US-libritts_r-medium` | three of its 904 speakers |
| `kitten-nano-0` / `-2` / `-5` | `kitten-nano-en-v0_1-fp16` | three of its 8 voices |
| `kitten-mini-0` | `kitten-mini-en-v0_1-fp16` | 0 — the larger Kitten |
| `matcha-ljspeech` | `matcha-icefall-en_US-ljspeech` | 0 — with the Vocos vocoder |
| `kokoro-0` | `kokoro-int8-en-v0_19` | 0 — the family box's voice, the other control |

**Where that Speed column comes from.** One bake-off run, 2026-09-23, all fourteen voices on one
machine (a desktop i5-13600KF), model load included — so the rows are comparable to each other, which
is the only thing a column like that is good for. Per voice:

| | | | |
|---|---|---|---|
| `piper-libritts-40` **0.13×** | `piper-libritts-109` **0.14×** | `piper-libritts-0` **0.16×** | `matcha-ljspeech` **0.17×** |
| `piper-amy-medium` **0.18×** | `kitten-nano-2` **0.24×** | `kitten-nano-0` **0.27×** | `kitten-nano-5` **0.28×** |
| `piper-lessac-medium` **0.33×** | `piper-ryan-high` **0.39×** | `piper-lessac-high` **0.48×** | `piper-cori-high` **0.54×** |
| `kitten-mini-0` **0.62×** | `kokoro-0` **1.14×** | | |

⚠️ **These are a desktop's, and they are not the numbers that decide anything.** The demo droplet is a
2 GHz shared core with no AVX-512 VNNI, where Kokoro measured **3.1×** against this run's 1.14× — so
expect every row above to be several times worse there, and a voice comfortably under 1.0× here can
still miss on the box. Use this column to narrow the field by ear and by rough order; use `VoiceCheck`
on the droplet to decide.

⚠️ And do not mix this column with the figures in `docs/deploy-piper.md` (Piper 0.05×, Kokoro 1.37×).
Those are best-of-three on two pinned cores with the model already loaded, on a different machine — a
different question, honestly answered, and not this one. A row from each in the same sentence is the
two-numbers-one-story failure this repo keeps paying for.

## Putting a model on a box by hand

The droplet deploy's `bootstrap` step unpacks **Piper and Kokoro** only. For the other two you do it by
hand — and ⚠️ **check the sha256 before unpacking**, because these are release assets on a mutable tag
and you are running as root on a box holding real households' receipts. `deploy-droplet.yml`'s
`bootstrap` does exactly this for the archives it fetches, and for exactly this reason: *"the bytes we
measured" and "whatever that URL serves today" are different promises, and only the first one is worth
running as root.* `--no-same-owner --no-same-permissions` is part of it — GNU tar refuses `..` members
and symlink escapes, but it will happily preserve a setuid bit.

⚠️ **Every check below is joined to what follows it with `&&`, and that is load-bearing, not style.**
`sha256sum -c` on a line of its own prints `FAILED` and returns 1, and a block pasted into a shell runs
the next line anyway — so an archive that failed its check would be unpacked, as root, and the only
sign would be four lines back up the scrollback. `deploy-droplet.yml`'s `bootstrap` exits on a
mismatch; these commands have to do the same thing to be worth calling equivalent to it. **Paste each
block whole.**

```bash
cd /var/lib/shelfaware/models
R=https://github.com/k2-fsa/sherpa-onnx/releases/download

# Kitten
A=kitten-nano-en-v0_1-fp16.tar.bz2
curl -fsSL --proto '=https' -O "$R/tts-models/$A" \
  && { echo "f35dac93754fe2ac97c66e1f468311d0d2130f7f0f5a89bfa1197e09a0cbdec5  $A" | sha256sum -c - \
       || { rm -f "$A"; false; }; } \
  && tar xjf "$A" --no-same-owner --no-same-permissions \
  && rm "$A"
```

Matcha needs the vocoder fetched separately, into the model's own directory. ⚠️ **Verify that one too**:
without `-f`, curl writes an error page to the output file and exits 0, and a 404 body sitting at that
path passes the app's own check — which is `File.Exists` and nothing more — and then reaches sherpa-onnx,
which answers garbage by killing the process rather than by saying so.

```bash
cd /var/lib/shelfaware/models
R=https://github.com/k2-fsa/sherpa-onnx/releases/download
M=matcha-icefall-en_US-ljspeech

curl -fsSL --proto '=https' -O "$R/tts-models/$M.tar.bz2" \
  && { echo "ea75702da7456a8b1874728278a835220dc8a26f4e8bd93c83bf53dc27679845  $M.tar.bz2" | sha256sum -c - \
       || { rm -f "$M.tar.bz2"; false; }; } \
  && tar xjf "$M.tar.bz2" --no-same-owner --no-same-permissions \
  && rm "$M.tar.bz2"

# ⚠️ Downloaded to .part and moved only once it verifies. Written straight to its live name, a
# body that failed the check would still be sitting where the app loads it: Missing() is File.Exists
# and nothing more, so the box boots clean and dies on the first read-aloud.
curl -fsSL --proto '=https' -o "$M/vocos-22khz-univ.onnx.part" "$R/vocoder-models/vocos-22khz-univ.onnx" \
  && { echo "0574a135aa1db2de6e181050db2ec528496cacd4a4701fc5d7faf9f9804c0081  $M/vocos-22khz-univ.onnx.part" | sha256sum -c - \
       || { rm -f "$M/vocos-22khz-univ.onnx.part"; false; }; } \
  && mv "$M/vocos-22khz-univ.onnx.part" "$M/vocos-22khz-univ.onnx"
```

⚠️ **Those hashes are written twice** — here and in `sha_for()` in
`.github/workflows/voice-bakeoff.yml` — because the commands above have to be complete enough to paste,
and sending an operator to read a YAML file for a hash is how a hash gets skipped. Two sites answering
one question is the failure CLAUDE.md names as this repo's most expensive, so the pair is held by a
test: `VoiceModelHashRulesTests` fails the build if this file names a hash the workflow does not record.
A sentence promising they cannot drift would not have been worth anything.

Then prove it speaks **before** pointing the app at it. From a machine with the repo checked out (a dev
box, or the family box):

```bash
dotnet run --project tools/VoiceCheck -- kitten /var/lib/shelfaware/models/kitten-nano-en-v0_1-fp16 out.wav 0
dotnet run --project tools/VoiceCheck -- matcha /var/lib/shelfaware/models/matcha-icefall-en_US-ljspeech out.wav 0
```

⚠️ **Not on the droplet — those two lines want an SDK and a checkout, and the droplet is deliberately
given neither** (the app ships self-contained precisely so the box needs no .NET install). Don't install
one for a smoke test: publish the check the way the app is published and send it up. That route is
written out in [docs/deploy-kokoro.md §3](deploy-kokoro.md), and it is the same three commands whichever
family you are checking — swap `kokoro` for `kitten` or `matcha` and give the matching directory. It is
not repeated here, because a pre-flight procedure with two copies is one that will be half-corrected.

A droplet has no sound card, so `scp` the WAV back to listen to it.

⚠️ **Unpack the model, then set `Speech__Provider`, in that order.** The app refuses to boot pointed at an
incomplete model directory on purpose: sherpa-onnx answers a missing file by printing one line to stderr
and killing the process with a SIGSEGV, so a box configured early boots clean and then dies whole on the
first read-aloud with nothing of ours in the log. `deploy/env.example` lists every setting.

A voice change needs a **restart** — the family is read once, at registration, so that "which family this
box runs" has exactly one answer for the life of the process.

## Adding a fifth family

A family is a descriptor, not an engine. `ISherpaTtsModel` answers the only two questions that differ —
which files must be on disk, and which block of `OfflineTtsConfig` names them — and everything else (the
synthesis gate, the timeout that is not a cancellation, cancellation that actually cancels, the cache
fingerprint) is shared. So a new family is:

1. an options class and a `…ModelFiles` record (override `FamilyInvalid` if it has a rule of its own,
   and `FingerprintExtras` if it has a second file that decides how it sounds, as Matcha's vocoder does);
2. one member on the `SpeechProvider` enum;
3. one arm in `SpeechRegistration.LocalVoiceOf`;
4. one row in `SherpaTtsModelTests.EveryFamily`;
5. one row in `LocalVoiceFamilyRegistrationTests.EveryLocalFamily` **and one arm in its `AModelFor`** —
   a row without the arm throws in the helper and takes every one of that family's cases with it.
   Step 2 is what forces this one: `The_family_table_names_every_local_family_exactly_once` compares
   the table against the `SpeechProvider` enum, so a member added without a row fails rather than
   quietly testing one family fewer;
6. one case in `SherpaTextToSpeechTests.OptionsFor`, plus its `[InlineData]` rows;
7. one case in `tools/VoiceCheck`'s family switch, or the family cannot be pre-flighted and step 3 above
   is not runnable for it;
8. a block in `deploy/env.example`;
9. a row in the bake-off lineup and its hash in `sha_for()` — and in this page's table above.

⚠️ **Both theory rows are the point, not paperwork.** Every family fills the same config struct and hands
it to the same native constructor, and sherpa-onnx does not report a config it cannot make sense of — it
dies. Kitten's file list is Kokoro's *exactly*, so a descriptor that filled the wrong block would be
correct in every name it used and wrong in the only way that matters. The theories assert that each
family fills its own block and leaves every other one empty, and that no two families share a cache
fingerprint prefix — a household that switched voices being served its old clips forever is a silent
failure with a green suite over it.
