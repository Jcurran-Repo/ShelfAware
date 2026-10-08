# Backlog — what's open

Carried out of `CLAUDE.md` on 2026-09-19. Everything here is deliberate: either parked with a reason,
or small-and-not-yet-worth-a-branch. Shipped items are struck from the list rather than accumulating
as "(shipped since this note)" parentheticals, which is how the old version got to be wrong about
`docs/demo.gif` for two months.

## Open

- **The Core mutation gate can report success having tested ZERO mutants.** Found 2026-09-19 on the
  in-process-voice branch: `WaveAudio.cs` was the only `src/ShelfAware.Core/**` file it added, all 34 of
  its mutants failed to compile (a `short` argument that was only legal because the compiler folded it to
  a constant — Stryker rewrites the expression into a ternary, which is not one), Stryker logged *"0 total
  mutants will be tested"* and *"unable to calculate a mutation score"*, **exited 0**, and CI's
  `Core mutation (changed files)` check went green. The file was fixed so its mutants compile and it now
  scores 100% over 28 of them, so nothing is currently unmeasured — but the gate said yes to a file it had
  not read, which is the same shape as the razor-scan gap `ProviderCancellationSiteTests` was given a reach
  guard for, one level up. The fix is a reach guard in `mutation-pr.yml`: when the diff touches Core, fail
  if the report's tested-mutant count is 0. **Deliberately not done on that branch** — it is a workflow
  change that cannot be tested from a session, and the mutation workflow already has one entry on this
  list. `docs/mutation-testing.md` §"Known limitation: compile-error mutants" describes the bucket but
  reads as "a few mutants cannot exist", not "all of them can vanish and the check still passes".

- **The mutation score on `/admin` is carried forward, not re-measured.** `ci.yml` does not run Stryker,
  so its snapshot never measures a score; it now keeps whatever the last measuring run wrote rather than
  blanking the tile (which is what it did before 2026-09-19, and would have removed the card on the first
  push to master). The consequence is that the number can sit unchanged indefinitely with nothing saying
  how old it is — a milder version of exactly what §6 is about. The fix is for `mutation.yml`, the thing
  that measures it, to publish it: that needs `contents: write` and a commit job of its own, which is more
  new untested workflow than a gate fix should carry, so it is written down instead. **Revisit when the
  weekly run next changes the score**, since that is when the staleness would first bite.

