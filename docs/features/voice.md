# The voice engine — the built-in cook-along

Extracted from `CLAUDE.md` on 2026-09-19. Read this when working on `RecipeReadAloud`,
`VoiceAgent`, `CookAlongCommands`, `SpeechText`, the TTS cache, or anything that listens.


**The reader is ours; the ElevenLabs agent is an alternative.** `Recipes.razor`'s split button leads with
the built-in hands-free reader (`RecipeReadAloud` with `HandsFree="true"`); the caret holds "Read it to me"
(no mic) and "Live agent" (the EL realtime agent — only when `ElevenLabs:AgentId` is set, billed per minute,
kept because interrupting mid-sentence is the one thing our loop can't do). `read_recipe` lands in ours.
No settings toggle — the caret IS the choice, made per recipe. The agent's connect failure falls back to
the built-in reader.

- **`SpeechText` (Core) spells text out before TTS.** Not a nicety: ElevenLabs disable normalization on
  Flash v2.5 for latency and gate `apply_text_normalization` behind Enterprise, and their own docs show
  Flash reading "$1,000,000" as "one thousand thousand dollars". On our plan, doing it ourselves is the
  ONLY option. Gated by `ElevenLabs:NormalizeText`. It deliberately won't guess: "2 C flour" stays cups,
  not Celsius. **`SpeechText.Version` rides in the TTS fingerprint — bump it when the rules change** or
  cached clips keep yesterday's pronunciation.
- **Narration streams.** `readaloud.js` plays the intro while the steps synthesize behind it and append
  as they land; when playback outruns synthesis the player PARKS on `wantIndex` rather than mistaking an
  empty queue for the end of the recipe. `load(..., auto)` picks the mode: the button reader runs on,
  hands-free stops after each step and calls `OnStepFinished` so .NET can listen.
- **`CachingTextToSpeech` (Web/Services) decorates `ITextToSpeech`** — content-addressed, under
  `app-data/tts-cache`, keyed on text + neighbouring segments (they change the audio) +
  `ITextToSpeech.OutputFingerprint`. **A cache hit needs no API key**, which is what lets seeded/demo
  recipes talk for a keyless visitor. Registered via `SpeechRegistration.AddSpeech` so a test can prove
  nothing bypasses it. Bounded by `Speech:CacheMegabytes` (default 256), trimmed at startup.
- **The mouth has five providers and the ear two, chosen SEPARATELY.** `Speech:Provider` picks the
  mouth: ElevenLabs (cloud, per-character, the visitor's own key), or `Kokoro`, `Piper`, `Kitten` or
  `Matcha` — all running IN THIS PROCESS via sherpa-onnx, for $0 with nothing to meter and nothing to
  deploy beside the app. Which one a given box should run is measured, not assumed: `docs/voice-bakeoff.md`.
  `Speech:Ear` picks the ear: ElevenLabs Scribe or `Moonshine`, in this process, on the same package —
  so the ear adds *nothing* to the publish a local-mouth box already carries. Two settings rather than one
  because a box has to be able to move one before the other. With both local, a deployment needs **no
  ElevenLabs key at all**. Every mouth answers through `CachingTextToSpeech`, and each namespaces its own
  `OutputFingerprint` with its family name, so a clip voiced by one is never served for another's key.
  Setup + the model archives: `docs/deploy-kokoro.md` and `docs/deploy-piper.md` (mouth),
  `docs/deploy-moonshine.md` (ear).
  - **The four local families are one engine and four descriptors** (`ISherpaTtsModel`): they differ only
    in which files must be on disk and which block of `OfflineTtsConfig` names them. The gate, the
    timeout that is not a cancellation, the empty-clip refusal and the fingerprint rules exist once.
    A fifth family — a cloned voice, say — is a descriptor, not a copy.
  - **Kokoro is warmer; Piper is ~26× faster.** Measured on identical cores: Kokoro 1.37× real time,
    Piper 0.05×. ⚠️ The threshold that matters is **1.0×**, because below it synthesis outruns playback
    and a reply can start speaking before it is finished being made. The demo droplet measured Kokoro at
    **3.1×** — 28 seconds of silence for a 9-second reply — which is why that box runs Piper and the
    family box, on real hardware, keeps Kokoro. ⚠️ A Piper *medium* voice (`ryan-medium`, 0.12–0.14×
    there): the *high* ones measured 0.8–0.95× on the droplet, which a desktop run had hidden by timing
    the model load along with the synthesis. `docs/voice-bakeoff.md`, *A worked example*.
  - **A Piper voice is one setting — its directory.** Piper names its weights after the voice, and the
    app reads the name off the archive (`vits-piper-<voice>` → `<voice>.onnx`) rather than defaulting
    to one voice's file, so a new default voice cannot strand a box whose env names the old directory.
    One resolved `ModelFile` is what the file check, the load and the cache fingerprint all read
    (`SherpaTtsOptions.ModelFile`); the raw setting binds to its own property because the config binder
    writes a getter's value back and would otherwise make every unset name look set.
  - **The ear is far cheaper than the mouth.** Measured on one core: Kokoro needs ~17 s to *say* a 7.4 s
    sentence; Moonshine needs 0.7 s to *hear* it. If a small box feels slow, it is synthesis.
  - **The browser sends 16 kHz mono PCM** (`wwwroot/js/pcm.js`, imported by all three capture paths),
    because a model in this process has no codec and decoding opus server-side would mean ffmpeg — a
    second thing to install, which is the property this whole shape exists to keep. A browser can always
    decode what it just recorded. If that conversion fails it falls back to the compressed bytes, which
    a cloud ear still reads and a local one refuses by name.
- **Whether a box can hear at all is ONE definition: `VoiceEar`,** asked by every microphone affordance
  — push-to-talk, the roaming assistant, the hands-free reader, the Recipes cook-along button, the
  reader's "Back to assistant" and "Try again", and Settings' listening calibration — and by the
  transcriber's failure copy. A box that cannot hear offers no microphone rather than recording someone
  and then refusing. ⚠️ It gates BEHAVIOUR, not only markup: the pre-merge review found that hiding the
  roaming assistant's panel behind an `@if` left the component alive and subscribed, so "Back to
  assistant" opened the microphone and rendered nothing — an open mic with no panel and no way to end
  it. `VoiceAgent.StartListeningAsync` and `Settings.CalibrateAsync` ask the predicate themselves, and
  the tests that hold this drive the EVENT rather than reading the markup (`DeafBoxVoiceTests`). ⚠️ The two shut states are deliberately different sentences: BYOK with no key says
  "add your key in Settings", a managed box with no host key says "not available on this box" — on a
  managed deployment that Settings panel is hidden and a pasted key is a no-op, so the instruction would
  be impossible to follow. A local ear is `Ready` regardless of any key.
  - ⚠️ **sherpa-onnx does not throw on a bad model path — it kills the process** (stderr line, then
    SIGSEGV; no managed exception to catch). So `SpeechRegistration` refuses to boot with an incomplete
    model directory and `SherpaKokoroEngine` checks again before loading, both asking the one
    definition (`KokoroModelFiles`). A check that read different paths than the load would pass and
    then crash, which is why they are not two lists.
  - ⚠️ **An out-of-range `SpeakerId` is REFUSED, not clamped.** The model answers a voice index it
    hasn't got by quietly using voice 0 — which would file every clip under a fingerprint naming a
    voice that never spoke it, and go on serving them after the setting was corrected. The archive
    ships no voice-name table, which is also why the setting is an index rather than a name: a mapping
    in our source is a fact nothing could check against the model.
  - **Cancellation is real.** Synthesis is a blocking native call that can run for tens of seconds, so
    the caller's token is read inside sherpa's progress callback (returning 0 stops the run — a 32 s
    synthesis stops in 4 s). A reader the household closed doesn't keep burning a core.
  - **Clips are WAV, ~10× an MP3.** `WaveAudio` packs the samples; nothing re-encodes. That is a real
    consideration for `Speech:CacheMegabytes`, noted in the deploy doc.

- **`CookAlongCommands` (Core) is the fast path, NOT a gate.** Whole-utterance matching (same discipline
  as `VoiceCommands.IsStop`) resolves next/back/repeat/step N/start over/hold/stop for free. Anything it
  misses goes to `IPantryChat` — with the recipe as `screenContext` — which can ANSWER or MOVE us
  (`go_to_step`). That's deliberate: before `go_to_step` a grammar miss was *wrong*, so the phrase list
  had to enumerate every way a human says "next" through a cough. Now it's just slower. **Don't
  re-tighten the grammar into a gate.**
- **Half-duplex on purpose.** We listen BETWEEN steps only. Listening over our own voice needs echo
  cancellation good enough to hear "stop" under the voice saying "stop"; a step boundary is where a cook
  actually talks. Cost: no mid-sentence interruption (that's what the Live agent is for). Consequence:
  **"hold on" can't pause anything** — by the time you can say it the step has ended and the reader is
  already waiting. Its job is to stop us reacting to the room (no brain calls while held).
- **`pause`/`resume` must ignore an ENDED clip.** An ended element reports `paused === true` and
  `play()`ing one rewinds it — which re-read the step every time Jordan held. `resume()` returns whether
  anything actually resumed, because "I'm back" with nothing to resume must keep LISTENING, not hand off
  to a playback that will never call back.
- **`ListeningSettings` (Core) + the Settings calibration wizard.** The browser measures (`measureFloor`,
  `measureUtterance`); Core decides. The gate sits at the GEOMETRIC mean of room and voice (loudness is a
  ratio scale). Calibration listens with a 2.5s end-silence — a shorter one couldn't observe a pause it
  would then cut off, i.e. it would confirm its own guess. **Per DEVICE**, own localStorage key
  (`shelfaware.listening`, NOT `shelfaware.ai` — that store has a session-only mode and a calibration
  isn't a secret). A run that heard nobody changes nothing and says so.
- **Scribe gotchas (both cost real bugs):** `tag_audio_events` defaults TRUE and tags events into the
  TEXT ("Next (coughing)") — we turn it off AND strip annotations in `Utterance`; and a clean one-word
  "Next." comes back with `language_probability` 0.33, so `ElevenLabs:SpeechLanguage` (default `eng`)
  names the language rather than letting it guess.
- **`VoiceCoordinator.StandDownRequested`** is the mirror of `ResumeRequested`: there's one microphone,
  and `read_recipe`'s `HandsOff` only covered the agent STARTING a reader. This covers a user opening one
  while the roaming agent is already listening. The agent stands down but keeps its conversation.
- **Privacy:** the reader logs what it RESOLVED at Information but what it HEARD only at Debug — a
  microphone in someone's kitchen shouldn't record their speech to disk on a real deployment.
  Development turns it on for `ShelfAware.Web.Components.RecipeReadAloud`.
- **Open:** an intermittent bug where jumping to a step left "next" advancing from the old index, then
  wouldn't reproduce. Every static path says it can't happen (the player was proven correct in a browser),
  so it's timing. The logging above exists to catch it.

