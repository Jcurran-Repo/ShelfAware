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
- **⚠️ Anomaly, first stop of the vacuous-test hunt:** `Data\EfAppSettings.cs` reads **0%** while
  `EfAppSettingsTests.cs` exists and passes. Either the tests exercise a different type, the
  implementation moved, or the tests are vacuous. Diagnose before anything else in Phase B —
  whatever the answer, it calibrates how much to trust file-name↔test-name pairing everywhere else.

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

## Deletion criteria — PROPOSED, awaiting Jordan's sign-off

A test is deleted only when at least one of these holds, named per test in the commit message:

1. It cannot fail (tautology / vacuous / pins nothing).
2. It exactly duplicates another test's coverage — the stronger one stays.
3. It pins an implementation detail no behavior depends on, AND the behavior it was gesturing at
   is covered (or gets covered in the same commit).
4. Its subject no longer exists.

Never deleted for being slow, inconvenient, or red. A red test is a finding, not a nuisance.

## Coverage-exclusion policy — PROPOSED, awaiting Jordan's sign-off

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

### ShelfAware.Tests (33 files, 510 tests)
| File | Verdict | Notes |
|---|---|---|
| *(pending Phase B)* | | |

### ShelfAware.Llm.Tests (5 files + Fakes, 93 tests)
| File | Verdict | Notes |
|---|---|---|
| *(pending Phase B)* | | |

### ShelfAware.Web.Tests (28 files + TestDb/TestAuthDb/Fakes, 286 tests)
| File | Verdict | Notes |
|---|---|---|
| *(pending Phase B)* | | |
