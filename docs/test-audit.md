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
  could only verify by hand.
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

## Phase C shortlist (confirmed by the full pass)

- The three 0% Llm advisors (substitutes / alternatives / tags) — construction + fail-soft pins.
- `AnthropicRecipeAdvisor`'s uncovered 32% (prompt-building, error paths).
- `CachingTextToSpeech`'s uncovered error branches (corrupt sidecar / IO failures).
- 0% Web services: `ReceiptSelfEval` (92 lines — the biggest genuinely untested service),
  `CircuitVoiceCredentials`, `VoiceCoordinator`, `HouseholdClaimsPrincipalFactory`, `HouseholdDbFactory`.
- Core's last 21 uncovered lines, where they are behavior.
- (`ByokChatClient`'s streaming delegate is now covered via the MeteredChatClient streamed test.)
