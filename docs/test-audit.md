# Test-suite audit & rebuild — working doc

The arc's driving document (branch `feature/test-suite-rebuild`, started 7/30/2026).
Phase B fills the worklist; verdicts and findings land here as they're made, so the
audit survives any one session.

## The bar (Jordan, 7/30/2026)

- **Everything covered** — every behavior, not every line (see the exclusion policy below).
- **Useless tests deleted** — per the deletion criteria below, one justification per deletion.
- **Strict quality throughout** — a test asserts a behavior someone depends on, and would fail if that behavior broke.
- **No test is ever weakened to make it pass.** A failing test means fix the code, or fix the
  expectation because the expectation was wrong — stated in the commit either way.
- **Page flows are TESTED, not walkthrough-verified.** A page-test harness (bUnit or
  equivalent) is in scope, not optional.

## Baseline (7/30/2026, branch point = master `9f79cb5`)

| Project | Tests | Line coverage (union of the 3 runs) | Files at 0% |
|---|---|---|---|
| ShelfAware.Tests (Core) | 510 | **98.8%** (1723/1744) | 0 of 65 |
| ShelfAware.Llm.Tests | 93 | **79.4%** (889/1119) | 3 of 11 |
| ShelfAware.Web.Tests | 286 | **25.0%** (2171/8698) | 49 of 82 |
| **Total** | **889 green, 0 failed** | **41.4%** (4783/11561) | 52 |

Regenerate: `dotnet test ShelfAware.slnx --collect:"XPlat Code Coverage"` — coverlet.collector
is already referenced by all three test projects. ⚠️ Each run emits filenames relative to a
*different* base directory; merging naively splits one file into two keys and reports false 0%s
(this bit us on the very first run of this audit). Union at line granularity after resolving
filenames against `src/`.

### What the numbers say

- **Core is the model.** Near-total coverage, zero uncovered files. The 21 uncovered lines are
  the audit's cheapest sweep.
- **Llm's gap is three advisors never constructed in any test:** `AnthropicProductSubstituteAdvisor`
  (41 lines), `AnthropicIngredientAlternativesAdvisor` (39), `AnthropicTagAdvisor` (24). All three
  are fail-soft AI calls — low stakes, but "fail soft" is itself a behavior worth pinning (a thrown
  advisor must degrade, not crash the caller). Partials worth reading: `AnthropicRecipeAdvisor` 68%,
  `AnthropicPantryChat` 90% (587 lines — what do the missing 58 do?).
- **Web's gap is the page layer, plus a handful of real services.** Every `.razor` page and
  component is at 0% — that's the known no-harness gap the bar now closes. The non-page 0%s that
  are genuine logic: `ReceiptSelfEval` (92 lines), `CircuitVoiceCredentials`, `VoiceCoordinator`,
  `HouseholdClaimsPrincipalFactory`, `HouseholdDbFactory`. Partials with dedicated test files that
  still read low: `MeteredChatClient` **45%** (has `MeteredChatClientTests`), `CachingTextToSpeech`
  63%, `EfPantryStore` 66%.
- **⚠️ Anomaly — DIAGNOSED (Phase B, 7/30):** `EfAppSettingsTests` never constructs `EfAppSettings`.
  Its private `SetAsync`/`GetAsync` helpers re-implement the subject's two methods inline against the
  DbContext, so the assertions pin the context's tenancy filter (real behavior) but cannot fail
  because of the class in the filename: the upsert add-vs-update branch, `value ?? ""`, and the
  factory path have zero coverage. **A new hunt-list entry falls out: tests that re-implement their
  subject instead of calling it.** Calibration lesson: a passing test file named after a class proves
  nothing about that class until you check what the arrange actually constructs.

## The hunt list — bad-test classes this repo has already caught (each cost a real bug)

1. **Vacuous via over-permissive fakes** — `set_quantity`'s fake accepted writes the real store
   refuses; refusal tests passed without testing refusal. *Audit move: diff every fake's contract
   against the real implementation it stands in for.*
2. **null==null pins** — the pick-clock test compared two nulls and pinned nothing until it seeded
   a real date. *Audit move: for every equality assertion, ask what makes the expected side non-default.*
3. **One-context guarantees** — the "Ate it" re-plan test shared one `DbContext`, asserting a
   guarantee production (two contexts, unbounded gap) never had. *Audit move: any test of a
   read-then-write flow must model the real context boundaries.*
4. **One-sided coverage** — all four `PantryOnHand` count tests covered "count should win"; the
   pinned-item regression (count must LOSE) sailed through 849 green. *Audit move: for every
   precedence rule, demand a test on each side.*
5. **Complement tests that can't fail** — on-hand/out-of-stock both negate one predicate, so they
   stay complements even when both are wrong. *Audit move: pin absolute membership, not just the relationship.*

Plus the generic classes: tautologies (assert what the arrange constructed), framework tests
(asserting EF/xUnit behavior), duplicate coverage (keep the stronger), and
implementation-detail pins no behavior depends on.

6. **(added in Phase B)** **Subject re-implemented in the test** — `EfAppSettingsTests` rebuilt its
   subject's methods as private helpers and tested those; assertions can fail, but never because of
   the subject. *Audit move: check what the arrange constructs — a test that never instantiates the
   class in its filename is testing something else.*

## Deletion criteria — SIGNED OFF (Jordan, 7/30/2026)

A test is deleted only when at least one of these holds, named per test in the commit message:

1. It cannot fail (tautology / vacuous / pins nothing).
2. It exactly duplicates another test's coverage — the stronger one stays.
3. It pins an implementation detail no behavior depends on, AND the behavior it was gesturing at
   is covered (or gets covered in the same commit).
4. Its subject no longer exists.

