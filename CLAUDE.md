# CLAUDE.md — the rules that bind

Working notes for Claude Code sessions on this repo. The authoritative spec is [DESIGN.md](DESIGN.md) —
read §0 (rules) and §10 (phases) before doing anything.

**This file is short on purpose.** It held 4,261 lines until 2026-09-19 and was read in full on every
turn, which made it both expensive and — because nobody re-reads 410 KB to correct it — three weeks out
of date. What binds every session stayed here. What you read *when working in an area* moved to `docs/`;
the index is at the bottom. The pre-split file is preserved verbatim at
`docs/journal/CLAUDE-2026-09-19.md`.

**Audience & quality bar:** a portfolio piece with real users (Jordan + his wife) and professional
viewers (current + prospective employers). Demonstrate production-ready work — robustness, clean atomic
git history, tests, accessibility, and visual polish are in-scope and expected, not gold-plating. Don't
dismiss polish as overkill "because it's single-user."

## Design directives

- **Co-creation — stop and discuss before diverging.** Jordan and Claude are
  co-creators. Always stop and talk it through if you disagree about a direction,
  or see a better/riskier/materially harder path than what was asked. Don't silently
  build what you think is best, and don't silently implement something you believe is
  wrong — surface the trade-off, reason it out together, decide jointly, then code.

- **Never MERGE to `master` without a code review AND a security review.** The gate is a *pre-merge*
  gate: it protects what lands on `master`, not every push. **Pushing a topic branch to origin is
  fine and encouraged** — as a backup, to open a PR, or to run CI — and needs no review; a
  feature-branch push is not code landing on `master`. Run the gate when the branch is about to
  become part of `master` (a merge, or a direct push to `master`). Run **`/pre-push`**
  (`.claude/commands/pre-push.md` — the name is historical; it is the pre-merge gate), which drives
  `/code-review` + `/security-review` over the whole branch diff and spells out what "security" means
  in this repo (the tenancy boundary, new settings keys, anything written to disk per household, new
  endpoints). This is a hard gate, not a suggestion, and it applies to a one-line fix as much as an
  arc. **Reviewing after the merge is worth much less than before it** — the voice-engine arc's
  pre-merge review found five real bugs including an open microphone, and the 7/15 no-household 500
  shipped past a fully green test suite and was only caught by running the app. Green tests are not a
  review. Report the findings and then **stop: merging is Jordan's call, always.**

- **One session owns a branch; a PR called ready is frozen.** Two Claude sessions work this repo at
  once — a cloud session, and a Remote Control session on Jordan's PC, which is the only machine here
  with the .NET SDK. On 2026-09-23 they collided four times in one afternoon: the same helper edited on
  two branches, the same follow-up picked up twice within two minutes of each other, and two PRs
  merged while their last gated commits were still being pushed. Nothing half-finished reached
  `master`, but two branches were closed as duplicates and one set of commits was stranded on an
  already-merged branch and had to be carried into a fresh PR. The rules that stop a repeat:
  - **A "ready" or "gated" report names the exact head sha, and any push after it retracts that
    report.** The freeze is on the *claim*, not on the branch: merging `master` in, or fixing CI on the
    gated code, is expected and fine — it just means the gate is re-run and a new `ready, head <sha>`
    is reported for the new head. What must never happen is a head moving while an old "ready" still
    stands, because a review covered different code than the commit that merges. New *work* — the next
    ask, or a finding that isn't this PR's to fix — goes in its own PR: off `master` when this PR has
    merged, off the frozen head when it hasn't, since `master` does not yet hold the code it builds on.
  - **Before merging, compare the PR's current head to the sha in its ready report.** GitHub's button
    always takes whatever the branch points at now, so this comparison is the only thing standing
    between a gated review and an ungated merge. Heads differ → wait for the new report.
  - **Re-read a PR's state immediately before every push, not at the start of the work.** Never push to
    a merged PR: those commits are stranded, and the follow-up has to restart from `master` as its own
    branch.
  - **Never merge a PR a sibling session has been pushing to** until it has reported `ready, head
    <sha>`. Merging is Jordan's call either way, and "merge away" authorizes the PRs as they stood when
    he said it, not whatever lands on them next.
  - **Don't work a file in parallel with a sibling session.** Anything needing a real build, a test run
    or a mutation sweep belongs on the PC — it is the only machine here that can run them; the rest is
    done in the cloud session. One at a time, not both at once.
  - **The thread that started a device session owns coordinating it.** The device session cannot see
    the thread, the other sessions, or what anyone else is doing; its only line out is reporting back.
    So splitting the work is the thread's job, not Jordan's — he sees one Claude, and asking him which
    of us should take something is asking him to do the coordinating he can't see well enough to do.

  ⚠️ This one cannot be held by a test, which is the usual and better answer here: nothing in the
  build can see another session. What it can be held by is the sha — a "ready" without one is the
  failure itself, because it is exactly what let a moving head look mergeable.

