---
description: Run the required code review + security review gate before any push or merge to master.
---

# Pre-push gate

**This repo does not push or merge to `master` without both reviews passing.** Not because a rule
says so — because this app holds real households' receipts, and a self-review after the fact has
already been shown here to be worth less than one before (the voice-engine arc's pre-merge review
found five real bugs, one of them an open microphone).

Run both, in this order, and report honestly. A finding you talk yourself out of is the one that ships.

## 1. Confirm what is actually about to move

```
git status --porcelain
git log --oneline master..HEAD
git diff --stat master..HEAD
```

State the branch, the commit count, and the diffstat back to the user before reviewing. If the
working tree is dirty, stop and say so — an unreviewed change is about to ride along.

## 2. Run the reviews

Invoke the `/code-review` skill, then the `/security-review` skill, over the full branch diff
against `master` (not just the last commit).

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

## 3. Verify, don't assume

Before reporting a finding as real, try to disprove it — and before reporting the code as clean,
name what you actually checked. Where a claim is testable, test it rather than reasoning about it:
this repo has real-SQLite tests (`tests/ShelfAware.Web.Tests`) and two past "obvious bugs"
(EF `FindAsync` skipping query filters; `AddDbContextFactory` registering a singleton context) were
**false** and only settled by a probe.

Green tests are not verification. The no-household 500 shipped past a fully green suite and was only
found by running the app.

## 4. Report, then stop

Give the user the findings — file, line, and a concrete scenario — ranked, with the ones you couldn't
construct a scenario for ranked lowest and labelled as such.

**Do not push or merge.** Ask. Pushing is the user's call, always.
