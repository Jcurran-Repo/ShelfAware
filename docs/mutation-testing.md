# Mutation testing

Tests prove code. Mutation testing proves the tests. This project treats "green" as necessary but
not sufficient — a test that passes whether or not the code is correct is worse than no test,
because it reads as coverage while pinning nothing (CLAUDE.md item 34: *green is what the defect
produces*). This doc records how we keep proving the tests aren't pointless, and tracks the score
over time so a regression is visible.

## Two layers, on purpose

Mutation testing happens at two altitudes here, and they are complementary — neither replaces the
other:

1. **Manual, per-change (surgical, authoring-time).** When a test is written for a specific
   behaviour, the author deliberately breaks *that* behaviour in the production code, confirms the
   test (ideally only that test) goes red, and reverts. It is fast, targeted, and catches a vacuous
   test the moment it is written. This is the discipline recorded throughout CLAUDE.md and the commit
   history as "mutation-checked — flipping X fails exactly test Y". **It stays. Automation does not
   retire it** — it only checks the mutations the author thought to make.

2. **Automated, exhaustive (periodic audit).** [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/)
   generates every mutation it can across a project — flip every comparison, swap every boolean,
   blank every return — runs the suite against each, and reports which mutants *survived*: lines you
   could break with no test noticing. This finds the gaps the manual pass didn't think of, and gives
   a single quantified number to hold the line on.

## Scope: Core first

Automated mutation testing runs against **`ShelfAware.Core`** (tested by `tests/ShelfAware.Tests`).
Core is the pure-logic heart of the app — the prediction engine, the matchers, the formatters, every
"one definition" rule — with fast, EF-free, browser-free tests. It is where a surviving mutant is
most alarming and where a run is fastest.

The other projects (`Llm`, `Web`, `Web.UI`) are **deliberately out of scope for now**: their tests
are much slower per run (real SQLite, bUnit rendering, faked AI seams), and mutation testing
multiplies that cost. They keep the manual per-change discipline. Expanding automated coverage to
them is a later decision, made once Core's is established and holding.

## What "100%" means here — the honesty rule

The target for Core is a **100% mutation score**, and it is only worth having if it is honest. 100%
here means: **every mutant is either killed by a test, or explicitly annotated in the code as
equivalent, with a one-line justification.** An *equivalent mutant* is one that genuinely cannot
change observable behaviour — e.g. a bound already fully covered by a neighbouring guard, so flipping
it changes nothing any test could see. Those are documented, not tested-around:

```csharp
// Stryker disable once equality : `>=` and `>` are equivalent here — <n> is already guaranteed > 0 by the guard above.
```

A bare 100% reached by quietly ignoring inconvenient mutants is the lie. A 100% where every exclusion
is a stated, reviewable decision is the same "known residual, stated" discipline used everywhere else
in this repo — and it makes the number mean something. Every annotation lands in a diff and is
reviewed like any other change.

### Known limitation: compile-error / "safe mode" mutants