- **The credit gate checks the balance but does not reserve it.** `EnsureManagedCallAllowedAsync` reads
  the balance before the provider call; the act's charge is claimed in the tail after it, and
  `RecordConsumptionAsync` writes the negative row with no floor. So several ACTS started at once can all
  pass the same check and all charge — an **overdraft, not free credit**: the balance goes negative, the next
  gate refuses, and the household fills the hole before spending again, so it self-corrects and errs toward
  the operator for exactly one burst. ⚠️ **Re-costed 2026-10-07** (assessment in the session that did the
  pre-release audit): the original note said closing it needs "a reservation row at the gate and a release
  path on every exit of every act". The release path already exists — `AiActionScope.DisposeAsync` →
  `MeteredChatClient.ReverseUndeliveredAsync`, built 2026-09-19 after this note was written, runs on every
  exit with `delivered` defaulting to 0 — so "reserve then release unused" collapses to "charge at the gate,
  refund what wasn't delivered". What is left is a conditional ledger write (`INSERT … WHERE balance >=
  price`, raw SQL, atomic on SQLite only in a form that has to be checked on the PC), carrying the margin-day
  stamp from the gate to the tail, a file-backed test harness for the concurrency case, and three semantic
  calls that are Jordan's: a failed first call writes a net-zero charge+reversal pair that appears in the
  export; a ledger failure at the gate refuses the act (today it lets the call run — pinned by
  `A_failed_money_write_costs_that_call_not_the_whole_action`); and `CheckAiAsync` becomes a preview beside
  the write that enforces. Its own PR, on the PC, after those are decided. **Revisit if a balance is ever
  seen materially negative.**
- **A handful of invisible code points still read as an "answer" and are charged.** `ProviderReply`'s
  allow-list excludes marks, punctuation, separators, controls and the replacement character, but a
  Hangul filler (category `Lo`) and BRAILLE PATTERN BLANK (`So`) are categorically letters and symbols
  while rendering as nothing. Left alone deliberately: a model emitting only one of those is not a shape
  anyone has seen, and chasing every invisible code point by hand is how the deny-list this replaced got
  it wrong. Revisit if a real reply ever lands in that gap. Raised by the pre-merge code gate, 2026-09-19.
- **Re-record `docs/demo.gif`** — optional polish, not a gap. It has existed since 2026-07-12
  (`5f34b24`), but it pre-dates every feature from v3.5 on: variety, expiration, Reports, the whole
  counting arc, the census, the tour. A re-record needs a NEW capture plan first — the original
  storyboard was deleted when the gif landed, per that file's own lifecycle note.
- ⚠️ **Speech is neither metered nor gated** (found by the phase-7 security gate, 2026-09-19).
  `ElevenLabsTextToSpeech` and `ElevenLabsSpeechToText` are typed `HttpClient`s, not `IChatClient`s, so
  they never reach `MeteredChatClient`: nothing records their cost, nothing charges for them, and
  `RecipeReadAloud.razor` has no `AiErrorText.BlockedReasonAsync` gate (unlike `PushToTalk.razor`). A
  household at **zero balance** can still burn the host's ElevenLabs quota — on a box that HAS an ElevenLabs
  key; the demo box deliberately has none and voices with Piper in-process, so there the exposure was CPU
  and disk, not dollars. **Narrowed 2026-10-07:** the disk half is closed by the after-write cache trim
  (Recently closed) and the CPU half is bounded by `PromptInput` capping a pasted recipe at 20,000
  characters; synthesis was already serialised (`SherpaTtsEngine`). The published prices for
  `TtsSynthesis` and `RealtimeMinute` stay WITHDRAWN from `CreditPricing.MeteredActions`, so no false
  statement stands. What remains is the ElevenLabs case on a paid box: wiring it needs a real ElevenLabs
  invoice to price a read against (the 3-credit figure is an estimate) and a charge point that isn't an
  `IChatClient`. Jordan's call whether to wire it or leave cloud speech free.
- **The remediation arc** — all seven phases landed 2026-09-19, designed in `docs/remediation-plan.md`,
  with the review-gate pass on phase 7 written up in its §9. What's left out of that arc is deliberate:
  draining logic out of `.razor` (D1) and EF Migrations (D3), both with reasons in §8.

- **Four pages cannot be read by the razor lift the build rules use.** `MainLayout`, `Accuracy`,
  `GroceryList` and `MealPlanPage` each define a `RenderFragment` with a razor TEMPLATE expression
  (`=> @<div>…`), which the razor compiler turns into C# but a plain brace-match lift cannot. They are
  named in `SourceTree.Unliftable` and asserted to be exactly that set, so a fifth page fails the build
  rather than dropping out of the scan — but they are genuinely unjudged today. None calls a provider.
  If one ever needs judging, move the fragment into a component rather than widening the lift.

- **There is no account deletion.** "Delete all my data" (`UserDataService.cs`) removes the pantry
  tables, receipt images, the TTS cache, recipe images and API tokens, and deliberately keeps
  `AiUsages` and `CreditLedger` (the operator's cost record). What it does not touch: the `AspNetUsers`
  row, the `Household` row, `UserLoginStats` and `ProcessedPaymentEvents` — `HouseholdService.cs:289`
  says so in as many words ("no account deletion exists yet"). The README's old promise to "delete
  every trace" was reworded 2026-10-07 to say what the button actually does. A real deletion has to
  decide what happens to a household's other members, the credit ledger a refund may still need, and
  the Stripe customer, which is why it is a design item and not a tidy-up.

- **CI's "Publish test status" job has never landed a commit.** It pushes to `master` on every
  post-merge run and branch protection refuses it every time (run 35926123956: `GH006: Protected
  branch update failed … 2 of 2 required status checks are expected`), and `ci.yml` turns the refusal
  into a warning, so `src/ShelfAware.Web/wwwroot/test-status.json` is still the hand-generated
  2026-09-19 snapshot (`CommitSha ""`, `Branch ""`) — the card §6 of the remediation plan made
  self-maintaining is not. The fix is a GitHub setting, not a repo change: let the Actions app bypass
  protection for that path, or have the job open a PR instead. `mutation.yml` is getting the same
  commit pattern for the mutation score and needs the same bypass, so one setting closes both.

- **The CSP trusts `https://esm.sh` site-wide for one optional feature.** `Program.cs:731-752` allows
  `script-src https://esm.sh` so the cook-along can load the ElevenLabs SDK, which is off by default.
  That trusts everything that CDN ever serves, on every page, on every box — and on a BYOK box a
  compromise there can read `localStorage['shelfaware.ai']`. The honest fix is to vendor the SDK at a
  pinned version (the worklets already are, see `THIRD-PARTY-NOTICES.md`) or to add the origin only on
  the page that needs it. Parked because the feature is off by default and the demo box is managed-key.

