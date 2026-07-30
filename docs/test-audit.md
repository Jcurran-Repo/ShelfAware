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

### ShelfAware.Tests (33 files, 510 tests) — 10 of 33 audited 7/30
| File | Verdict | Notes |
|---|---|---|
| ReplenishmentPredictorTests | keep + strengthen | The model file: both sides of every precedence rule, ±1-day boundaries, real controls. Strengthen: (a) `SameDayTie_PurchaseWins` + `Restocked_ClearsAnEarlierOutNow` assert `NotEqual(Overdue)` — pin the exact expected status; (b) no edge test for burn-cycle pairing (a SECOND OutNow in one cycle must not add a sample; an OutNow before the first purchase is ignored). |
| PantryOnHandTests | keep + strengthen | Both pin cases from the 7/29 regression covered; complement test asserts absolute membership. Strengthen: no test for a STALE POSITIVE count with an overdue rhythm deferring to the rhythm (item drops out of on-hand). |
| StockLedgerTests | keep + strengthen | Full v4.1 attestation-clock semantics. Strengthen: `A_negative_attested_count_is_clamped_not_stored` asserts only the return value and discards the product — if Attest stored −2 it still passes; assert `QuantityOnHand == 0`. |
| TypicalPackageTests | keep | Full discriminator matrix incl. the pinned residual limit and noise filtering. |
| BacklogSignalsTests | keep | Boundary days both sides, cycle-closing edges, ranking, coverage disclosure, empty input. |
| IngredientMatcherTests | keep | Strong negatives; `IsSatisfied ≡ Covering` matrix is a deliberate anti-drift pin against re-implementation (the item-25 bug class), not a tautology. |
| ShoppingEstimatorTests | keep | CountNote gated both directions; trip summing; weight-vs-whole rounding; brand/variety grouping + case folding. |
| SpendForecastTests | keep | Count-moves-money cases; straddle, already-past, and degenerate-interval edges. |
| CountingAdviceTests | keep | Boundary tested both sides incl. exactly-10; null case reasoned. |
| SignalDateTests | keep | Day-keeping semantics incl. the same-instant-two-offsets case. |
| *(23 files remaining — next session)* | | ReportEngine, CookAlongCommands, SpeechText, ListeningSettings, PredictionBacktest, ExtractionScorer, PriceSeries, PriceWatch, Recipe, ProductMatcher, QuantityFormat, ReportSpecUrl, RecipeNarration, ExpirationOutcomes, RecipeSuggestionStorage, SwapCloud, ReceiptTotals, SettingKeys, ProductPriceIndex, SizeFormat, VoiceCommands, TagVocabulary, SizeBucket, ImportMode |

### ShelfAware.Llm.Tests (5 files + Fakes, 93 tests)
| File | Verdict | Notes |
|---|---|---|
| *(pending Phase B)* | | |

### ShelfAware.Web.Tests (28 files + TestDb/TestAuthDb/Fakes, 286 tests) — 1 of 28 audited 7/30
| File | Verdict | Notes |
|---|---|---|
| EfAppSettingsTests | rewrite | Re-implements its subject (hunt-list class 6). Keep the tenancy pins but route them through the real `EfAppSettings`; add the upsert update branch and `value ?? ""` null case. Check overlap with `HouseholdIsolationTests` while there. |
| *(27 files remaining)* | | |