Never deleted for being slow, inconvenient, or red. A red test is a finding, not a nuisance.

## Coverage-exclusion policy — SIGNED OFF (Jordan, 7/30/2026)

"Everything covered" means every *behavior*. Excluded from the coverage bar, with reasons:

- **`Program.cs`** (341 lines) — composition root; its behavior is "the app boots", which no unit
  test can honestly claim. The middleware decisions in it (auth redirects vs status codes, CSP)
  are pinned by tests where extractable; the wiring itself is verified by running the app.
- **ASP.NET Identity scaffolding** (`Components/Account/Identity*`, shared layouts) — framework
  plumbing on static SSR; the *decisions* in it (registration gate, household middleware guard)
  are already covered via `HouseholdServiceTests`/middleware tests, and the pages themselves are
  exercised on every real login.
- **Pure-markup components** (`App.razor`, layouts, `StatusMessage`) — no logic to assert.
- **NOT excluded:** every page with handlers (`Recipes`, `ProductDetail`, `Upload`, `GroceryList`,
  `Products`, `Home`, `Receipts`, `Reports`, `Settings`, `Accuracy`, `SpendInsight`), the chart
  components (render logic + honesty rules), `VoiceAgent`/`RecipeReadAloud`/`CookAlong`/
  `PushToTalk` (JS-interop boundaries mocked), `OnboardingBanner`, `SplitButton`,
  `BrandVarietyHint`, and all the 0% services named above.

## Phases

- **A — Measure** (this commit): baseline, coverage, worklist, policies drafted. ✅ 7/30
- **B — Audit passes, one project at a time** (Core → Llm → Web): read every test, verdict each
  (`keep` / `strengthen` / `rewrite` / `delete-candidate`), hunt the five classes, diagnose the
  EfAppSettings anomaly. No deletions land until the criteria above are signed off.
- **C — Coverage gaps in existing harnesses:** the three Llm advisors, the 0% Web services, the
  low partials (`MeteredChatClient` 45%), Core's last 21 lines where they're behavior.
- **D — Page harness:** bUnit project (`tests/ShelfAware.Web.UI.Tests` — separate project per the
  standing structural rule), then the named untested flows first: the "Ate it" notice + Undo, the
  picker's gated exits, the count panel, ProductDetail's split write-failure/reload-failure advice,
  GroceryList's `UsedOne`, Enter-submits-Quick-update. These are exactly the flows past reviews
  could only verify by hand. **✅ COMPLETE 7/31, in four sessions: harness + the six named flows
  (38 tests), the four pages' remaining surfaces + four more pages + components (+106 → 144),
  Upload/Reports/Settings (+43 → 187), and the voice components (+32 → 219 page tests; 1163 green
  total; 0 warnings). Three product bugs found and fixed along the way; every page with handlers,
  every chart, and every voice surface is covered. See the Phase D section below. Next: Phase E.**
- **E — Gauntlet:** `/pre-push`, Jordan's `/code-review`, merge. Tests-about-tests get the same
  review rigor as code.

## Worklist (filled during Phase B)