- **No `ErrorBoundary` anywhere.** Acknowledged in the code at `Cookbook.razor:693` and
  `Recipes.razor:1091`: an exception that escapes an event handler tears down the whole circuit, and
  the person sees Blazor's reconnect overlay instead of the page with one broken panel. The handlers
  catch what they expect, so this is about the unexpected. A boundary per page (or in `MainLayout`
  around `@Body`) is small; deciding what it renders, and how that reaches the error log, is the work.

- **No clock abstraction in Web.** 113 wall-clock reads in `ShelfAware.Web`, 48 of them the literal
  `DateOnly.FromDateTime(DateTime.Today)`, and 0 in Core, which takes `today` as a parameter. The
  Core half is why the engine is testable; the Web half is why page tests cannot pin a date and why
  the "one prediction" drifts below can exist at all (two `today`s on one page). A `TimeProvider`
  injected at the composition root is the standard shape; the cost is touching 113 sites in one
  change, which the rule on partial conversions says is the only way to do it.

- **Three "one prediction, one story" drifts.** (1) `ProductDetail.razor:257` computes "expires in N
  days" in markup from a second `today` rather than reading it off the `PredictionResult` beside it.
  (2) `ReportDataService.cs:147` calls `Predict` without `honorQuantity` while `:250` and every page
  pass it, so a report can disagree with the page it links to about a counted item. (3)
  `PantryPhoto.razor:732` defaults both flags. Each is the exact shape the CLAUDE.md rule was written
  for; each is a one-line fix plus the test that pins it, and (2) is the one most likely to be seen.

- **`ElevenLabsSpeechToText.cs:82` has no `OperationCanceledException` arm** while every Anthropic
  provider in the same project has one (`catch (OperationCanceledException) when
  (cancellationToken.IsCancellationRequested) { throw; }`, pinned by `ProviderCancellationSiteTests`).
  A cancelled transcription is logged as a failure and reported as one. That build rule covers the
  Anthropic sites and the pages, not this `HttpClient` provider, which is how it slipped.

- **`MealPlanJobs.cs:15` promises a test that does not exist.** The comment says the detached runner
  is covered; nothing under `tests/` exercises it. Either write the test (a job that outlives its
  circuit, is cancelled with the host, and reports to the right household) or delete the claim — a
  comment that names a test is a claim in the §6 sense.

- **`coverlet.collector` is referenced by all four test projects and nothing collects coverage.**
  It was added for the 2026-07-30 audit (`docs/test-audit.md`) and no workflow has passed
  `--collect:"XPlat Code Coverage"` since. Either wire a coverage step into `ci.yml` (and decide what
  to do with the number — the mutation score is the one the repo actually trusts) or drop the four
  references. A dependency nothing uses is a question every reader has to answer again.

- **Two things the suites never reach.** The bUnit suite never renders `Register`, `Login`,
  `ExternalLogin` or `ChooseHousehold` — the pages a new person meets first, and the ones with the
  most hand-written auth flow. And `wwwroot/js` holds 19 modules (`theme.js`, `bug-capture.js`,
  `cookbook-carousel.js`, `voice.js` …) with no JS test tooling at all; the enhanced-nav theme
  regression in PR #18 was exactly the kind of bug a ten-line test would have held. The account pages
  are static-rendered, so bUnit can mount them; the JS side needs a runner decision first.

