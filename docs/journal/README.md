# The journal

Where this project's history lives, so that `CLAUDE.md` doesn't have to carry it.

| File | What it is |
|---|---|
| `build-log.md` | 68 numbered feature arcs, 2026-06 → 2026-09. The working record: what shipped, what each `/pre-push` gate found, and the constraint behind each design call. |
| `CLAUDE-2026-09-19.md` | The pre-split `CLAUDE.md`, verbatim, 4,261 lines. Kept so nothing is lost to the reorganisation — if a section seems to have vanished, it is in here. |

## How to search it

The arcs are numbered and mostly chronological, so `grep -n "item 41" build-log.md` resolves a
cross-reference, and `grep -n "⚠️" build-log.md` lists the constraints that were expensive enough to
mark. The most common reason to open it is a cross-reference from a code comment ("item 28's rule",
"the census cascade") — those are always item numbers in this file.

## Why it reads the way it does

It is written as incident reports rather than changelog entries, and that is deliberate: nearly every
entry exists because something shipped past a fully green test suite. The repeated shape — two places
answering the same question with their own arithmetic, agreeing today, disagreeing after one of them
is edited — is the reason for the "one accessible definition" directive at the top of `CLAUDE.md`.

## The honest caveat

⚠️ Several ⚠️ rules in here were broken *after* being written down, sometimes by the session that had
just read them. A paragraph cannot fail a build. Where one of these could be held by a test or an
analyzer instead, it should be; `docs/remediation-plan.md` §1 makes that the standing rule for new
constraints.
