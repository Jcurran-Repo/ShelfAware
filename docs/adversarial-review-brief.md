# Adversarial review brief

A prompt to hand a **fresh** Claude context for an independent review of this repo. Written by the
Claude that built the voice arc, which is exactly why it shouldn't be the one reviewing it: a
high-effort review of that branch found five real bugs in code it had read all session, including a
page that held the microphone open and never let go. Authorship is a blindfold.

Delete this file, or update it, when the review has happened.

---

## Before you paste anything

**Don't ask for "review the repo".** It's ~12k lines across a decade of feature arcs; a reviewer with
no focus will skim everything and find nothing. Point it at the boundary where a bug is worst.

**`/security-review` and `/code-review` are diff-scoped** — they review pending changes on the current
branch. Master is clean, so they'd return an empty review. Paste the prompt below instead.

**Run it as two separate sessions.** Tenancy and the voice loop are different mindsets, and a single
context doing both will do neither well. Tenancy first — it's where a bug means one household reading
another's data.

---

## Session 1 — tenancy (do this one)

> You are performing an adversarial security review of a multi-tenant .NET 10 / Blazor Server app. I
> want you skeptical, not reassuring: your job is to find the case where household A can read, write,
> or infer household B's data. Assume the author was competent and the bug is subtle.
>
> **The boundary.** Every pantry entity implements `IHouseholdOwned`. `ShelfAwareDbContext` carries a
> per-instance `HouseholdId` that drives a global query filter on every table and stamps it onto
> inserts. `IHouseholdDbFactory` is the only sanctioned way to get a pantry context; it pre-sets that
> id from the scoped `ICurrentHousehold`, which resolves in this order: an explicit `UseFixed` pin →
> an HttpContext claim → the circuit's auth state. Read `src/ShelfAware.Web/Data/CurrentHousehold.cs`
> and `ShelfAwareDbContext` first. Identity lives in a separate SQLite file (`auth.db`).
>
> **Specifically hunt for:**
> 1. Any path to a pantry `DbContext` that does NOT go through `IHouseholdDbFactory` — the raw
>    `IDbContextFactory` is supposed to be bootstrap-only. Grep for it. Who else resolves it?
> 2. Any query using `IgnoreQueryFilters`. There is at least one legitimate use (enumerating
>    households for the startup receipt scan). Is it only reading which households exist, or does it
>    reach their data?
> 3. A scope where `ICurrentHousehold` resolves to the WRONG household or to null and the code
>    proceeds anyway. Detached background tasks are the interesting case: the auth-state step only
>    works inside a component's synchronization context, so circuits pin via `UseFixed` up front. What
>    happens if that pin is missed, or races? What does a null household do at each call site?
> 4. `AppSettings` has a composite PK `(HouseholdId, Key)`, and alias uniqueness is
>    `(HouseholdId, Merchant, RawText)`. Is there any table where the household is NOT part of the
>    identity but should be?
> 5. Invite codes (`HouseholdService`): generation entropy, whether they expire, whether they're
>    single-use, whether a valid code lets you join a household you shouldn't, and whether the join
>    path bypasses the `Auth:AllowRegistration` gate by design or by accident.
> 6. The API endpoints in `Program.cs` (`/api/data/export`, `/api/cookalong/signed-url`). Both require
>    auth. Do they scope to the CALLER's household, or accept an id from the request?
> 7. The speech cache (`Services/CachingTextToSpeech.cs`) files clips per household under a hash of
>    the id. Can one household read another's clips? Does `UserDataService.DeleteAllAsync` actually
>    remove them?
>
> For each finding give me the file, the line, and a concrete scenario: what a user does, in what
> order, and what data crosses. If you can't construct the scenario, say so and rank it lower —
> I'd rather have five real findings than twenty maybes.
>
> Do NOT propose fixes until I've seen the list.

## Session 2 — the voice arc (only if session 1 comes back clean)

> You are reviewing a hands-free voice cook-along in a .NET 10 / Blazor Server app, adversarially. It
> was built and reviewed by the same author, so assume the review was blind in the usual ways.
>
> **Read `CLAUDE.md`'s "Voice: the built-in cook-along" section FIRST.** It documents the deliberate
> decisions and why. Several things that look like bugs are load-bearing choices — flag them if you
> disagree with the REASONING, but don't report them as oversights:
> - The reader is **half-duplex** (listens only between steps). Interruption mid-sentence is
>   knowingly given up; that's what the ElevenLabs realtime agent option is for.
> - The plain-code grammar (`Core/Speech/CookAlongCommands.cs`) is deliberately **incomplete**. A miss
>   falls through to the model, which can move the reader via `go_to_step`. It's an optimisation, not
>   a gate.
> - "Hold on" intentionally can't pause audio (nothing is playing when you can say it).
>
> **Hunt for:** races in the listening loop in `Components/RecipeReadAloud.razor` (there is a KNOWN
> intermittent bug: jumping to a step occasionally leaves "next" advancing from the stale index, and
> it will not reproduce — the player was proven correct in isolation, so it's timing; `OnStepFinished`
> is fire-and-forget from JS and the loop is re-entrant); microphone lifetime (is it ever left open —
> disposal, navigation, circuit death, two components wanting it at once); anything that spends money
> per room noise rather than per user intent; and whether `Utterance`/`SpeechText` can be made to
> behave pathologically by a hostile transcript.
>
> Give me file, line, and a concrete failure scenario for each.

---

## Known, deliberate, and documented — don't re-litigate these

A reviewer will hit these and may flag them. Each was reasoned about; the reasoning is in `CLAUDE.md`
and the commit messages. Challenge the reasoning if you like — don't report them as discoveries.

| Thing | Why it's like that |
|---|---|
| The EL realtime agent's prompt + model live in a vendor dashboard, not git | Known weakest surface. It's an opt-in alternative, gated on the user's own agent id. |
| The speech cache serves a clip without an API key | Within one household only, and it's audio that household already paid to synthesize. Sharing across households was removed deliberately. |
| `go_to_step` is registered on every chat call, not just cook-alongs | Nothing outside a cook-along reads `ChatResult.StepTarget`. |
| Transcripts log at Debug, Development turns them on | A microphone in a kitchen shouldn't record its owner to disk by default. |
| BYOK keys transit server RAM during a call | Documented in the README's "Whose keys?" — never persisted, never logged. |

## Known-open, no need to rediscover

- **The intermittent step-jump** (above). Instrumented, unreproduced.
- **`Auth:AllowRegistration` is unset on the tailnet deploy**, so it defaults to true — anything that
  reaches that box can self-register a household and spend the host's managed keys. Tailnet-only today;
  **mandatory to set false before any public Azure deploy.**
- **Azure has never been deployed.** The timezone gotcha (`WEBSITE_TIME_ZONE`) is documented and
  untested.