## Parked, with reasons

- **CSV history importer** — Walmart won't export to Jordan's state, so there is no itemized source to
  import from. Needs a different provider before it's worth building.
- **Barcode / SKU lookup for receipts** — rejected, not deferred. Receipts print internal store SKUs,
  not scannable UPCs, and no public API maps them; it would mean hand-maintaining per-store tables.
- **The lapsed-Free "keep-warm" problem** and **the dormant-subscriber conscience nudge** — both in
  `docs/subscription-plan.md` §8, each with its candidate shapes recorded.

## Deployment gotchas that are not backlog but keep being rediscovered

⚠️ **Timezone and locale, same root.** Every "today" in the app (purchases, signals, predictions) is
server-local `DateTime.Today`/`DateTimeOffset.Now`, deliberately consistent, and every price formats on
the server's culture. A box with no timezone or locale set files evening purchases on tomorrow's date
and renders `¤3.99` (invariant culture — a systemd service starts with **no** `LANG`, found on the
first live deploy). Set the droplet's timezone (`timedatectl set-timezone`, or `TZ` in the service
env) and keep `LANG` in the env file. Runbook step 2 covers both.

⚠️ **Branch protection refuses the test-status commit, and CI reports it as a warning.** Every
post-merge `ci.yml` run ends with `GH006: Protected branch update failed` from the "Publish test
status" job (first seen in run 35926123956), so the `/admin` card reads the 2026-09-19 snapshot no
matter how many PRs merge. It looks green. The one-line fix is on GitHub, not in the repo: in the
`master` branch ruleset, add the GitHub Actions app to the bypass list (or let the job push a PR).
`mutation.yml`'s score commit needs the same allowance.

## Recently closed

