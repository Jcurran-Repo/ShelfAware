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
- **The mouth has two providers; the ear has one.** `Speech:Provider` picks between ElevenLabs (cloud,
  per-character, the visitor's own key) and `Kokoro` — Kokoro-82M running IN THIS PROCESS via
  sherpa-onnx, for $0 with nothing to meter and nothing to deploy beside the app. The STT ear stays
  ElevenLabs Scribe either way; moving speech RECOGNITION off ElevenLabs is a separate seam. Both
  mouths answer through `CachingTextToSpeech`, and each namespaces its own `OutputFingerprint`, so a
  clip voiced by one is never served for the other's key. Setup + the model archive:
  `docs/deploy-kokoro.md`.
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