### ShelfAware.Tests (34 files — the Phase A count of 33 was off by one) — ✅ AUDIT COMPLETE 7/30
**Every file is a keep; zero deletion candidates. All five strengthen items were applied the same
day (+4 tests, 2 exact-status pins, 1 storage assertion → 514 green).** The strengthens share a
theme worth carrying into the Llm/Web passes: each was a test whose *fixture couldn't exercise the
claim its name or comment made* (a no-peeking test with no future signal to peek at; a "not stored"
test that discarded the object; a NotEqual where the exact state was known).
| File | Verdict | Notes |
|---|---|---|
| ReplenishmentPredictorTests | keep + strengthened ✅ | The model file: both sides of every precedence rule, ±1-day boundaries, real controls. Strengthen: (a) `SameDayTie_PurchaseWins` + `Restocked_ClearsAnEarlierOutNow` assert `NotEqual(Overdue)` — pin the exact expected status; (b) no edge test for burn-cycle pairing (a SECOND OutNow in one cycle must not add a sample; an OutNow before the first purchase is ignored). |
| PantryOnHandTests | keep + strengthened ✅ | Both pin cases from the 7/29 regression covered; complement test asserts absolute membership. Strengthen: no test for a STALE POSITIVE count with an overdue rhythm deferring to the rhythm (item drops out of on-hand). |
| StockLedgerTests | keep + strengthened ✅ | Full v4.1 attestation-clock semantics. Strengthen: `A_negative_attested_count_is_clamped_not_stored` asserts only the return value and discards the product — if Attest stored −2 it still passes; assert `QuantityOnHand == 0`. |
| TypicalPackageTests | keep | Full discriminator matrix incl. the pinned residual limit and noise filtering. |
| BacklogSignalsTests | keep | Boundary days both sides, cycle-closing edges, ranking, coverage disclosure, empty input. |
| IngredientMatcherTests | keep | Strong negatives; `IsSatisfied ≡ Covering` matrix is a deliberate anti-drift pin against re-implementation (the item-25 bug class), not a tautology. |
| ShoppingEstimatorTests | keep | CountNote gated both directions; trip summing; weight-vs-whole rounding; brand/variety grouping + case folding. |
| SpendForecastTests | keep | Count-moves-money cases; straddle, already-past, and degenerate-interval edges. |
| CountingAdviceTests | keep | Boundary tested both sides incl. exactly-10; null case reasoned. |
| SignalDateTests | keep | Day-keeping semantics incl. the same-instant-two-offsets case. |
| ReportEngineTests | keep | Honesty rules pinned as behavior: pool-never-drop with totals intact, tag overlap never stacks/totals, gaps-not-zeros, estimate ≠ paid, UI/engine rule agreement, TopN table-exemption regression. |
| CookAlongCommandsTests | keep | Both failure directions of the grammar; repetition can't MAKE a command; stop precedence; IsWorthAsking. |
| SpeechTextTests | keep | Refuse-to-guess cases pinned (bare C, F starting a word); realistic end-to-end step. |
| ListeningSettingsTests | keep | Geometric-mean gate with the arithmetic shown; monotonicity; failed-run keeps defaults; NaN clamp; cap-exceeds-silence invariant. |
| PredictionBacktestTests | keep + strengthened ✅ | Added the future-signal-invisible test — the existing restock test sits BEFORE the scored trip, so a peeking implementation passed it too. |
| ExtractionScorerTests | keep | Containment, plural fold, duplicate-consumption, both-miss-lists, errored-fixture aggregation. |
| PriceSeriesTests | keep | The limes regression; dominance + tie→most-recent; dateless ordering; bucket count. |
| PriceWatchTests | keep | Spend-weighting contrasted with the naive mean; disclosure floor; off-size/estimate exclusion. |
| RecipeTests | keep | Entity-level delegation; variant identity; no-mains-never-makeable. |
| ProductMatcherTests | keep | Store-brand-prefix regression; distinctive-token reorder still resolves. |
| QuantityFormatTests | keep | Singularization edges: case, "glass", one-letter unit, 1.5 stays plural. |
| ReportSpecUrlTests | keep | Full round-trip; defaults-omitted exact URL; garbage degrades to defaults; numeric enum smuggling refused. |
| RecipeNarrationTests | keep | The cache-key CONTRACT: segmentation + neighbour context, nulls beyond the ends. |
| ExpirationOutcomesTests | keep | Every rule tested both directions; consumption evidence beats the freezer override. |
| RecipeSuggestionStorageTests | keep | Round-trip plus derived members provably absent from the payload. |
| SwapCloudTests | keep | Self-swap excluded; merge subsumption of generic forms. |
| ReceiptTotalsTests | keep | Weight quantities; unpriced lines counted honestly; empty input. |
| SettingKeysTests | keep | Reflection guard with a teaching failure message — a new key can't default to surviving delete-my-data. |
| ProductPriceIndexTests | keep | Null/each one pricing bucket; own-bucket average; unpriced-size fallback blend. |
| SizeFormatTests | keep | Case/whitespace collapse; blanks → null. |
| VoiceCommandsTests | keep | Stop grammar both directions — statement-first utterances stay with the model. |
| TagVocabularyTests | keep | Trivial variants caught; genuine synonyms deliberately left to the LLM pass. |
| SizeBucketTests | keep | Each-family fold; no unit arithmetic between real sizes. |
| ImportModeTests | keep | Explicit mode > legacy bool > Smart default, incl. nonsense input. |

### ShelfAware.Llm.Tests (5 files + Fakes) — ✅ AUDIT COMPLETE 7/30, all keeps
**The class-1 fake-vs-real diff found one PRODUCT bug (below) and three fake drifts, all fixed:**
`FakePantryStore` now runs its quantity/purchase moves through the REAL `StockLedger` (can't drift
more permissive by construction, and an asserted zero files the same OutNow the real store files);
`AddPurchaseAsync`/`RecordSignalAsync`/`SetTrackingAsync` gained the real store's existence refusals.

| File | Verdict | Notes |
|---|---|---|
| Fakes.cs | strengthened ✅ | Real-ledger mirror + existence refusals (above). `CreateProductAsync` deliberately skips tag canonicalization — a documented simplification: the vocabulary rules are pinned in TagVocabularyTests/Web, and the fake records what the tool passed. |
| PantryChatTests | keep + 2 new pins ✅ | Comprehensive: every tool, error branches, history replay, screen context, JsonElement args, loop bail-out. Added: the dormant-relative refusal reply, and spoken-zero → OutNow through the store. |
| SpeechServicesTests | keep | Request-shape pins; fingerprint discipline all three ways (changes with anything audible, ignores the key, absent ≠ defaults); cancellation propagation. |
| ReceiptExtractorTests | keep | Retry semantics incl. wrong-shape-but-parseable; transport errors not retried; clamp/dedupe; suggestion carry-through. |
| RecipeAdvisorTests | keep | Parse coverage incl. steps trim/drop and null calories. The advisor's uncovered 32% is prompt-building + error paths — Phase C. |
| ChatClientFactoryTests | keep | Construction-only across three providers + rejection cases — honest scope for CI without keys. |

## Product bugs found by the audit (fixed on this branch — flag for Jordan's review)

2. **ProductDetail's failure advice could never be seen, and its catch crashed the circuit (7/31,
   fixed — found by the FIRST page test to exercise the path).** `OnParametersSetAsync` cleared
   `product` to null BEFORE creating its context, so when a post-write reload's context failed:
   (a) `ApplyQuantityAsync`'s catch dereferenced `product.Id` for its log → `NullReferenceException`
   thrown FROM THE CATCH — circuit down, write landed, no message; and (b) even without the NRE,
   `quantityError` renders inside the `product is not null` branch, so the page sat on "Loading…"
   forever with "Saved — don't repeat the change" set but unreachable. Item 27 shipped that split
   advice as the fix for exactly this scenario and noted the handlers were "review-verified rather
   than unit-pinned — no page harness exists"; the harness's first contact proved the review wrong.
   **Fix:** a product SWITCH still blanks to "Loading…" (B must not render under A's data), but a
   same-product REFRESH keeps the current view up while the load runs — a failed refresh now leaves
   a live page with the advice visible (derived rows clear after the fresh product is in hand; the
   not-found path nulls explicitly). Pinned by
   `A_failed_reload_after_a_landed_used_one_warns_against_repeating_it` (message AND count moved)
   and its failed-write mirror (message AND count unchanged). ⚠️ `ApplyExpirationAsync` had NO
   catch at all — same handler family, a reload failure there tore down the circuit. **Fixed in the
   Phase D tail session (7/31 later):** the same split-advice shape as its siblings, pinned both
   directions (`A_failed_write_advises_retry…` / `A_failed_reload_after_a_landed_date…`).

3. **Removing the LAST receipt swallowed the removal accounting (7/31, fixed — found by the
   Receipts page tests).** `removeResult` — the summary naming everything the removal undid
   (purchases, products removed vs kept, merchant matches untaught) — rendered inside the
   non-empty-list branch, so removing the only receipt flipped the page to "No receipts yet" and
   the accounting vanished. That's the most likely first-contact case (one test upload, then
   remove it), and the summary is the user's only record of what the removal did. The notice now
   renders outside the list branches. Pinned by
   `Removal_says_its_consequences_first_and_the_confirm_actually_removes`.