Some mutations produce C# that does not compile — most often `CS0165 (use of unassigned local
variable)`, where a mutation removes an assignment path the compiler's definite-assignment analysis
requires. **This is a fact about the mutation, not about our code: all of Core compiles and every
Core test passes.** Stryker cannot test a mutant that will not build, so it marks it `CompileError`
and excludes it from the score (neither killed nor survived — it cannot exist).

When Stryker cannot isolate which mutation in a method caused the compile error, its "safe mode"
removes *all* mutants in that method. As of the first baseline that affects a small number of methods
(e.g. `StockLedger.Move`, `BacklogSignals.Find`, `PredictionBacktest.Run`). Those methods remain
covered by ordinary unit tests — they simply cannot be mutation-*tested* here. This is a Stryker
limitation, recorded so the tracked score is honest about what it does and does not cover.

⚠️ One sharper consequence, found by the adversarial review below: the rollback can also sweep a
**compilable** mutant into the CompileError bucket alongside its genuinely-uncompilable siblings in
the same expression — and such a mutant can be a live SURVIVOR the score silently excludes. See rule
4 of the adversarial-review rules.

## Scope-tuning outcome (2026-08-25): nothing honest to exclude

Before the sweep, a per-file audit of the baseline (the JSON report) settled whether any Core files
should be excluded from mutation as non-behavioral. The answer was **no**, and the reasoning is worth
keeping because "exclude the boring files" is the easy way to fake a high score:

- The pure EF **entity / DTO / enum** types (`Product`, `ReceiptLine`, `PurchaseEvent`, `InventorySignal`,
  …) contribute **zero surviving mutants** — whatever they declare is already pinned by tests at its
  point of use. Nothing to exclude, nothing gained by excluding.
- The `I*.cs` "**interface**" files are **not** pure contracts: each houses its result-record DTOs
  (`ChatResult`, `ExtractionResult`, `ShelfCensusResult`, `RecipeSuggestion`, …), and those carry real
  computed logic — e.g. `RecipeSuggestion.ToGrab => Ingredients.Where(i => i.IsMain && !i.Have)` and
  `SuggestedIngredient.Have => MatchedProduct is not null`. Excluding those files would **hide** a
  genuine `&& !` decision from the audit. They stay and get tested.

So **no `mutate` exclusions are configured.** The honest target is the full set — 601 survived + 45
no-coverage ≈ **646 mutants** — each to be killed by a test or annotated equivalent with a reason.

## Sweep worklist (worst first)

Closed file-by-file; each file re-run scoped (`dotnet stryker --mutate "**/<File>.cs"`) until it reads
100%, then a tracked row below. Top of the list as of the 2026-08-25 baseline (survived + no-coverage):

| File | To close | Killed | File | To close | Killed |
|------|----------|--------|------|----------|--------|
| Speech/SpeechText | 142 | 131 | Reporting/ReportSpec | 18 | 61 |
| Speech/CookAlongCommands | 86 | 59 | Speech/Utterance | 17 | 50 |
| Recipes/IngredientMatcher | 69 | 45 | Reporting/PriceWatch | 13 | 28 |
| Reporting/ReportEngine | 54 | 106 | Speech/ListeningSettings | 11 | 28 |
| Prediction/ReplenishmentPredictor | 45 | 176 | Chat/ProductMatcher | 11 | 31 |
| Tagging/TagVocabulary | 42 | 37 | Reporting/ReportSpecUrl | 7 | 45 |
| Recipes/RecipeTagVocabulary | 28 | 3 | + a long tail of files with ≤ 4 each (ShoppingEstimator, ExpirationOutcomes, CensusPlan, AiPricing, SizeBucket, the result-DTO factories, …) |||
| Onboarding/TourScript | 26 | 34 | | | |
| Evaluation/ExtractionScorer | 25 | 53 | | | |

The top 5 files hold ~400 of the ~646. This is a multi-session arc; the score-history table records
each session's progress.

### The adversarial review of the first two closed files (2026-08-25) — and the rules it set

An independent reviewer was briefed to REFUTE the first two files' 100%: strip every annotation and
re-run Stryker, hand-apply each "equivalent" mutation and hunt a killing input, judge every new test
for domain reachability, re-derive the math. What it found is why this pass is now mandatory:

- **A false equivalence claim hiding two uncovered killable mutants (HIGH).** The author's annotation
  on `IsSameFood`'s non-empty guards claimed `> 0`/`>= 0` "agree everywhere", with a proof that was
  factually wrong. The killing input: `IsSameFood("pack", "packs")` — a `Trivial` word colliding with
  its non-Trivial plural through `Singular`, where `Covers()` of an empty need is vacuously true. With
  either guard loosened the FULL 632-test suite stayed green. The annotation is gone; the collision
  tests pin both directions.
- **A live survivor hidden in the CompileError bucket (HIGH).** An `&&`→`\|\|` mutant on the same line
  compiled fine but was swept into `CompileError` by Stryker's rollback of genuinely-uncompilable
  siblings in the same expression — so the committed "100%" silently excluded a real surviving mutant.
  The collision tests kill it too.
- **Both `disable once all` scopes were over-broad (MED).** Each suppressed the whole line, but the
  written reasons argued ONE mutation each; 5 killable mutants rode along unexamined. Narrowed to the
  argued categories, the freed mutants are now Killed.
- **What survived attack** (a survived attack is the evidence an annotation is sound): both
  ProductMatcher equivalence cores, the plain-s length-guard annotation (exhaustive 16,200-token
  probe found zero observable difference), and the exact-0.5 threshold test's arithmetic (bit-exact
  in IEEE doubles, no earlier rule short-circuiting).

**Rules adopted from this, binding on the rest of the sweep:**

1. **`Stryker disable once all` is banned.** An annotation names the NARROWEST mutator list its
   reason actually argues (`Logical`, `Statement`, `Equality`, …) — anything broader suppresses
   mutants nobody examined.
2. **"Domain-equivalent" annotations are rejected.** A mutant killable only by inputs the domain
   "never produces" still gets a test, not an annotation: a test is self-verifying, an annotation is
   trusted prose — and this very review refuted one "the domain never produces X" claim, on a
   codebase where a prior "unreachable" (junk product names) had already been proven reachable.
   Such tests carry a comment naming what they pin and when to delete them.
3. **Every equivalence annotation gets adversarially attacked before it ships.** Author reasoning
   alone produced one false proof out of five claims — a 20% error rate on exactly the claims no
   test checks.
4. **The CompileError bucket is not purely "cannot exist".** Rollback can sweep COMPILABLE mutants of
   an expression into it alongside their uncompilable siblings; after changing annotations or nearby
   code, re-check that bucket before trusting the score.

### The adversarial review of ReplenishmentPredictor's 7 annotations (2026-08-26) — the OWED pass

Rule 3 makes an adversarial attack on every equivalence annotation mandatory; the predictor's shipped
without one (the agent died on the session limit). Re-run here: each annotation's equivalent mutant
hand-applied and the full suite run (a stronger check than Stryker's coverage-filtered per-mutant run),
then each mutant reasoned about for a killing input the suite lacks.

- **All 7 equivalence claims are SOUND** — every equivalent mutant survived, and reasoning found no
  killing input (median is provably ≥ 1, so `median >= 0` and the trim's `>= 0` can't differ from `> 0`;
  `labelStatus >= status` is a no-op self-assign; the `||`→`&&` early-out yields `[]` by the loop in both
  single-empty cases; `First()`→`FirstOrDefault()` is guarded to ≥ 1 group; `Median`'s `OrderBy` is
  sort-invariant, odd and even). **0/7 false, versus the 20% false-proof rate the first review measured.**
- **One over-broad scope FIXED (the same class the first review caught).** `DominantSize`'s
  `disable once Linq` sat above the whole multi-line `return purchases…First()` statement, so it silently
  suppressed the entire chain's Linq mutants — `OrderByDescending(Count)→OrderBy`, `ThenByDescending`,
  `Max→Min`, the inner `FirstOrDefault` — while the comment CLAIMED "inner mutants live on their own lines
  and stay live". Hand-applying `OrderByDescending(x => x.Count)→OrderBy` proved them killable (2 tests
  red). Fixed by extracting the chain to a local so the annotation covers only the isolated
  `return byDominance.First();`; the freed mutants are now Killed (176 → 208), 100% held.
- **One mis-cited test FIXED.** The `labelStatus > status` annotation named `TheCap_NeverCalmsAWarningDown`
  as pinning its killable `<` sibling — but that test sets the label BEYOND the rhythm's due date, so the
  cap branch is skipped and L245 never runs; it can't pin the mutant. The sibling is genuinely killed (by
  7 other cap-window tests); the citation now names `TheLabel_HardCaps_ACadenceDueDate` (a verified
  Stocked→DueSoon escalation the `<` mutant breaks). **Lesson: verify a cited test actually executes the
  annotated line — a plausible name is not coverage.**
- **The bundled-Equality residual, stated.** Four annotations (`median > 0`, `labelStatus > status`,
  `trimmed.Count > 0`, the `StockUpFactor` compound guard) each disable one operator whose Equality
  mutants are BOTH an equivalent (`>=`/`<`) AND a killable inversion — Stryker.NET cannot disable a single
  replacement, and separating them would mean deleting genuine divide-by-zero / empty-list defensive
  guards. So the killable sibling is Ignored (not Killed) rather than freed. Every one was hand-verified
  killed by its NAMED test, so the residual is honest — but it is a residual: the weekly Stryker gate does
  not protect those siblings' coverage the way it protects a Killed mutant (the ordinary suite still does).
  Unlike `DominantSize` (a multi-line statement, cleanly separable) these are single inseparable operators,
  so the bundle is kept per the doc-blessed pattern above, with citations verified.

### Files closed to 100% (scoped `--mutate`)

| Date | File | Mutants closed | How |
|------|------|----------------|-----|
| 2026-08-25 | Chat/ProductMatcher | 11 (9 survived + 2 no-coverage) | 4 targeted tests + 2 equivalent-mutant annotations. The tests pin the IDF-weighted scorer's exact behaviour: `IdentityKey(null)`, the inclusive 0.5 threshold + first-wins tie, `max(qWeight,pWeight)` as the denominator (a diluted overlap stays below the line), and an absent token counting at full `MaxIdf` weight. Both annotation CORES survived adversarial attack (the `\|\|`→`&&` the downstream guards absorb; the empty-name `continue` that only skips a guaranteed-zero score) — but both were originally scoped `disable once all`, which also suppressed 5 killable mutants the reasons never argued; narrowed to `Logical`/`Statement` after the adversarial review, the freed mutants now Killed (33 → 40). |
| 2026-08-25 | Prediction/ReplenishmentPredictor | 45 (79.64% → 100%) | 21 targeted tests + 7 annotations, written under the adversarial-review rules. The tests close real doctrine gaps: §6.6's same-day tie in `BurnCycles` (strict `>`) was comment-only — now engine-pinned; the newest-active-signal rule and the same-instant OutNow tie-break had no tests; the `Basis` wording ("lasts" vs "every") was unpinned; the trim's exactly-3-intervals and exactly-3×-median boundaries, the even-count IQR halves, the label window's 20%/spread/minus-one arithmetic, an equal-to-due label NOT reporting as a cap, the count's boundary days (run-out day inclusive; day 91 trips the age fallback; a fresh zero believed and projection-less), and the purchase-less-product-with-expirations-on crash guard. Several survivors existed because earlier fixtures were non-discriminating: every stock-up test used typical-trip 1 (where × and ÷ coincide), the size tie-break fixture won under Max AND Min — the new fixtures discriminate. Annotations each name their suppressed category siblings and the covering test. |
| 2026-08-25 | Recipes/IngredientMatcher | 69 (46% → 100%) | ~44 killed by one comprehensive test over the whole `Trivial` modifier set; the rest by targeted tests for the `Singular` suffix rules (ies/oes/ses/xes/ches/shes/plain-s + the length boundaries), the digit-token filter (`v8` kept, pure numbers stripped), punctuation splitting, the mutual-coverage `&&`, the trivial-plural collision guards, and the blank-grounded-name guard (fixture: the creatable junk name `"!!"`, not an impossible empty-named product). ONE equivalent-mutant annotation stands (the plain-s length guard — survived an exhaustive 16,200-token adversarial probe). A second annotation on `IsSameFood`'s non-empty guards was **REFUTED** by the adversarial review — see below — and became the collision tests (104 → 113 killed). |
| 2026-08-26 | Recipes/RecipeTagVocabulary | 10 (68.75% → 100%) | A thin wrapper over `TagVocabulary.Canonicalize` (the shared dedup policy) — the existing tests already covered the apply/skip/canonicalize/batch paths, leaving only the unpinned `Seed` strings and the vocab-teach step. Pinned the exact `Seed` set, and added a test that a newly-coined tag joins the vocabulary (kills the `!Any`/`Any→All`/`Add` mutants on the teach step, which the recipe-side tests had only ever read through `recipe.Tags`, never the vocabulary). Test-only. |
| 2026-08-26 | Tagging/TagVocabulary | 32 (59.49% → 100%) | The `Seed` vocabulary pinned as an exact set; the near-dup path exercised by substitution / insertion (candidate longer) / deletion (candidate shorter) / two-apart, so the Levenshtein length-ordering and insertion branches are all hit; the `Normalize` plural-drop pinned in every direction ("Boxes"→"Box" needs the drop; "Card"↛"Cat" forbids dropping a non-plural letter; "gas"/"gasp" pins the length>3 floor); and `Canonicalize`/`ApplyTags` for the exact/near-dup/coin-new and add-to-vocab/skip-dup/batch-dedup paths. Three behaviour-preserving simplifications removed genuinely-dead code and its mutants: the `if (a == b) return true` base case (the caller already returned on an exact match), and the `j < longer.Length` loop bound (the `|diff| ≤ 1` guard above makes j reach longer.Length exactly as i reaches shorter.Length — provably redundant). TWO equivalents annotated: the length-order `<=`→`<` (symmetric at equal length; `>` is the covered killable sibling) and the unreachable `if (diff > 1) return false` (the caller only passes |diff| ≤ 1). ⚠️ TWO Stryker COVERAGE FALSE-SURVIVORS again — the multi-`??` chain in `Canonicalize` (a `FirstOrDefault` lambda + coalescing), where a mutant it marked Survived is killed by a real test (proven by hand-applying). Rewriting the chain to explicit `??=` statements fixed the tracing AND surfaced a genuine untested case: an exact vocab match must win over a near-dup (pinned). |
| 2026-08-26 | Speech/CookAlongCommands | 78 (46.21% → 100%) | Same shape as SpeechText — the bulk was untested COMMAND-PHRASE strings (Next/Back/Repeat/Hold/Resume/Stop/StartOver/FirstStep), each of which IS a hands-free control, so an exhaustive one-InlineData-per-entry sweep plus the number-word 0–20 and step-digit paths. ⚠️ Three entries were **dead once normalized** and got removed (behaviour-preserving): `Utterance.Core` strips trailing/leading filler, so `"and then"`→`"then"` (still Next, harmless), `"back now"`→`"back"` (→Back) and `"carry on now"`→`"carry on"` (→Next) — no input can reach those strings, so no test can kill their mutants, and they share lines with live entries (can't annotate one without over-suppressing). Two defensive-dead `≥ 0` guards in `StepNumberIn` were simplified away (`\w+` can't capture a sign), removing their equivalent Conditional/Equality mutants; the surviving word-lookup guard is pinned by "step back" (word not found → falls through) and "step zero" (index 0). ONE equivalent annotation: the `Compiled \| CultureInvariant` bitwise on the step regex — `& → None` is inert (no IgnoreCase, so CultureInvariant does nothing; Compiled is speed only). The 7 timeouts are killed mutants (a regex mutation that backtracks catastrophically). |
| 2026-08-26 | Speech/SpeechText | 140 (48.54% → 100%) | The bulk was untested DICTIONARY string-values — these strings ARE what the TTS reader says, so the honest close is an exhaustive sweep over each table: every `Units` entry (singular at "1", plural at "3"), every `Fractions` entry (alone + before-a-unit), every `MixedFractions`, every `UnicodeFractions` glyph, and every `SmallNumbers` word (via a mixed number 0–20, plus 21 to pin the `n < Length` boundary — one InlineData table per dictionary). Plus tests for the bare mixed number (`MixedBare`), and a unicode fraction glued to a whole number ("1½" must split to "1 1/2", pinning the decoded-fraction space prefix). ONE equivalent annotation: the `OrderByDescending` on `UnitPattern` — **proven** unobservable by the whole unit sweep passing identically under both orderings (the `\b` after every unit group makes a shorter prefix-unit fail and backtrack, so order can't change the match). ⚠️ `IntegerWord`'s compound `&&`-chain (with `out var n`, reached through a compiled-regex `MatchEvaluator`) was a Stryker **coverage false-survivor** — a `&&`→`\|\|` mutant it marked Survived that a real n=21 test kills (throws `SmallNumbers[21]`), proven by hand-applying it; the two identical mutants split Killed/Survived, the tell. Rewrote `IntegerWord` to explicit `if`s (behaviour-identical, clearer, defensive checks kept) and Stryker then killed it. |
| 2026-08-26 | Reporting/ReportEngine | 12 (65.62% → 100%) | First fixed the WIP compile error (`Category.Bakery`, which doesn't exist → `Pantry`) and a WIP test that asserted an **invalid** spec — quantity-by-category, which the rules refuse — so it could never have passed; repurposed it to pin that refusal. 3 targeted tests: the two date-window `&&`→`||` mutants (an out-of-window fact forms a ghost *series*, since the bucketed Total can't see it — an out-of-window date maps to no bucket — which is exactly why the existing edge test, which only asserted Total, missed them), and the multi-problem message join separator. 8 annotations: two `g.First()` on guaranteed-non-empty GroupBy groups; an equivalent `continue` (its trailing `foreach` iterates zero times); the `groups.Count <= 1` boundary (a direct return equals the ranking path for one group, with `>= 1` the covered killable sibling); the quarter `- 1` (Label only ever gets a quarter-start month); and **three UNREACHABLE** branches — the `"categories"`/`"recipes"` disclosure nouns (both splits always pool, never disclose) and the defensive `default` throw in `PurchaseValue` (only purchase metrics reach it). 3 behaviour-preserving refactors isolate the annotations cleanly: extract `ByProductGroups`/`Quarter`, and drop the redundant `spec.Split == None ||` (a None split always yields exactly one group, so `groups.Count <= 1` subsumes it). ⚠️ A `disable once` placed **mid-fluent-chain does NOT attach** — Stryker binds the directive to the enclosing statement, so the `ByProduct` `First()` had to move into a single-statement helper before its annotation took. |

## How to run it

Stryker.NET is a local dotnet tool, pinned in `.config/dotnet-tools.json` (so CI and any checkout use
the same version). Restore once, then run from the Core test project:

```bash
dotnet tool restore
cd tests/ShelfAware.Tests
dotnet stryker
```

Config lives in `tests/ShelfAware.Tests/stryker-config.json` (mutate `ShelfAware.Core`, break at a
score below 100). The HTML report is written under `StrykerOutput/` (gitignored) — open
`reports/mutation-report.html` to see every surviving mutant with its exact line and mutation.

**Diff-scoped run** (only the Core code a branch changed — fast, for local checks before a push):

```bash
cd tests/ShelfAware.Tests
dotnet stryker --since:master
```

## Gate posture

Reconciling "target 100%" with "don't wall off feature work":

- **Weekly full Core run** in GitHub Actions (`.github/workflows/mutation.yml`), break threshold =
  100. The build goes **red on any drop** below 100% — that is the no-regression guard: if a change
  ever lets a previously-killed mutant survive, it surfaces within a week and gets fixed (a new test
  or a justified annotation).
- **Pre-push diff run** (`--since:master`, changed Core files only) **reports** new survivors so they
  are visible at push time, but does not hard-block a merge. For a basically-done project where Core
  changes are rare, weekly-enforced-100% is the robust line; the pre-push report is the early warning.

The honest imprecision, named: the hard "block on regression" is the weekly red build, not a
push-time wall, because true push-time regression-diffing against a stored per-mutant baseline is
fragile. If Core churn ever picks up, the pre-push step can be promoted to a hard block.

## Score history

Each row is a full Core run (`dotnet stryker`, mutation-level Standard). "Survived" should be 0 at
100%; any survivor is either turned into a test or annotated equivalent (with the reason) before the
row is called done.

| Date | Score | Killed | Survived | Compile-error (excluded) | Notes |
|------|-------|--------|----------|--------------------------|-------|
| 2026-08-25 | 64.72% | 1214 | 611 | 453 | First baseline (+8 timeout, ~55 no-coverage). ~666 to close to 100%. Untuned scope — includes interfaces/DTOs. |

## Resume state (2026-08-26 handoff)

Where the sweep stands, for the next session picking this up on `feature/mutation-testing`:

- **Closed to 100% AND adversarially reviewed (committed):** `Chat/ProductMatcher`,
  `Recipes/IngredientMatcher`, `Prediction/ReplenishmentPredictor` — see the "Files closed" table above.
  All three went through the adversarial review; the first two refuted one false equivalence claim, and
  the predictor's pass (2026-08-26) found no false equivalence but fixed one over-broad Linq scope and one
  mis-cited test (see "The adversarial review of ReplenishmentPredictor's 7 annotations" above). The rules
  those reviews set are binding on everything below.
- ✅ **DONE — the OWED adversarial attack on `ReplenishmentPredictor`'s 7 equivalence annotations**
  (was the single most important open item; the prior agent died on the session limit). All 7 equivalence
  claims survived (0/7 false); `DominantSize`'s over-broad `disable once Linq` was restructured so its
  killable chain mutants are Killed (176 → 208), and one mis-cited test was corrected. 100% re-confirmed.
- ✅ **DONE — `Reporting/ReportEngine` closed to 100%** (65.62% → 100%; the WIP `ReportEngineTests.cs`
  compiled + verified). Fixed the compile error and a WIP test that asserted an invalid spec; 3 tests +
  8 annotations + 3 behaviour-preserving refactors. See the "Files closed" row above.
- **Next, by the worklist (worst first):** `Speech/SpeechText` (142, the biggest single file), then
  `Speech/CookAlongCommands` (86), `Tagging/TagVocabulary` (42), `Recipes/RecipeTagVocabulary` (28),
  `Onboarding/TourScript` (26), `Evaluation/ExtractionScorer` (25), and down the table. Also still owed:
  `Core/Ingest/QuantityAnomaly` (new Core from master's PR #32, now on the branch after the rebase) needs
  its own mutation coverage.
- ✅ **DONE — rebased onto current master** (2026-08-26; was ~10 behind, forked at `91d48f7`). The branch
  now carries master's newer Core, including `Core/Ingest/QuantityAnomaly` (listed above as still owed).
- **Per the gate posture above**, this branch also still needs its `/pre-push` gate before merge (it
  is test-only + a Stryker config + a CI workflow, so the security surface is minimal, but the rule
  stands).
