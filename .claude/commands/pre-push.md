---
description: Run the required code review + security review + Core mutation-coverage gate before any push or merge to master.
---

# Pre-push gate

**This repo does not push or merge to `master` without both reviews passing.** Not because a rule
says so — because this app holds real households' receipts, and a self-review after the fact has
already been shown here to be worth less than one before (the voice-engine arc's pre-merge review
found five real bugs, one of them an open microphone).

Run both, in this order, and report honestly. A finding you talk yourself out of is the one that ships.

## 1. Confirm what is actually about to move

```
git fetch origin master
BASE=origin/master          # a PR stacked on a frozen, unmerged head: that head instead
git status --porcelain
git log --oneline $BASE..HEAD
git diff --stat $BASE..HEAD
```

⚠️ **`$BASE` is the one base every step below diffs against** — §2's review scope and §3's mutation
scope included. Set it once here; never let a later step name its own.

- **`origin/master`, not `master`.** The local ref only moves when someone checks `master` out and
  pulls, which no session here does — this gate's own review once scoped itself against a local
  `master` 38 commits behind and reported 324 KB of someone else's already-merged work as part of the
  branch.
- **On a stacked PR, the parent's frozen head.** Against `origin/master` the gate would read the
  parent's commits as this branch's, and certify a diff that silently changes the moment the parent
  merges.

State the branch, the commit count, and the diffstat back to the user before reviewing. If the
working tree is dirty, stop and say so — an unreviewed change is about to ride along.

## 2. Run the reviews

Invoke the `/code-review` skill, then the `/security-review` skill, over the full branch diff
against `$BASE` from §1 (not just the last commit, and not the local `master` ref).

For this repo, security review means the multi-tenancy boundary above all else:

- Does anything reach a pantry `DbContext` without going through `IHouseholdDbFactory`?
  (The raw `IDbContextFactory` is bootstrap-only.)
- Any new `IgnoreQueryFilters`? The only sanctioned one enumerates *which* households exist for the
  startup receipt scan — it must never reach their data.
- Any new write path that could carry another household's id? `ShelfAwareDbContext.EnforceHousehold`
  refuses those now; a new path must not be built to work around it.
- Any new `AppSettings` key: is it classified in `SettingKeys` as `Config` or `UserContent`? Content
  must be exported and deleted with the rest of the household's data.
- Anything new written to disk per household: can "delete my data" reach it? (See `ReceiptStorage`
  and `CachingTextToSpeech` — a file you can't attribute is a file you can't delete.)
- Any new endpoint: does it scope to the CALLER's household claim, or take an id from the request?

## 3. Mutation coverage on Core changes

`ShelfAware.Core` is held at a 100% mutation score (`tests/ShelfAware.Tests/stryker-config.json`, break
threshold 100). If the branch diff touches `src/ShelfAware.Core/**`, run that same gate over **only what
this branch changed** — diff-scoped, so it is seconds-to-minutes rather than the ~13-minute full run:

```
cd tests/ShelfAware.Tests
dotnet stryker --since:$BASE
```

**If no `src/ShelfAware.Core/**` files changed, skip this — there is nothing to mutate.**

⚠️ **`$BASE` here too.** `--since:master` against a stale local ref scopes the run over Core changes
that merged weeks ago; the break threshold is 100, so it then fails on code this branch never touched.
CI gets this right already — `.github/workflows/mutation-pr.yml` passes the PR's base sha.

⚠️ **If the session running this gate has no .NET SDK, it cannot run this step** — the cloud session
does not. Say so in the report rather than skipping it silently, and treat `mutation-pr.yml`'s
diff-scoped check on the PR as the run that counts: no `ready, head <sha>` until that check is green on
the sha being named.

Anything under 100% is a survivor: a mutant this branch introduced or newly exposed that no test kills.
Treat each like a review finding — it is either a real coverage gap (**add the test**) or a true
equivalent mutant (**annotate it in-code with a reason**, the `// Stryker disable once ...` pattern in
`docs/mutation-testing.md`). Never lower the break threshold to pass.

This is diff-scoped by design. The weekly full-Core run (`.github/workflows/mutation.yml`) stays the
backstop for the one thing it cannot see — a Core edit that makes a *different, unchanged* file's
previously-killed mutant survive. CI also runs this same diff-scoped check on every PR to `master`
(`.github/workflows/mutation-pr.yml`), so it is enforced whether or not this gate was run by hand.

## 4. Verify, don't assume

Before reporting a finding as real, try to disprove it — and before reporting the code as clean,
name what you actually checked. Where a claim is testable, test it rather than reasoning about it:
this repo has real-SQLite tests (`tests/ShelfAware.Web.Tests`) and two past "obvious bugs"
(EF `FindAsync` skipping query filters; `AddDbContextFactory` registering a singleton context) were
**false** and only settled by a probe.

Green tests are not verification. The no-household 500 shipped past a fully green suite and was only
found by running the app.

## 5. Report, then stop

Give the user the findings — file, line, and a concrete scenario — ranked, with the ones you couldn't
construct a scenario for ranked lowest and labelled as such.

**Name the head commit the gate covered.** A gate covers the branch diff *as it stood at one commit* —
§2 is what it reads, this is what it certifies — so report it as `ready, head <sha>`. Any push after
that retracts the report: re-run the gate and name the new head. New work goes in its own PR, off the
frozen head while this one is unmerged and off `master` once it has merged. See the branch-ownership
directive in CLAUDE.md for why — on 2026-09-23 two PRs were merged at one head while the next round of
reviewed commits was still being pushed to them, which stranded that work on already-merged branches and
gave `master` a version no gate had covered.

**Do not merge.** Merging is Jordan's call, always — report, then stop.

⚠️ This line used to read "do not push or merge", which contradicted CLAUDE.md's own gate rule two
files away: **pushing the topic branch is fine and encouraged**, and is in fact how the gated head
reaches `origin` for him to merge at all. A direct push to `master` is a merge by another name, and is
what "do not merge" covers.