- **Tag dedup does not see through Unicode confusables.** — closed 2026-10-07. `TagVocabulary.MatchKey` folds the common Cyrillic and Greek lookalikes (a TR39-style skeleton, the common subset not the full table) to their Latin twins — only when every other letter in the tag is Latin, so a genuinely Cyrillic or Greek tag keeps its own letters. "Ѕoda" ≈ "Soda" is pinned, both directions, in `TagVocabularyTests`.
- **Advisor prompts interpolate the household's whole vocabulary with no cap.** — closed 2026-10-07. `TagVocabulary.NearestForPrompt` ranks the vocabulary by edit distance between match keys to the candidate (or, for the recipe tag advisor, to the recipe's name and ingredients) and sends the nearest `PromptVocabularyLimit` (200) — not a `.Take`. The limit was first 40; the 2026-10-08 audit raised it far past any real vocabulary, because nearness by spelling is not nearness by meaning ("soda" is nearer "deli" than "soft drink"), so a trim at 40 would have dropped exactly the synonyms the advisor exists to find. The reply is resolved against the list the model saw, and a vocabulary with no tag-sized entry skips the charged call. `TagPromptRankingTests`, plus prompt-shape tests in both advisor suites.
- **The advisor prompt caps each tag's length but not the tag COUNT.** — closed 2026-10-07. Same change as the vocabulary entry above: bounded at `PromptVocabularyLimit` by nearest-N, in Core.
- **A pre-cap tag stored in decomposed form drops out of dedup.** — closed 2026-10-07. `TagStoredFormMigration` runs at boot strictly after `AdditiveSchema.Apply`, in one transaction over `ProductTags` and `RecipeTags` across households (raw SQL, not a fifth `IgnoreQueryFilters` site): every value is rewritten to `TagVocabulary.StoredForm` (trimmed, whitespace-collapsed, NFC), a collision on the same owner keeps the earlier row, anything over the cap in any form is left alone and counted, and a second boot rewrites nothing. `Canonicalize` now writes the stored form too, so no new row can need it. Eleven tests in `TagStoredFormMigrationTests`.
- **The TTS cache is trimmed only at startup, and a free voice fills it ~10× faster.** — closed 2026-10-07. `SpeechCacheBudget` keeps a lock-free running byte total per household (seeded by one scan on the household's first write after boot); a write that crosses `Speech:CacheMegabytes` claims the household's one trim slot and runs the existing eviction sweep detached from the request, which re-measures the drawer and corrects the total. The common case is an interlocked add and a compare — no directory scan on the hot path, which was the cost that parked it. The boot trim is unchanged. Ten tests in `CachingTextToSpeechTests`.
- **A per-size Trends price chart** — closed 2026-10-07. `PriceSeries.BySize` is the one definition of a product's price series by size, ranked most-bought first; `Dominant` is now defined as its head, so Product Detail's one-size chart and Trends' every-size rows agree by construction. Trends renders one row per (product, size); the year's spend stays per product and spans its size rows. Six Core tests, three bUnit tests.
- **Eggs' suit buttons are misaligned — realign on EVERY icon** — closed 2026-10-07. Both buttons centred at `cx=256`, `cy=352/372` in `shelfaware-icon.svg` and `EggsMascot.razor` (the two sibling SVGs already had them there); the three served PNGs re-rasterised from the corrected source. `docs/icons/README.md` records the placement.
- **The recipe request box has no cap, client or server.** — closed 2026-10-07. `PromptInput` in Core is the one answer to how long a prose box may be — the quick update (500), the recipe request (300), a pasted recipe (20,000) — and each page's handler refuses over-length input before its pre-check and before any charged call, with the cap named. The `maxlength` attributes are the courtesy.

- **`docs/accuracy.png`** — verified 2026-10-07: the file exists (since 2026-07-12) and the README
  renders it. Nothing to do; the item was carried for months on a note that was itself stale.
- **The model picker offered aliases.** `Settings.razor:1041` listed `claude-sonnet-5` and
  `claude-opus-4-8`, both aliases, against DESIGN.md §2's "pin versioned IDs, never aliases" — an
  alias moves under you and the pinned-ID rule exists so a receipt extracted today and one extracted
  next month ran the same model. Fixed 2026-10-07 on the docs-and-gaps branch.

- **A meal plan charging for meals it does not deliver** — closed 2026-09-19, in two passes. The first
  fixed the gate: it asks whether the balance covers *this act's* price, so a household is refused a plan
  it cannot afford before any of it runs. The second (Jordan's call: "any call that fails should probably
  be refunded") made every act settle up. An act now declares how many units it asked for and reports how
  many it delivered; disposing it reverses the difference as a `Reversal` ledger entry priced by the same
  `CreditPricing` call that charged it, so a 124-meal plan that returns 7 meals keeps 3 credits of its 42
  and gives 39 back. The refund is keyed to a charge that really happened — `ChargeRecorded` is only
  handed a settlement when the meter actually wrote one — so a Founder or a billing-off box, which never
  charged, cannot mint credit by failing. `UnspentAllowanceCreditsAsync` counts reversals alongside
  consumption, so a refunded allowance credit still expires with its month rather than outliving it.
  A build rule (`Every_act_reports_what_it_delivered`) fails the build if a new charging site forgets to
  say what it delivered, which would silently refund the whole act.

- **Phase-5 cloud deploy** — LIVE on a DigitalOcean droplet since 2026-08-11 (not Azure), via
  `docs/deploy-droplet.md` + `deploy/`. The demo link points at https://demo.shelfaware.net. It runs
  in **Managed** key mode (`Llm__KeyMode=Managed`, the host's own spend-capped key) with
  email-confirmed registration and daily caps on new accounts, per-household AI calls and tokens, and
  box-wide AI calls — the caps are deliberate and stay (Jordan: "it has to stay to keep me safe from
  bots"). Earlier notes calling it BYOK describe how it launched, not how it runs.
- **Learning corrected names and brands from receipt review** — the corrected product NAME via the
  alias's product (PR #19, item 53), the corrected BRAND per (merchant, raw text) via PR #46 (item 61).
- **The ~768–1400px header overflow** — retired by the left sidebar nav rail (PR #21, 2026-08-22).
- **Downloading a receipt's saved image copy** — household-scoped `/api/receipt-image/{id}` plus a
  Download link on `/receipts` (PR #22, 2026-08-22).