1. **A relative chat move edited a DORMANT count (7/30, fixed).** `StockLedger.AdjustByHuman` gated
   only on a null baseline, and dormancy keeps the number — so *"stop counting the rice"* followed by
   a habitual *"used two rice"* silently moved the frozen number the product page attributes as "you
   counted N on <date>", while the assistant replied "Noted — adjusted what's on hand." That broke two
   written invariants: `StopCounting`'s "frozen at its date," and GroceryList's "the only road to a
   false return is a concurrent stop-counting" (which was true only if dormancy refused). **Fix, per
   the design's own doctrine ("resuming starts from a fresh Attest"):** the ledger now refuses a delta
   on a dormant product (structural, same as `Move`'s gate), the store's relative branch refuses with
   the same reply shape as the null-baseline case, and the chat reply distinguishes it ("frozen
   history — say a fresh count"). Pinned at all four layers: ledger, store, chat, and the fake.
   UI paths were already safe (their controls only render for counted rows).

### ShelfAware.Web.Tests (26 files + TestDb/TestAuthDb/Fakes) — ✅ AUDIT COMPLETE 7/30 late
**Every file read end to end; every file a keep.** The standouts hold the same bar the Core suite set:
`HouseholdServiceTests` (real UserManager with the reasoning stated; the honest-scope comment on the
replaced-code race), `MealStockTests` (every walkthrough-era rule pinned incl. clamp-aware undo and
the same-name pair), `HouseholdIsolationTests` (the FindAsync claim *proven*, every v4 write path
walking the drill), `UserDataServiceTests` (the Kestrel-modeling stream, the zip-slip matrix, the
reflection every-table guard), `NullableInviteCodeMigrationTests` (unrecognized-column fail-loud),
`DemoDataSeederTests` (every hero run through the REAL engine). One-line verdicts for the rest:
ReportDataService (the regression pinned at its own layer + the same-day two-price join), ReceiptRemoval
(the full v4.1 ConfirmedAt matrix both directions), ReceiptConfirmation (idempotency + all trust
boundaries), ReceiptAutoConfirmer (Smart's undated tightening + the Auto duplicate exception),
AdditiveSchema (schema-parity fingerprints with the DEFAULT-clause reasoning), RecipeAdapter (ignored-
swap guard, re-rooting), ProductMerge (order-sensitive move + tenancy both directions + label matrix),
Byok (managed ignores browser overrides, SSRF lock), ReceiptDuplicateDetector (RawText-first, undated-
never), ReceiptStorage (portable separators, traversal + root refusals), HouseholdWriteGuard (both
detached-entity shapes), CurrentHousehold (fail-safe-not-guess + caching), SpeechCacheTrim (per-
household budget + orphan sweep), ProductDeletion (pins the FAILURE MODE first), ProductRename
(case-collision vs case-fix), PantryDbGuard (fail-fast wording). Helpers (TestDb/TestAuthDb/Fakes)
reviewed: honest — `FakeCurrentHousehold` mirrors the real refusal.
| File | Verdict | Notes |
|---|---|---|
| EfAppSettingsTests | rewritten ✅ | Was hunt-list class 6 (re-implemented its subject). Now drives the real `EfAppSettings`; gained the upsert update-in-place branch (row count pinned unscoped) and the null→"" distinction vs never-set. The unscoped-context test kept with a note: it's a context guarantee, retained beside the reads it protects. |
| MeteredChatClientTests | keep + strengthened ✅ | **The 45% suspect was NOT class 6** — the tests honestly exercised their subject; the uncovered half was the SUBJECT's streaming path (implemented so streaming can't bypass metering, unused today) + the metering-failure catch. Added: streamed-call records trailing UsageContent, streamed-call blocked at cap before the provider yields, and a metering write failure never fails the user's answer (disposed-DB simulation — the catch is now live-proven, not review-verified). |
| EfPantryStoreTests | keep + 1 new pin ✅ | Read during the dormancy-bug fix: strong file (asserted-zero → OutNow, clock semantics both directions, refusals with state asserted untouched). Added the dormant-relative refusal + frozen-pair assertion. |
| *(all 26 audited — verdicts in the section note above; the three changed files are the rows preceding this one)* | | |

## The second pass (7/30, per Jordan's "review it twice")

The audit's own hunt-list, re-applied to THIS SESSION's additions. Findings:

1. **The dormancy fix created an adjacent unpinned edge — fixed.** The gate refuses deltas on a
   dormant product; the promised way back in is a fresh `Attest`. Nothing pinned that the resume
   actually works, so a later symmetric-looking "fix" (making `Attest` respect dormancy too) would
   turn stop-counting into a one-way door with every test green — breaking exactly the recovery the
   chat refusal promises ("say a fresh count and I'll start again"). Now pinned at the ledger
   (`A_fresh_count_resumes_a_dormant_product`) and the store
   (`A_fresh_absolute_count_resumes_a_dormant_product`).
2. **Re-verified the new streaming-cap test discriminates:** the quota gate lives inside the async
   iterator, so it fires on first MoveNext; the test enumerates, and `Calls == 0` fails under a
   peeking implementation.
3. **Re-verified the metering-failure test exercises the intended branch:** Byok mode skips the
   up-front quota read, so the disposed connection throws inside `RecordAsync`'s try — the one the
   catch guards — not before the provider call.
4. **Re-verified the dormancy change against every `AdjustByHuman` caller:** the store pre-checks;
   the UI decrement buttons render only on counted rows; `MealStock` uses `TakeOne`/`Move` (already
   gated). No caller loses a behavior it had.
5. **Doc-vs-reality count check:** 26 Web test files + 3 helpers, 34 Core, 5 Llm + Fakes — the
   worklist headers now match what is actually on disk.

## Phase C — ✅ COMPLETE 7/30 late (+40 tests → 944 green)

Coverage after: **Core 99.0%** (was 98.8) · **Llm 91.9%, zero files at 0%** (was 79.4 with three) ·
**Web 27.2%** with every remaining 0% file a `.razor` page/component, `Program.cs`, or Identity
scaffolding — i.e. exactly the signed-off exclusions plus Phase D's harness targets. **No non-page
service remains untested.** What landed:

- **The three fail-soft advisors** (`AdvisorTests.cs`, 13 tests): parse contracts (lowercase, dedupe,
  self-exclusion, caps at 8/6, trailing-period trim), the NONE token, no-call short-circuits, and —
  the load-bearing one — a hallucinated tag NOT in the vocabulary reads as "no synonym" (the dedup
  must never coin the near-dupe it exists to prevent). Every advisor fails OPEN on a thrown client.
- **`AnthropicRecipeAdvisor.AdaptAsync`** (was 0% of the file's uncovered third): the prompt assembly
  the adapter's ignored-swap guard DEPENDS on — mandatory-swap line, curated also-works-as lists,
  seasoning markers, numbered steps, the won't-eat list — plus empty-recipes → null and the
  no-recipes-property parse branch.
- **`ReceiptSelfEval`** (6 tests): confirmed+verified filter, missing-audit-copy errors that receipt
  without spending a vision call, a failed extraction errors its fixture, a THROWING receipt costs
  itself and not the run, persist + round-trip, corrupt stored JSON reads as no-run.
- **`CachingTextToSpeech` error branches, live-proven**: a locked clip is a MISS the provider covers;
  an unwritable household folder costs money not correctness; the REAL `FindAsync` (the export's
  road in) honors keying incl. context and household.
- **Small services**: `CircuitVoiceCredentials` (managed ignores browser creds; null Apply CLEARS
  rather than falling back to the host's key), `VoiceCoordinator` (every subscriber awaited — a
  multicast Invoke awaits only the last), `HouseholdDbFactory` (scoped context; no household →
  refusal, never an unscoped context), `HouseholdClaimsPrincipalFactory` (claim present/absent —
  absent, never empty-string).
- **Core's last lines**: PurchaseCount metric (counts facts, price-blind, additive), Quarterly
  bucketing + Q labels, Weekly labels, four unasserted `ReportSpecRules` objections (unit-price
  never splits, ByRecipe is meal-only, meal metrics ignore pantry filters, TopN ≥ 1), the
  `BacklogFinding` columns the page links/renders from, and the estimate's sorted tag list.

**Accepted uncovered (with reasons):** `AiUsage.Id` (EF key auto-property), the two unreachable
`default:` guard throws in `ReportEngine` (`PurchaseValue`/`BucketStart` — enum-exhaustive switches),
and `Trim`'s unlistable-directory branches (startup housekeeping, logged-warning-only by design).

## Phase D — the page harness (7/31, named flows ✅ / page tail open)

**`tests/ShelfAware.Web.UI.Tests`** — bUnit 2.8.6 on net10.0, in the solution and CI (fourth test
step). **38 tests, 4 files, all green; 982 across the suite; 0 warnings.** The harness renders real
pages over the SAME stack the persistence suite trusts: `TestDb` + `FakeAppSettings` shared from
Web.Tests via `InternalsVisibleTo` (one definition, no drift), the real `EfPantryStore`, real
rename/merge services — fakes only at the AI/voice seams, whose real implementations Llm.Tests
already covers. AI-adjacent child components (PushToTalk, OnboardingBanner, the readers) are
stubbed; pure-markup children (SplitButton, BrandVarietyHint, LineChart) render for real.

**The load-bearing harness piece is `FlakyDbFactory`:** pages get contexts through it, and it can
fail the Nth create (`FailAfter`) or stall one on a gate (`HoldNext`). That models the boundary
production genuinely has — every load and save is its own short-lived context — which is what makes
two previously walkthrough-only classes honestly testable: the split failure advice ("didn't save —
try again" vs "saved — don't repeat it", asserted with the message AND the database state so the
branch is proven, not assumed), and the picker's gated exits (a real mid-flight interleave: hold the
pick at its context create, click the backdrop, release, then prove the pick landed once and nowhere
else). The store shares the factory, so `FailAfter = 0` kills the store's write and `= 1` kills the
page's reload after it.

| File | Pins |
|---|---|
| HomeQuickUpdateTests (6) | Enter-submits fix both sides (enabled on blank, disabled only while busy — held mid-flight via the fake's gate); blank send answers with a hint and never calls chat; success clears the box, failure keeps the text and styles as error; a navigating reply moves the page. |
| GroceryListUsedOneTests (5) | Suppressed row = CountNote + `linkish` "Used one", full cell text regex-pinned so a leaked due date fails; decrement is the household's own 1.25 lb median package (weight fixture — a hardcoded −1 fails) and never renews the clock; last package records the OutNow and re-anchors (the one delta that IS a look); the stop-counting race reloads in silence with the dormant pair frozen; the gate's other side (no count → date form, no control). |
| RecipesEatFlowTests (11) | One-tap commit with the exact take line; Undo reverses all three writes; failure advice split both directions with DB state; ambiguity → picker with live counts, nothing moves until answered; grounded-to-uncounted asks even with one candidate; click-away lands in Skipped and is SAID; both gated-exit interleavings (backdrop + Dismiss mid-pick); a pick that finds nothing lands in Skipped with no decrement and no error. |
| ProductDetailCountPanelTests (17) | All four confidence renderings (assert / Aging / Spent / distrusted zero) with each band's sentence pinned positively AND the neighbours' absent; suppression note + "Rhythm would ask" relabel, and the relabel's other side on a stale count; empty box ≠ zero; negative refused with its own message; concurrent stop-counting refusal; split failure advice on "Used one" both directions; stop-counting dormant copy with the pair kept; unit box relabels without touching the number; fast-mover nudge shown and (three ways) not shown; transient error clears on reload; panels reset on product switch. |

**Hunt-list pass over the new tests (the audit's own rigor, applied to itself):**
- **Mutation check on the crown jewels:** removed `eatBusy` from `ClosePicker`'s gate and disabled
  `DismissEat`'s — both interleaving tests FAILED, then passed again with the gates restored. The
  timing tests discriminate; they are not passing by accident of bUnit's dispatch order.
- **One one-sided pin found and closed:** the rhythm-row relabel had only its suppressed side
  ("Rhythm would ask" present); a mutant relabeling unconditionally would have passed. The Spent
  test now pins "Next buy" present / relabel absent when suppression is NOT active.
- Class 2 (null==null): every clock assertion compares against a seeded non-null instant; the two
  `Assert.Null`s are deliberate absolute pins. Class 3: the factory IS the context boundary; DB
  asserts go through a separate unscoped context. Class 6: none — every test drives rendered markup.

**Page coverage from this run alone** (whole-page lines, so the untested surfaces are visible):
GroceryList 55% · ProductDetail 50% · Recipes 39% · Home 35%.

### The tail session (7/31 later) — +106 tests → 144 in the harness, 1088 across the suite

The four pages' remaining surfaces, four more pages, and four components — same bar, same
harness. What each new file pins:

| File | Pins |
|---|---|
| ProductDetailExpirationTests (11) | Toggle-off = panel absent (dormant, not disabled); no-purchases teaching state; label caps the projection (min, never max) with the cap named on the rhythm row; passed label pins out WITH the override path on screen; Restocked-after-label override wording; save stamps every latest-day purchase and only those; Clear; Save disabled until the date changes (both sides); split failure advice both directions (the bug-#2 catch, live-proven); the racing-receipt-removal refusal. |
| ProductDetailEditFlowsTests (14) | Rename in place (button/Enter/Escape), collision keeps the editor open holding the refused value; merge candidates prefiltered by the product's own tag with Clear, variety pre-fill from the name diff, hidden-target reset, disabled-until-target, success navigates to the survivor; §13.6 purchase correction moves the count by the DIFFERENCE without renewing the clock, non-positive refused toward receipt removal, split advice both directions, Cancel; substitutes add/Enter/dedupe/remove/Suggest-adds-only-new. |
| RecipesSuggestAndAdaptTests (21) | Batch success renders + persists; failure keeps the old batch on screen AND in storage; empty answer ≠ success; restore recomputes availability live (a stale ✓ can't replay); corrupt snapshot discarded and cleared; Clear ideas; Save locks the card and persists ingredients/steps/calories; the won't-eat list provably reaches the model call; makeability both ways; run-out row offers Restocked and recovers, untracked row offers Track-it; Add-missing sends only the gaps and dedupes; Adapt reports and records the ask; swap cloud generates ONCE and caches (call-counted); curated stand-ins lead, bubble click adapts to that form; Pick-for-me pool = eaten AND makeable; delete; ?uses matches variants on their own ingredients with the original as reference (and out of the voice list); ?read starts the stubbed hands-free reader and strips itself; ScreenContext ordering + cleared on dispose. |
| GroceryListPageTests (7) | Extras trim/dedupe/sort/clear; concurrent-removal no-op; Restocked re-anchors without a purchase AND stales the "Copied" status; Untrack keeps history; Copy writes the shoppable text (qualifiers + extras) — asserted on the actual clipboard payload; Download names the file for today + the D-shortcut wiring; aisle-then-urgency ordering; still-learning disclosure. |
| HomeCardsTests (7) | §8 ordering (pinned outage > severity > date) with the 📌 note; chips count by status + the quiet state; Bought-today writes a Manual purchase (feeds the rhythm), Restocked writes only a signal (never a purchase) — both sides of the two-stream rule at page level; the expired card names the label as the reason; the runs-out-early habit panel from real burn cycles; learning hints count purchases only; the coordinator ping reloads. |
| SplitButtonTests (5) | Menu closed until the caret; selection/backdrop/Escape all close; dismissing ≠ choosing; primary fires + closes; Disabled reaches both halves. |
| BrandVarietyHintTests (3) | No usual → nothing renders; usuals + full breakdown; empty breakdown row omitted. |
| LineChartTests (4) | y-inversion with exact points; single point = no line, centered dot; flat series holds the middle (no divide-by-zero); screen-reader labelling. |
| OnboardingBannerTests (5) | Keyless pitch; Load-sample runs the REAL seeder + fires OnSeeded; dismissal spares the empty-catalog offer; dismissal with stock hides fully; a stored dismissal isn't re-nagged. |
| ProductsPageTests (12) | Add stores name/category/unit + clears; blank refused; exact duplicate BLOCKED with a link (no "anyway" exists — mutation-checked); fuzzy asks with both answers pinned; tag cloud + untagged complement exclusivity; search/status filters; multi-aisle deep link named on screen with unknown names skipped; Out files the outage and the row reads due TODAY; tracking checkbox writes through; delete asks first and no means no; the suppressed row speaks CountNote (the third surface of that rule). |
| ReceiptsPageTests (6) | Totals from lines with unpriced lines disclosed; newest-first, Discarded invisible; pending chip points back at review; verification offered only with an audit image and toggles through the DB both ways; removal consequences-first with Cancel whole (found bug #3); no-provenance refusal says why. |
| SpendInsightTests (6) | Dominant-size ticker with the size named (the $/bag-vs-$/lime rule); grocery change semantics both directions; spend windows sum paid prices; the forecast counts an asking rhythm and goes to ZERO under a fresh count (the count-aware start, seen from the page); no-history teaching state. |
| AccuracyPageTests (5) | Both empty states teach; pass/fail against targets per stat; errored fixtures render their error, not vanish; the backtest scores live history; the self-check renders its stored run with a THROWING extractor proving grading never happens on load. |

**Hunt-list pass over the tail (same six classes):** mutation-checked the duplicate guard
(`duplicateIsExact = false` → the exact-block test fails; restored → passes); the two new
split-advice pairs assert message AND database state; every clock assertion compares a seeded
non-null instant; the spend-window test's calendar arithmetic is fixture math (noted in-test), the
summing/valuation is the assertion. Known one-sided residual, accepted: Products' tag/untagged
exclusivity is pinned in one direction (tag → untagged); the mirror shares the same two-line
implementation.

**Coverage after the tail** (per page, this project's run alone): SplitButton/LineChart/
BrandVarietyHint 100% · SpendInsight 96% · Home 96% · GroceryList 92% · Products 90% ·
Recipes 89% · ProductDetail 84% · OnboardingBanner 84% · Accuracy 71% · Receipts 70%. The
sub-90 remainders are the deliberately unexercised roads: Accuracy/Receipts' self-eval RUN click
and fixture export (service-tested in Web.Tests; the page pins the cost discipline), ProductDetail's
merge-concurrency catch, OnboardingBanner's JSException fallbacks.

**The Phase D tail (still open after the second session):** Upload, Reports, Settings, the chart
components, and the voice components. The first four closed in the third session below.

### The third session (7/31 later) — Upload, Reports, Settings (+43 → 187 in the harness, 1131 green)

The three biggest pages, over their REAL stacks: Upload runs the actual ingest pipeline (storage,
confirmation service, auto-confirm router, duplicate detector, removal — only the extractor is a
canned-results queue, and PDFs skip the browser resize so bUnit drives file selection through
`InputFile.UploadFiles` for real); Reports runs the real `ReportEngine`/`ReportDataService` (so
the chart family renders against real results); Settings runs the real `HouseholdService` over a
real `UserManager` (TestAuthDb) with bUnit's authorization context carrying the household claim.

| File | Pins |
|---|---|
| UploadPageTests (18) | The mode hint before upload (Smart default / Auto / Review silent); the queue separating readable rows from failed reads (Retry visible — a failed read used to vanish); the queued-duplicate chip; review pre-fill by TRUST ORDER (alias 🔗 > model suggestion > matcher > create-new, one select each); undated demands a date, dated offers correction; the exact-duplicate warning on review; Confirm-all through the one path with the REVIEWED date and the ticked assertion riding through — and its unticked mirror staying false; the lost-race confirm reading "already recorded" with NO Undo (once, not twice, in the DB); Undo reversing the confirm it is about; Discard; Retry re-extracting from the audit copy (fail → still-couldn't message, succeed → straight into review); the Expires column gated on the toggle both ways; both tag-dedup stages (near-dup asks → Use takes the canonical; synonym asks → Add-anyway keeps the user's word); Smart confirming a trusted single PDF (with the audit copy proven on disk) vs falling through to review for a novel product; a stack reading as separate receipts with per-file fates (one failure doesn't sink the batch); combine-as-one sending BOTH pages in ONE extractor call. |
| ReportsPageTests (13) | The teaching empty state; the report card's month arithmetic, aisle chart + always-present data table, ranked top items; presets from the URL with nonsense falling back (and the pill lit); the builder surfacing `ReportSpecRules` objections and holding Run until answered; running writes the spec into the URL (THE serialization); a deep link seeding the form and running without a click; save/delete of saved reports (the pill IS the stored query); **the ⚠️-commented invalidation rule — a pantry change recomputes the RESULT (old spec, new facts) without re-seeding the FORM mid-edit (mutation-checked: removing `customResult = null` from the handler fails the test)**; waste watch gated on the toggle and reading "worth checking", never "wasted"; piling-up asking the engine + disclosing thin outage evidence; eat charting logged meals and pricing mains at today's receipts (+ waist reading the same meal's calories); price watch refusing an overall claim below its floor while still listing the movers; the gap report's burn-vs-rebuy arithmetic pinned to the day. |
| SettingsPageTests (11) + SettingsManagedModeTests (1) | Import-mode radios reading and writing the household setting (Smart default checked); the expiration toggle writing through with the dormant-dates reassurance both sides; the recipe-add preference; usage rendering recorded days + the honest zero state; BYOK save applying to THIS circuit without a reload (`CircuitAiSettings.HasKey` flips) and Forget clearing back to keyless; per-provider configs remembered across a switch only once SAVED (the test originally expected unsaved text to survive — the expectation was wrong, fixed to the real contract); the calibration wizard refusing kindly on a browser that can't listen; the invite lifecycle (no code until asked, single-use default with the status sentence, Copy putting the real code on the clipboard, Replace invalidating, Clear returning to "—" — each verified against auth.db); member removal asking first, never offered for yourself, really detaching the account; rename arming only on change; delete-my-data arming only on the exact word DELETE (case-sensitive both sides) and really deleting through `UserDataService`. |

**Coverage (this project's run):** ReportDataTable/TimeSeriesChart 100% · BarChart 98% ·
Reports 85% · Upload 85% · ChartLegend 81% · Settings 67%. Settings' remainder is the calibration
wizard's mic-session interior — it needs a real browser microphone and its measured math is
Core-tested (`ListeningSettingsTests`); the page pins the refusal path. Upload's remainder is the
image-resize branch (`RequestImageFileAsync` is browser JS; PDFs cover the pipeline) and the
oversize-file guard.

**Still open after the third session:** only the voice components — closed in the fourth session
below, which completes the phase.

### The fourth session (7/31 latest) — the voice components (+32 → 219 in the harness, 1163 green) · PHASE D ✅

The four JS-interop-heavy surfaces, driven through bUnit's scripted modules with the speech seams
faked (the REAL `ElevenLabs*` implementations are pinned in ShelfAware.Llm.Tests). The loop
harness convention that makes the async listening loops deterministic: the fake browser's capture
is STICKY (every window "hears" the same bytes) and **`FakeSpeechToText` is the sequencer** — a
queue of transcripts whose exhausted-queue backstop answers "stop listening", so a loop under
test always winds down instead of spinning. A first draft tried sequencing with fresh pending JS
handlers instead; bUnit reuses the handler for an identical setup, the test failed, and the
rewrite landed on the queue convention — which also moved the resume test's assertion to where
the claim actually lives (the history REPLAYED to the brain, not the panel paint).

**Testability seam:** `ShelfAware.Web` gained `InternalsVisibleTo("ShelfAware.Web.UI.Tests")` and
the four JS-interop DTO records (`PushToTalk.VoiceCapture`, `VoiceAgent.VoiceCapture`,
`RecipeReadAloud.SessionResult`/`HeardResult`) went `private` → `internal` — the tests must
construct them to script the browser side, and they mirror private JS shapes that are nobody
else's contract. Same precedent as Web.Tests' IVT.

| File | Pins |
|---|---|
| PushToTalkTests (8) | The one-shot state machine end to end (hold → speak → release → transcript shown → reply read back → OnApplied → Idle); silence coached without waking the brain (no STT, no chat); unreadable audio apologizes without a chat call; a failed chat turn styles as error and skips the refresh; a navigating reply moves AFTER the spoken confirmation; a failed read-back is a bonus lost, not a turn lost (reply on screen, no play, machine home); unsupported browser disables and says why; the keyboard hold's auto-repeat guard (three keydowns, ONE recording). |
| VoiceAgentTests (8) | A conversation heard, answered WITH the screen context, and ended by the plain-code stop phrase (goodbye spoken, brain never woken for it); refused mic reports and leaves the launcher; silence winds down instead of holding the mic open; **an ordinary navigation keeps the agent listening so commands chain** (the backstop stop heard on the destination page is the proof) vs **a hand-off standing it down first** (no goodbye, recorder released — mutation-checked: disabling the hand-off branch fails the test); a non-navigating change pings `PantryChanged` exactly once; resume REPLAYS the kept history to the brain while the launcher starts clean; a stand-down mid-turn releases the mic at once and the held turn still lands in the history the hand-back replays. |
| CookAlongTests (5) | The agent's dynamic variable carries the recipe with ORDER-ordered renumbered steps + the calorie estimate; mode/transcript callbacks drive the panel; a session that can't start reports and fires OnUnavailable exactly ONCE (a second failure signal must not fight the parent's fallback swap); ✕ ends without resuming the assistant; both hand-back roads (button + spoken) stop the session FIRST then resume. |
| RecipeReadAloudTests (11) | Narration speaks EXACTLY `RecipeNarration`'s segmentation with its neighbour contexts (the cache-key contract the export depends on) and streams — intro playing before the steps append in order; a failed intro names the key; a failed STEP stops loudly, never skips (the listener's hands are busy); buttons drive the player and the highlight follows OnIndex, never guesses; ✕ vs Back-to-assistant; hands-free asks the agent to stand down before opening ears and the grammar moves the reader for free (no chat call for "next"); no-ears and denied-mic keep the buttons working; a question falls through to the brain with the recipe + "listening, not reading" + go_to_step steering as context, the answer spoken, and "stop listening" putting the mic down while the recipe stays; the brain's `StepTarget` moves the reader with the step AS the answer (reply not spoken); "hold on" ignores kitchen chatter (zero chat calls) until a command releases it. |

**Coverage:** CookAlong 96% · VoiceAgent 89% · PushToTalk 89% · RecipeReadAloud 87%. The
remainders are circuit-teardown catches and the JS-side branches (pause/resume against a real
`<audio>` element, the mic-session lifecycle) that only a real browser exercises — the class the
audit's exclusion policy anticipated for these components.

**Phase D is complete.** 219 page/component tests; every page with handlers, every chart, every
voice surface. Cumulative page-harness yield: three product bugs (the unreachable split advice +
catch NRE, its expiration sibling, the swallowed removal accounting), two wrong test expectations
fixed honestly, and four mutation checks proving the sharpest gates discriminate. Next: Phase E —
`/pre-push`, Jordan's `/code-review`, merge.