- **One prediction, one story — never let a screen state something the engine didn't do.** Anything a
  surface says *about* a prediction must come from the same `PredictionResult` that produced the due
  date it sits beside. Don't re-derive "is it due" from a median, don't render a factor you computed
  before the engine clamped it, don't ask `Predict` with different flags than the page next door.
  **This is a rule because it has been broken four times in one branch** (v4.0's backlog work): a
  report re-deriving a due date from the rebuy median called an item overdue while its own product
  page called it Stocked; `StockUpFactor` reported a raw ratio beside a bounded projection, so Product
  Detail claimed a 500× stretch the engine never made; the backlog check ran expiration-blind while
  the dashboard didn't; and a row said "1 days over". Every one shipped past a fully green suite, and
  every one was found by a human noticing two screens disagreeing. **Green tests cannot catch this
  class** — only asking "where did this number come from, and did the due date beside it come from
  the same place?" can.

- **If a fact is shown or used in more than one place, it gets ONE accessible definition — and
  converting the sites one at a time is worse than not starting.** The general form of the rule above.
  Any value a surface displays, or a guard acts on, that more than one place needs: give it a single
  property, method, or shared helper that everyone asks. Never let two call sites answer the same
  question with their own arithmetic, their own string comparison, or their own copy of a predicate.
  **This is the single most expensive failure in this repo's history**, and it is always the same
  shape: two places agree today, one is edited later, and the disagreement ships silently because
  every test still passes. Cases on file — the ✓ mark and the "Ate it" decrement using two different
  matchers (item 25); a suppressed row's phrasing reinvented per page (item 20's `CountNote`); seven
  readings of when a signal happened, one of them different (item 19's `SignalDate.Of`); a page
  re-deriving "exact vs fuzzy" from raw strings the matcher had already normalized (item 39).
  ⚠️ **And the sharpest lesson, from the census branch's own review cascade (item 41): "which product
  does this name mean?" was answered in NINE places, and fixing them one per round produced three
  consecutive rounds of new data-harm defects** — each round left a half-converted state where one
  guard promised something its neighbour then contradicted (a grid offering "leave this to create a
  separate item" over a write that replaced an existing product's count). Rounds ran 5 → 3 → 8
  findings; the count only fell when the *rule* moved into one place (`ProductMatcher.IdentityKey`)
  instead of the sites moving one at a time. So: when you find two sites disagreeing, the fix is the
  shared definition and **every** caller in the same change — a partial conversion is a new bug with a
  green suite over it.

- **Craftsmanship — take pride in every change; no shortcuts.** Always do the polished,
  professional thing, not the quickest thing that happens to pass. Concretely: **no empty
  or catch-all `catch` blocks that swallow errors** — catch specific exceptions, log via
  `ILogger`, and let cancellation (`OperationCanceledException`) propagate; don't duplicate
  logic that should live in one shared place; don't ship behavior without tests; leave no
  dead code, orphaned state, or TODO-shaped gaps. If you spot a corner being cut — yours or
  the existing code's — fix it or flag it, never leave it. Assume every line will be read by
  a prospective employer, because it will.

## Build state

| | |
|---|---|
| **Tests** | Four suites, 0 failing, 0 warnings on a non-incremental Release build. ⚠️ The count lives in `src/ShelfAware.Web/wwwroot/test-status.json`, written by the CI run that measured it and rendered on `/admin` — **don't type it here**: the copy that used to sit on this line was stale by 46 tests the week it was written, and the `/admin` card it mirrored was stale by 617. A number a person maintains is a number that will be wrong. |
| **Mutation gate** | Core at 100% — full sweep weekly, diff-scoped per PR |
| **Live** | Demo box on a DigitalOcean droplet since 2026-08-11 (BYOK); family box on Jordan's PC behind Cloudflare Access since 2026-08-12 |
| **Phases 1–4** (DESIGN.md §10) | ✅ Done, acceptance verified |
| **Phase 5** — cloud deploy + README | ✅ Deployed end to end; see `docs/backlog.md` for the last polish items |
| **In flight** | The remediation arc from the 2026-09-18 audit — seven phases, designed in `docs/remediation-plan.md` |

⚠️ **Read the test count off the final run before writing it down.** This number has been stale or
self-contradictory five separate times, each written from an intermediate run or from memory. The same
applies to any count in any doc — see `docs/remediation-plan.md` §6 on artifacts that claim to be true.

The app is far beyond the spec's three pages. Current surfaces: Dashboard (`/`), Upload (`/receipt`),
Products, Grocery List, Trends, Product Detail, Accuracy, Recipes, Receipts, Count from a photo
(`/pantry-photo`), Cookbook, History, Reports, Meal plan, Bugs, Admin, and the public About page —
the app's only page outside the auth wall.

## Where to look

| Working on… | Read |
|---|---|
| An entity, a write path, tenancy, the schema seam | `docs/architecture.md` |
| Your first build; a shell, path or deploy oddity | `docs/environment.md` |
| Anything that speaks or listens | `docs/features/voice.md`; choosing a voice, `docs/voice-bakeoff.md` |
| Recipes, tags, substitutes, the cookbook | `docs/features/recipes.md` |
| Billing, credits, tiers, the payment seam | `docs/subscription-plan.md` |
| The audit findings and the plan to close them | `docs/remediation-plan.md` |
| What's still open and what's parked | `docs/backlog.md` |
| Why some past decision was made that way | `docs/journal/build-log.md` |
| Mutation testing | `docs/mutation-testing.md` |
| Deploying | `docs/deploy-droplet.md`, `docs/family-cloudflare.md`, `docs/deploy-kokoro.md` + `docs/deploy-piper.md` (mouth), `docs/deploy-moonshine.md` (ear) |
| The GraphQL API | `docs/graphql-api.md` |

⚠️ **A code comment saying "CLAUDE.md item 38" means item 38 of `docs/journal/build-log.md`.** Twenty-four comments across the source carry that form; they were written when the history lived here. They were deliberately left alone rather than rewritten in bulk — the item numbers are what matters and they are unchanged.

`docs/journal/build-log.md` is the 68-arc history — what shipped, what each review gate found, and the
constraints the code can't state for itself. It is **reference**: open it for the area you are touching,
not before every task. Its ⚠️ marks are the expensive ones, and several were broken by sessions that had
just read them — which is the argument for holding a rule in a test rather than a paragraph.
