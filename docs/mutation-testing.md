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
