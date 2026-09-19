# Remediation plan — the 2026-09-18 audit and the architecture retrospective

Jordan's ask (2026-09-18): *"Id like you to fix the findings AND the what youd do differently... feel free
to split it up as needed and design first."* This is the design.

The audit produced two lists — seven findings and nine "what I'd do differently" items. They overlap
heavily: five of the nine retrospective items are the *general form* of a specific finding. So the real
arc is **seven phases, not sixteen**, each its own branch and its own `/pre-push` gate.

Two items are deliberately **not** in the arc, with reasons in §8. Read that before asking where they went.

---

## 0. The map

| # | Phase | Closes | Size | Risk |
|---|---|---|---|---|
| 1 | The docs reset ✅ | F5, D8 | M | none — no code |
| 2 | Pin the toolchain, unbreak the parse ✅ | F2, D7 | S | low |
| 3 | The presentation layer ✅ | F3, D2 | M | low |
| 4 | Errors stop leaking provider text ✅ | F4 | S | low |
| 5 | The operational floor ✅ | F6, F7a, F7b, D6 | M | low |
| 6 | Make the truth-claiming artifacts self-maintaining ✅ | F1, D9 | S | low |
| 7 | The Shelf Aware credit ✅ | D5 | L | **high — money** |
| — | Logic out of `.razor` | D1 | XL | see §8 |
| — | EF Migrations | D3 | L | see §8 |
| — | One database | D4 | XL | **rejected — see §8** |

Phases 1–6 are independent and can land in any order. Phase 7 depends on nothing but deserves to go last
because it is the only one that touches money, and because it is *cheapest to do before the first paying
customer* — see §7.

**All seven landed 2026-09-19** (`129695a`, `2144629`, `48a79ac`, `d5f7a8e`, `65b4a6b`+`ce4f280`, `62ecbb5`,
`e1b9daf`). Each phase's section carries an "As built" note recording what the code does that the design
above does not say. The two remaining items — draining logic out of `.razor` (D1) and EF Migrations (D3) —
are deliberately not phases; §8 says why.

---

## 1. The docs reset (F5, D8)

**The problem.** `CLAUDE.md` is 4,261 lines / 410 KB. Every session reads all of it before doing anything.
It is also three weeks behind the code: it has no mention of Stripe, `Entitlements`, the demo valve,
`LocalTextToSpeech`/Kokoro, `PaymentWebhookHandler` or `BillingCatalog` — all of which shipped after its
last entry — and its build-state table still names `docs/accuracy.png` as the outstanding TODO, which has
existed since before the audit.

**Why it got this way, and the thing worth keeping.** The file is 68 numbered arc items, and every one of
them earned its place: they are the record of bugs that shipped past a green suite. That history is
genuinely valuable and must not be deleted. But it is *reference* material — read when you are working in
that area — not *instruction* material, and today it is loaded as instruction on every single turn.

**The split.**

```
CLAUDE.md                     the rules that bind + an index      (~200 lines)
docs/architecture.md          data model, tenancy, the seams
docs/environment.md           toolchain, shell, deploy gotchas, conventions
docs/features/voice.md        the cook-along engine
docs/features/recipes.md      recipes, tags, substitutes
docs/journal/CLAUDE-2026-09-19.md   the archive, verbatim
docs/journal/README.md        what the journal is and how to search it
```

`CLAUDE.md` keeps: the design directives (they bind every session and are short), an accurate build-state
table, and a pointer index saying *which* file to open for *which* kind of work. Everything else moves.

**The rule this establishes, which is the D8 half:** a constraint that must hold goes in a **test or an
analyzer**, not a paragraph. The ⚠️ comments in the journal are constraints that were written down and then
broken anyway — several of them more than once, by sessions that had just read them. A sentence cannot
fail a build. Where a journal ⚠️ names a rule that a test could hold, phase 1 notes it; converting them is
follow-on work, not part of this phase.

---

## 2. Pin the toolchain, unbreak the parse (F2, D7)

**The problem.** CI asks for `dotnet-version: '10.0.x'` — the latest patch, whatever that is on the day.
On SDK **10.0.112** the Razor parser reads a relational pattern at the start of a switch-expression arm as
an HTML tag start:

```csharp
return days switch
{
    < 0 => $"{-days} day{Plural(-days)} overdue",   // 273 errors across four pages
    0 => "Due today",
    _ => $"Due in {days} day{Plural(days)}"
};
```

The same commit builds clean on whatever patch CI happened to draw. This is not theoretical — it is how
the audit's build failed, at a SHA that CI reports green.

⚠️ **Stated precisely, because the loose version is wrong.** The SDK this reproduces on is
`10.0.112-0ubuntu1~24.04.1` — Canonical's packaging, from Ubuntu's own feed, which is what an
apt-installed box gets. I could not reach Microsoft's release metadata from here, so I cannot say whether
the parse difference is a patch-level upstream regression or an artifact of the distro build. What *is*
established is the only thing the fix turns on: two toolchains that both call themselves "10.0.x" disagree
about whether this repo compiles. Do not repeat this as "SDK 10.0.112 has a Razor bug" — that is a claim
the evidence does not support.

**The fix, both halves:**

1. `global.json` pinning the feature band with `rollForward: latestPatch` and `allowPrerelease: false`,
   and all three workflows reading it via `global-json-file` rather than naming a wildcard of their own.
   **What this buys:** one declared toolchain, single-sourced, so a workflow cannot drift from what
   developers build with; a clear failure on a machine that only has .NET 9; and no silent preview SDK.
   **What it does not buy:** a byte-identical patch everywhere — that needs `rollForward: disable` and an
   exact version, which makes a fresh machine unable to build until it installs that precise patch. The
   pin makes the requirement *declared*; item 2 below is what makes the build *robust*.
2. Rewrite the five relational-pattern sites so they parse under any SDK — `_ when days < 0 =>` is
   equivalent, unambiguous, and verified to compile on 10.0.112. The sites:
   `Home.razor:517`, `GroceryList.razor:730`, `Products.razor:423`, `ProductDetail.razor:1493`,
   `SpendInsight.razor:194`.

3. **A test, not a paragraph.** `RazorSourceRulesTests` scans every `.razor` under `src/` and fails,
   naming file and line, if an arm opens with a bare relational pattern. Without it the next person to
   tidy `_ when days < 0 =>` back into `< 0 =>` re-breaks the build on some machines and passes on
   others. This is §1's rule applied to its first case: a constraint that must hold goes somewhere that
   can fail.

Four of those five sites disappear anyway in phase 3 — but the pin, the rewrite and the guard land first,
because until they do, a fresh machine cannot build the repo at all.

**Result:** the full solution builds `0 Warning(s), 0 Error(s)` on a non-incremental Release build under
the SDK that previously produced 273 errors, and all four suites pass — 3186 green, the one new test being
the guard. The guard was mutation-checked: restoring a single bare pattern fails it with
`GroceryList.razor:731`, and nothing else.

---

## 3. The presentation layer (F3, D2)

**The problem.** `ChipClass` and `StatusLabel` are byte-identical in four pages — `Home.razor:494`,
`GroceryList.razor:737`, `Products.razor:430`, `ProductDetail.razor:1498` — and `Urgency` is a near-copy in
the same four, differing only in how it spells the pluralizer. This is the exact shape the top of
`CLAUDE.md` names as this repo's most expensive failure: *two call sites answering the same question with
their own arithmetic*. It has already produced "1 days over" twice and a Stocked-chip-beside-overdue-text
contradiction twice.

**The fix.** One `PredictionDisplay` (Core — it is pure, and Core is where the mutation gate is 100%) that
owns the chip class, the status label, the urgency phrase and the pluralizer. All four pages convert **in
the same change**: a partial conversion is a new bug with a green suite over it, which is the directive's
own wording.

**Why this is the D2 item and not just F3.** The general rule is that a page should not contain a function
that another page could want. The four helpers are the proof; the layer is the fix. Phase 3 does not
attempt to drain the pages of logic wholesale — see §8 on D1 — it establishes the place that logic goes,
and moves the demonstrated duplicates into it.

**As built.** `PredictionDisplay` (Core/Prediction) owns `ChipClass`, `Label`, `Urgency`, `Relative` and
`Plural`. Two phrasings survived rather than one, because the pages genuinely wanted two: the dashboard's
standalone card line ("Due in 3 days") and the grids' inline cell phrase ("in 3 days"), which sits under a
heading that supplies the subject. Forcing one on both would have been a behaviour change dressed as a
refactor. What they now share — and could previously drift on — is the lateness wording, factored into one
private `Overdue(days)`: that is the arm that actually went wrong, shipping "1 days over" twice from two
private copies.

Call sites read `@PredictionDisplay.ChipClass(...)` rather than importing the type statically. The extra
words are the point: `@ChipClass(...)` reads like a page-local helper, which is exactly the misreading that
let four copies coexist.

**Verified by what did not change.** The 601 bUnit page tests render all four of these pages and assert on
their chips and urgency text; all of them still pass untouched, which is the evidence that the conversion
is behaviour-preserving. New Core tests pin every arm, both phrasings, the out-of-enum fallback, and that
the two forms word lateness identically. Diff-scoped Stryker on the new file: **100.00%**, 24 mutants, no
survivors. Net −99 lines.

One stale comment fixed in passing: `MakeabilityFormat`'s docstring cited "the same one-definition
discipline the app applies to prediction-status chips" as precedent. That was false when written — the
prediction chips were the four-copy case — and is true as of this phase.

---

## 4. Errors stop leaking provider text (F4)

**The problem.** A failed AI call returns `ex.Message` and the page renders it:

```csharp
// AnthropicReceiptExtractor.cs:139
return ExtractionResult.Fail(ex.Message, rawJson);
// Upload.razor:520
errorMessage = result?.Error ?? "Sorry — something went wrong reading that receipt. Please try again.";
```

The friendly fallback is only reached when `Error` is null, which is the one case that does not happen.
So the user sees an `HttpRequestException` string. Five sites do this:
`AnthropicReceiptExtractor.cs:139`, `AnthropicShelfCensusReader.cs:142`, `ElevenLabsTextToSpeech.cs:112`,
`ElevenLabsSpeechToText.cs:84`, `LocalTextToSpeech.cs:108`.

**The fix.** The detail goes to `ILogger` (where it is already going, in every one of the five — the log
call is right above the `return`), and the result carries a message written for a person. The house already
does this correctly one line up in `LocalTextToSpeech`:
`TextToSpeechResult.Fail($"Text-to-speech failed ({(int)response.StatusCode}).")` for the non-2xx path, and
then `Fail(ex.Message)` for the exception path. The exception path just never got the same treatment.

This is small, but it is the one finding with a security edge: an exception message can name an internal
host, a path, or a provider account detail.

### As built (2026-09-19)

**Seven sites, not five.** The audit counted the `Fail(ex.Message, …)` shape and missed two more that
reach the same place by a different route: the extractor's and the census reader's *terminal* parse
failures interpolate `lastError` — which is `ex.Message` — into the sentence. A `grep` for
`Fail(ex.Message` cannot see those. All seven now return fixed copy:

| Site | What the person now reads |
|---|---|
| extractor / census — call failed | "Couldn't reach the AI just now — please try again." |
| extractor — unparseable after a retry | "Couldn't read that receipt — try again, or use a clearer photo." |
| census — unparseable after a retry | "Couldn't read that photo — try again, or take a clearer one." |
| ElevenLabs + local TTS | "Couldn't reach text-to-speech just now — please try again." |
| ElevenLabs STT | "Couldn't reach speech-to-text just now — please try again." |

The importer's terminal copy was already written this way; only its log level changed.

**Three terminal failures were logged at Warning.** The person watching saw the thing fail, so the
operator has to be able to see why without asking them for the wording — and only `LogError` is captured
by the error-log pipeline onto `/admin`. Raised in the extractor, the census reader and the importer.

**The guard: `tests/ShelfAware.Llm.Tests/ProviderErrorCopyTests.cs` (8 tests).** Transport failures throw
an exception whose text names an internal host and an `sk-ant-` key; the test asserts both are absent and
that a non-empty message is still returned — so the copy stays free to change while the leak cannot come
back.

⚠️ **The parse-path guards were vacuous on their first writing, and the mutation check is what caught
it.** They pinned the absence of `"System."` and `"Path: $"` — an assumption about deserializer wording.
`System.Text.Json` actually says `'n' is an invalid start of a value. LineNumber: 0 |
BytePositionInLine: 0.`, so all three passed while observing nothing. They now pin the property a leak
genuinely cannot satisfy: **the terminal message is fixed copy, not derived from the failure** — two
different bad outputs must produce one identical sentence. Reverting all seven sites kills 7 of the 8
tests; the survivor is the importer, which had nothing to revert, and its comment says so.

3216 green, 0 warnings on a non-incremental Release build.

---

## 5. The operational floor (F6, F7a, F7b, D6)

**The problem, in three parts.**

**(a) The demo box's money guard fails open with nothing behind it.** `DemoUsageMeter.IsCallBlockedAsync`
returns "not blocked" when it cannot read its own counter, and the comment justifies this by naming *the
API key's console spend limit* as the hard backstop. That backstop is real but it lives outside the repo,
outside CI, and outside anything that would notice if it were removed or raised. The valve is the only
thing between a public demo box and an unbounded bill, and its failure mode is silence.

**(b) There is no health endpoint.** Nothing external can tell whether the app is up, whether both SQLite
files are readable, or whether the AI provider is reachable, short of loading a page behind auth.

**(c) The droplet has no backup script.** The family box has a rehearsed, verified nightly kit
(`deploy/backup-family.ps1` + `sqlite-snapshot/`, with `VACUUM INTO` from a read-only connection and an
integrity check on the copy). The droplet — the box that will take paying customers — has nothing.

**The fix.**

- A startup assertion that logs, loudly and once, whether the demo valve's limits are configured, so a box
  running with the guard effectively off says so in its own log rather than in someone's invoice.
- `/healthz` — anonymous, no household, checks both DB files open and returns a small JSON body. Follows
  the `/api` convention of answering with a status code rather than an HTML redirect.
- A droplet backup script that is the same shape as the family one, because that one has already been
  through a review that caught a data-loss chain and four fail-safe defects. Reuse the reviewed design.

### As built (2026-09-19)

**(a) The fail-open is bounded in DURATION rather than removed.** Failing open is right for a blip —
an auth.db hiccup must not block a legitimate call, nor tear down a circuit through the pre-check — and
wrong without end, because the justification names a backstop this repo cannot see. So
`DemoUsageMeter.FailOpenReadLimit` (10 consecutive failed reads) closes the valve, politely, with the
same come-back-tomorrow sentence a real cap gives; the first successful read reopens it with no
intervention. Only a box that has configured a cap ever reads the counter, so family and self-host boxes
are untouched.

**(b) A misconfigured valve says so at startup.** `DemoOptions.ConfigurationObjections()` is the one
reading of "is this section coherent?", so startup and any future surface ask the same question. It
objects to exactly two states, both provable from the two numbers alone: an alert with no cap (a
heads-up with nothing behind it), and an alert at or above the cap (it can only arrive once the box is
already closed). Nothing configured objects to nothing — a rule that fires on a coherent box is one an
operator learns to scroll past, and then the real misconfiguration scrolls past with it. A configured
valve narrates its real bound at Information.

**(c) `/healthz`, anonymous, behind `HealthProbe`.** A monitor has no account, and an endpoint that
needs one cannot tell "the box is down" from "my credentials expired". It answers `{"status":"ok"}` or a
503 naming which database is unreachable — by neutral name only, never the reason; that goes to the log,
same rule as phase 4, applied to the one surface that answers without a login. It deliberately does not
touch the AI provider: an anonymous endpoint that triggers an outbound call would bill the host key for
being scraped. The answer is cached for five seconds, which is what lets it stay unmetered — rate-limiting
a health check is how a monitor learns to report an outage that isn't happening.

⚠️ `HealthProbe` is the app's **sanctioned second use of the raw `IDbContextFactory`** for the pantry
context. It issues `CanConnectAsync` and nothing else: no row read, no tenant table touched, nothing
derived from anyone's data, so there is no filter for it to be missing. Confining it to one named class
rather than inlining it in `Program.cs` is the move `AdminReportReader` makes for its
`IgnoreQueryFilters` — a sanctioned exception belongs somewhere a reviewer can find it.

**(d) `deploy/backup-droplet.sh` + `install-droplet-backup.sh`.** The family design, ported: `VACUUM
INTO` over a read-only connection, `PRAGMA integrity_check` against the copy, dated snapshots pruned by
the date in the folder NAME (a folder that doesn't parse is left alone, never guessed at), rolling
`rsync` mirrors confined to dedicated per-tree directories, `--dry-run` that rehearses everything, one
log line per run either way, and `rclone --backup-dir` for offsite so a bad local night cannot erase good
remote copies. A systemd timer runs a COPY installed outside the repo, because the 03:30 run must not
depend on which branch is checked out.

This replaced an inline snippet in the droplet runbook that was a second, divergent definition of what to
back up — and it had already drifted: it missed `recipe-images` entirely, so every recipe photo on the
box was outside the backup.

⚠️ **Two defects were found by rehearsing rather than by reading**, both in code I had just written:
the `--dry-run` flag reached `rsync` through `$(… && echo -n)`, which prints *nothing* (it is the
suppress-newline flag), so a rehearsal would have mirrored for real; and a run that failed after the
first database left a correctly-named `db-<stamp>/` holding one of two, indistinguishable from a good
snapshot until someone restored from it and found no accounts. Snapshots are now built under
`.incomplete` and renamed only once both databases verify. Rehearsed end to end: a live run against a
database being written, a restore-read of the copy, retention against planted old and malformed folders,
the wrong-`--data-dir` failure, and every argument guard.

**Why this is the D6 item.** `docs/subscription-plan.md` §9 already names this set as the *ops launch gate*,
deliberately deferred until promised customers exist. That call was right at the time. The retrospective
point is narrower: the health endpoint and the backup script are each an afternoon, and they are the kind
of thing that is cheap before you need them and expensive at the moment you do. Phase 5 does the cheap
half now; the paid half of that gate (a transactional email provider, uptime alerting) stays where it is.

---

## 6. Make the truth-claiming artifacts self-maintaining (F1, D9)

**The problem.** `/admin` has a "Tests & quality" card. It says **2568** tests. There are **3185**. The
file behind it, `wwwroot/test-status.json`, is stamped `2026-08-27`, three weeks stale, and its `Branch`
field reads `feature/admin-dashboard`.

This is not a bug in the card — the card renders its input faithfully. It is that the input is refreshed
by a human remembering to, and nobody remembered. CI already generates the file correctly
(`tools/TestStatusGen` folds the `.trx` files) and then uploads it as an **artifact**, which no running app
can read. The workflow's own comment names the fix and calls it an opt-in.

**The fix.** Take the opt-in: CI commits the regenerated `test-status.json` back to master. A
`GITHUB_TOKEN` push does not re-trigger CI, so there is no loop.

### As built (2026-09-19)

**CI commits the snapshot back, from a separate job.** `publish-test-status` runs only on a push to
master, holds the only `contents: write` token in the workflow (permissions are per-job, so the build job
that also runs for pull requests stays read-only), and is serialised by a `concurrency` group so two
pushes cannot race each other's commit. A `GITHUB_TOKEN` push does not trigger workflows, so it cannot
loop. It commits only when the numbers changed — `jq` drops `GeneratedAt` from both sides — or master
collects a no-op commit on every push.

⚠️ **It publishes a failing suite and refuses an empty one.** The job runs under `always()`, because
"3 failing" is exactly the news the card exists to carry and a card that only ever publishes green can
never deliver bad news. But a run whose BUILD failed has no `.trx` at all, the generator honestly reports
zero projects, and committing that would make the card claim the suite is empty. One line —
refuse a snapshot with no test projects — is what tells "tests failed" apart from "nothing ran".

**The build's warning count now comes from the build.** `BUILD_WARNINGS` was read by the generator and
set by nobody, so a regenerated file would have silently dropped the warnings tile. CI captures MSBuild's
own summary count — not a grep for the word "warning", which matches warning *text* and would over-report
— and a pattern that matches nothing leaves the tile off rather than claiming zero.

**The rule was then applied to this repo's other hand-typed count.** `CLAUDE.md`'s build-state table
carried `3185 green … (Core 1378 · AI 189 · Persistence 1018 · Pages 600)`. It was stale by 46 within
three weeks of being written — the same failure as the card, one file over. The line now names where the
real number lives and says not to type one there.

⚠️ **Three defects, all found by rehearsing rather than reading**, and all of the same kind — a comment
describing behaviour the code did not have:
- The job's comment said `always()`; the condition did not have it, so a failing build would have skipped
  the publish entirely.
- `warnings=$(grep … | tail -1)` aborts under `set -e` when grep matches nothing, so an unrecognised
  MSBuild summary would have failed the **build** over a number the card merely displays.
- The push retry's comment promised a warning annotation rather than a red master; `git pull --rebase`
  is a bare command under `set -e`, so the retry could never reach its `||` and the job went red.

The commit step was extracted from the YAML and run against a scratch repository for all four cases:
empty snapshot, timestamp-only change, real change, and an unpushable remote.

**`docs/demo.gif` is NOT fixed and is listed as open.** It is the same class — a truth-claiming artifact
that shows an app from before v3.5 — but the fix is a capture session, not a workflow, and the storyboard
that would guide one was deleted when the current gif landed. It stays in `docs/backlog.md`.

**Why this is the D9 item, and the rule it sets.** Any artifact that makes a factual claim about the
codebase must be produced by the thing it claims about. A number typed into a file by a person is a number
that will be wrong; the only question is when. `eval-results.json` already works this way and is right.
`test-status.json` does not and was wrong by 617 tests. The same test applies to the README's
`docs/demo.gif`, which is real but predates every feature since v3.5 — a picture of an app that no longer
exists is a truth-claiming artifact too.

---

## 7. The Shelf Aware credit (D5)

> Jordan, 2026-09-19: *"The 'Unit of cost' is supposed to be a me-specific credit that represents a
> different amount of backing tokens depending on the service being used."*

This is the retrospective's D5 — *decide the money model before you build the metering* — and it is the one
place where the built system does not match the intent.

### 7.1 What is built today

A credit **is a retail dollar**. `CreditLedgerEntry.AmountMicros` is "signed RETAIL micros"; consumption is
`tokens × $/MTok × 1.65`; the balance is a sum of dollars. `BillingCatalog` prices the $5 pack at
`5_000_000` micros — five dollars, exactly.

This is a *pass-through* model: the customer's balance is denominated in Jordan's provider bill. It has two
structural consequences.

1. **Non-token services do not fit.** `docs/subscription-plan.md` §4 already has to carve out "two pricing
   shapes, one currency": token actions stamp exact cost, voice actions post *flat retail prices* because
   per-character and per-minute costs "are not observable per-call from inside the app". That carve-out is
   the model straining. Kokoro TTS makes it worse — it costs **nothing**, so under a cost-denominated model
   it is free, and "free on the box with the sidecar, priced on the box without" is a pricing rule derived
   from deployment topology.
2. **Margin per service is invisible by construction.** Credits and cost are the same number, so there is
   no arithmetic that could tell you chat turns are underpriced. You would have to leave the model to find
   out.

### 7.2 The design

**A credit is an abstract unit Shelf Aware issues. A price list says what each action costs in credits. What
a credit costs *Jordan* varies by service, and that variance is the point.**

Four pieces:

**(a) `ServiceAction`** — the enum of things a household can be charged for, at the granularity a *person*
would recognise: `ReceiptExtraction`, `CensusPhoto`, `ChatTurn`, `RecipeSuggest`, `RecipeAdapt`,
`RecipeImport`, `TagSuggest`, `SubstituteSuggest`, `TtsSynthesis`, `RealtimeMinute`. Not "an LLM call" —
a user does not buy LLM calls.

**(b) `CreditPrices`** — action → credits, config-bound like `BillingOptions` so it is an operator
variable, not a constant. Proposed opening values, derived from §3's measured cost ranges:

| Action | Backing cost | Price |
|---|---|---|
| Receipt page extraction | ~$0.005–0.015 | **1 credit** |
| Census photo | ~$0.01 | **1 credit** |
| Chat / voice turn (all tool rounds) | ~$0.01–0.04 | **2 credits** |
| Recipe suggest / adapt / import | ~$0.01–0.03 | **2 credits** |
| Tag / substitute suggest | <$0.002 | **free** |
| TTS synthesis (one recipe read) | measure first | **3 credits** (placeholder) |
| TTS cache hit | $0 | **free** |
| Realtime voice, per minute | ~$0.10–0.16 | **12 credits** (placeholder) |

Note what a chat turn and a realtime minute have in common: nothing. One is ~7,000 tokens, the other is
zero tokens and sixty seconds of somebody else's per-minute billing. The credit is the only unit that can
price both, which is the whole argument.

**(c) The anchor** — one number, set once, converting credits to money for grants and packs:

> **1 credit = $0.01 of cost = $0.0165 retail** at the existing 1.65× markup.

Everything else falls out and stays consistent with the numbers already in `docs/subscription-plan.md` §3:

| | Today | Under the credit |
|---|---|---|
| Welcome grant | $1.00 cost | **100 credits** |
| Aware monthly allowance | $1.00 cost | **100 credits/month** |
| $5 pack | 5,000,000 micros | **~300 credits** |
| $10 pack | 10,000,000 micros | **~600 credits** |
| $20 pack | 20,000,000 micros | **~1,200 credits** |

100 credits/month is ~100 receipts or ~50 chat turns — which lands exactly on §3's "$1.00 of cost ≈ 50–150
AI actions/month". The anchor is a re-denomination of the existing economics, not a price change.

**(d) Reconciliation.** `AiUsage.CostMicros` keeps doing exactly what it does now — stamping Jordan's real
cost per call, at the rate current when the call was made. The ledger moves to credits. The admin dashboard
gains the number that does not exist today: **credits consumed per service against dollars actually spent
on that service**, so a mis-priced action is something you *read*, not something you discover on an invoice.
This is the concrete thing decoupling buys.

### 7.3 Design rules that fall out

- **Credits are integers.** Nobody should ever see 1.3 credits.
- **If an action is too cheap to charge a whole credit for, it is free.** That is how the fractional problem
  goes away, and it makes the ✨ buttons free, which they should be anyway.
- **A user-visible action is one price, whatever it costs underneath.** A chat turn that needs five tool
  rounds still costs 2 credits. The variance is Jordan's; in exchange the UI can state the price *before*
  the button is pressed, which the dollar model can never do.
- **The price list is public.** Shown in Settings. An abstract unit with a hidden exchange rate is a
  casino chip; an abstract unit with a published price list is a product.

### 7.4 Migration, and why now

The ledger's stored unit changes from retail micros to credits. Existing rows convert once at the anchor.

**This is the cheapest it will ever be.** Payments are not live, no real money has moved, and every
household on the family box is a Founder with an unmetered tier. After the first paying customer, this same
change means converting someone's purchased balance — a thing you can only get wrong once.

### 7.5 Decided, and still open

**DECIDED (Jordan, 2026-09-19): the anchor is one cent of cost per credit.**

> **1 credit = $0.01 of Jordan's cost = $0.0165 retail** at the existing 1.65× markup.

Concretely, and these are the numbers phase 7 implements:

| | Credits |
|---|---|
| Welcome grant (one-time, per household) | **100** |
| Aware monthly allowance (no rollover) | **100 / month** |
| $5 credit pack | **303** |
| $10 credit pack | **606** |
| $20 credit pack | **1,212** |

Pack sizes are `floor(pack dollars ÷ $0.0165)` — deliberately computed from the anchor rather than rounded
to a marketing number, so there is exactly one exchange rate in the system and no pack quietly carries a
better one. If round numbers are wanted later (300 / 600 / 1,200), that is a *discount* decision and should
be recorded as one, not smuggled in as rounding.

**Still open:**
1. **The two placeholders.** TTS-per-read and realtime-per-minute cannot be priced honestly until EL
   invoices are measured against real usage — §9's standing open item. They ship as config with a comment
   saying they are estimates, and get corrected from evidence.
2. **Naming.** "Credits" is the safe default. If they get a Shelf Aware name, the abstraction is more
   obviously deliberate — but it also has to survive being said out loud.

### As built (2026-09-19)

Built as designed, at the decided anchor. What the code does that the plan above does not say:

**`CreditPricing` (Core) is the unit's one definition** — the anchor, the price list, and every conversion
between credits and money. `ServiceAction` gained two values the plan's list missed (`MealPlan`,
`IngredientAlternatives`), both real AI surfaces that would otherwise have been priced by accident.

⚠️ **The markup is expressed once, in what a DOLLAR buys, and cancels out of every charge.** A credit is
defined as a fixed amount of *cost*, so consumption has no markup term — raising `CreditMarkup` sells fewer
credits per dollar and leaves every action's price alone. Applying it in both places would double it,
invisibly. Pinned by `The_markup_is_expressed_once_in_what_a_dollar_buys`.

⚠️ **Dollars convert to credits two different ways, deliberately.** A *pack* is bought, so its dollars are
RETAIL (`PackCredits`); a *grant* is given, so its dollars are what the house is willing to SPEND
(`WelcomeGrantCredits`/`MonthlyAllowanceCredits`). $1 of cost is 100 credits; $1 of retail is 60. Getting
these the same way round is the mistake to guard against, and a test says so by name.

**`AiActionScope` (Core) is how the charge point learns which act it is serving.** The two facts live in
different places — only the service knows an action's boundary, only `MeteredChatClient` sees the provider
call — so the action rides an `AsyncLocal` ambient scope that every AI service opens around its own work.
Three properties are load-bearing: it flows DOWN only (a side task's scope can't leak into its caller);
disposing restores the ENCLOSING scope, not null (a chat turn that adapts a recipe through a tool is still a
chat turn afterwards); and `TryClaimCharge` succeeds exactly once per scope, which is what makes "a chat turn
is 2 credits" true however many tool rounds it took.

⚠️ **An unlabelled call is charged, not skipped.** A metered call with no scope falls back to
`CreditsForCostMicros` — its raw cost, rounded up. An AI service nobody has labelled yet reads as the old
cost-denominated behaviour, visible on the balance and in reconciliation, rather than silently becoming
free. That is the direction a pricing hole has to fail in.

**`ServiceMarginDay` + `ServiceMarginMeter` are §7.2(d).** Per day and per action: calls, charges, credits
charged, cost. Box-wide operator data in auth.db with no household id — pinned by a reflection test, because
a household id arriving on it later would silently make it tenant data that export and delete-my-data both
owe something to. It records in EVERY key mode and at EVERY tier (a Founder's calls cost real money and bill
nobody), so margin is read from `Charges`, never `Calls`. Rendered on `/admin` as "Margin by service", with
cost-per-CHARGE (on `/admin`; the price list is in Settings) and an em dash where nobody was charged.

**`CreditDenominationMigration` converts the existing ledger once**, at the anchor, in ONE transaction. ⚠️ The
transaction is why it is a class of its own rather than an `AdditiveSchema` line: the ALTER and the UPDATE
must commit together, or a crash between them leaves the column present, the conversion skipped (it keys on
the column's absence), and every pre-existing balance silently reading zero. `AmountCredits` is deliberately
NOT in `AdditiveSchema.Apply` for the same reason — an additive pass adding it first would disarm the guard.
The old `AmountMicros` column is kept as `LegacyAmountMicros`: the receipt for the conversion, so every
converted balance can be audited rather than taken on trust.

**The price list is published in Settings**, rendered from the same `BillingOptions` the charge reads, so
what it quotes and what it charges cannot drift. Zero-priced actions say "free" rather than "0".

**Testing.** 3,297 green across the four suites, 0 warnings on a non-incremental Release build. 19 hand
mutations, each killing exactly its test. ⚠️ **Scoped Stryker then found four survivors the hand pass
missed** — all guards against a misconfigured anchor or a negative pack, none of which I had thought to
break: a `CostDollarsPerCredit` of 0 would have thrown `DivideByZeroException` on the grant path, and a
negative pack would have computed to −303 credits. Closed; `--since:master` reads **100.00%**. This is
item 59's rule earning itself again: hand checks catch the behavioural mutants, only the exhaustive gate
catches the degenerate ones.

⚠️ **One test was written and then deleted, and the gap is the record.** A racing test for
`TryClaimCharge`'s atomicity killed a deliberately non-atomic version in only four runs of six, even with
real threads released from one gate and the race re-run 200 times. A test whose kill is a coin flip claims
coverage it does not have and will eventually fail on CI for reasons no change caused — strictly worse than
saying so. The atomicity is held by reading `Interlocked.Exchange`, and the note sits on both the method and
the test file.

**Also removed:** a `Math.Max(1, ...)` in `CreditsForCostMicros` that survived mutation because `Ceiling` of
any positive quotient is already ≥ 1. It was dead code, not an untested guard, so it went rather than
acquiring a test that could not fail.

**The two placeholders stand.** TTS-per-read (3) and realtime-per-minute (12) are still estimates and still
need EL invoices measured against real usage before they can be called prices.

---

## 8. What is not in the arc, and why

### D1 — "no logic in `.razor`" — partially, opportunistically, never as a big bang

18,500 lines of Razor across 40 components, with four pages over a thousand lines each
(`ProductDetail` 1,513, `Settings` 1,430, `Recipes` 1,359, `Reports` 1,185). Draining them is a
multi-week rewrite of the most-tested, most-live-verified surface in the app, for no behaviour change.

The honest version: **phase 3 builds the place logic goes, and logic moves when its page is touched for
another reason.** The pages have 600 bUnit tests over them; those tests are what make an incremental
extraction safe, and they are also what make a big-bang rewrite expensive to re-verify. The precedent is
already in the repo and it worked: `MealStock` and `SpendForecast` came out of pages *because a change was
being made there anyway*, and both found bugs on the way out.

### D3 — EF Migrations instead of the hand-built seam — worth doing, but not before phase 7

`AdditiveSchema` + `EnsureCreated` + schema-parity tests + `NullableInviteCodeMigration` is a bespoke
migration system, and it works: it has carried ~15 in-place column additions onto live boxes without
incident, and the parity tests genuinely catch the ALTER-path-vs-fresh-path divergence that bites hand-rolled
seams. The case against it is not that it is broken; it is that it only does additive columns, so every
structural change is a "fresh DB" decision, and one already needed its own hand-written rebuild class.

Adopting Migrations means baselining two existing databases with live data on them. That is a real risk for
a real benefit, and it should not be taken in the same season as a change to how money is denominated.
**Sequence it after phase 7, as its own arc, with the same dry-run-against-a-copy discipline the invite-code
rebuild used.**

### D4 — one database instead of two — **rejected**

I listed this in the retrospective and I now think it is wrong, so it is worth saying so plainly rather
than quietly dropping it.

The split is load-bearing in three ways the audit did not weigh properly:

1. **auth.db has no tenancy query filter, deliberately.** It is the operator and money space. The credit
   ledger lives there specifically so that a household cannot reach it, and so that there is no filter to
   punch through when the operator legitimately needs to.
2. **"Delete all my data" must not destroy money.** Today that is guaranteed *structurally* — the wipe
   operates on the pantry context and the ledger is not in it. Merged, that guarantee becomes a
   remembered exclusion in a delete path, which is exactly the class of thing this repo's history shows
   getting broken later.
3. **`EnsureCreated` builds the identity schema anywhere with no migrations**, which is what makes a
   self-host work at all.

The cost of the split is the one genuinely awkward thing: the usage write and the ledger write cannot share
a transaction, which §4 already documents honestly as a bounded, logged, best-effort pair. That is a fair
price for the three properties above. **Keep two databases.**

---

## 9. What the review gates found (2026-09-19)

Phase 7 went through both gates — a code review and a security review, on different models, reading
`dd80ec4..e1b9daf` by SHA. Neither found a tenancy break, an injection, or a fault in the one-shot ledger
migration. Between them they found **five money-correctness defects that a fully green 3,297-test suite
could not see**, every one of them the same shape the repo's top directive names: a surface stating
something the engine does not do.

**Every one is now held by a test that fails when the defect returns**, which is the only part of this
section worth trusting in six months.

| What was wrong | Where | Now held by |
|---|---|---|
| A 31-day meal plan was charged **18×**: the scope sat inside the generator, so each 7-slot batch opened a fresh one with its own unclaimed charge | `MealPlanService` / `AnthropicMealPlanGenerator` | `A_whole_plan_is_charged_once_however_many_batches_it_takes` |
| A reroll of one dinner was charged — and ledger-labelled — as a whole meal plan | `MealPlanService.RerollAsync` | `Swapping_one_meal_is_charged_as_a_swap_not_as_a_whole_plan` |
| "Reading a recipe aloud — 3 credits" and "A minute of live voice — 12" were published and charged by **nothing**; neither speech path enters the metering layer | `Settings.razor` / `BillingOptions.CreditPrices` | `The_published_price_list_quotes_exactly_the_actions_something_charges_for` (a source scan) |
| The published copy promised tool rounds were free, while a chat turn that generates or adapts a recipe deliberately charges both | `Settings.razor` copy vs `AiActionScope` | the copy now says what the engine does |
| `/admin`'s "Cost / charge" divided EVERY call's cost by only the CHARGED calls — several times an action's true cost on any box whose main user is a Founder, which is every box here | `ServiceMarginMeter` / `Admin.razor` | `Cost_per_charge_counts_only_what_a_billed_household_actually_cost` |

And three robustness findings, all about a number the operator could set wrong:

- **A zero anchor would have inflated the irreversible migration ~165×.** `RetailMicrosPerCredit` clamps to
  1 where its two siblings return 0 — right everywhere except in a one-shot conversion, where dividing by
  one micro turns a $1.65 grant into 1,650,000 credits with no second boot to undo it. Now: `BillingOptions`
  is validated at boot (`ValidateOnStart`), and the migration refuses independently.
- **Pack sizes were literals pinned against the COMPILED defaults**, not the configured anchor, so editing
  `Billing:CreditMarkup` sold $5 of credit at a rate that no longer applied. `BillingCatalog.PacksMatchTheAnchor`
  is now a startup check against the live options.
- **A failed ledger write cost the whole action, not one call.** The claim is taken before the write (which
  is what stops two parallel rounds both charging), so a failure that kept it made every remaining round
  free. `AiActionScope.ReleaseCharge` hands it back.

### What it cost to find

Two subtractions and one admission came out of this round, and they are the honest part:

- `CreditPricing.CreditsFromRetailMicros` was **deleted**. It had no production caller — the migration does
  the same conversion in SQL — so it was a second definition of one rule with a test standing between them.
  The rounding table now runs against the SQLite that actually converts the money.
- `SettingsPageTests.The_price_list_is_published_beside_the_balance` asserted two labels and the word
  "free" and **no price at all**, under a comment claiming it stopped prices drifting. It now asserts the
  numbers.
- Two shapes leak an `AiActionScope` and end in a FREE call rather than an over-charge: `Begin` from a
  non-`async` method, and fire-and-forget started inside a scope. Neither exists in the code today, and
  neither would announce itself if it did. `AiActionScopeSiteTests` scans for the first; the second is only
  written down.

### The fix pass got its own gate, and that was the point

Both gates ran again over the fix commit, because the repo's own history says a fix round is where the next
round of defects arrives. They found five more, and **every one was in the fixes rather than in the original
code** — which is the argument for reviewing a fix pass at all.

- ⚠️ **The headline fix was guarded by a comment, not a test.** The 18× defect was *a scope opened inside
  the generator*; re-adding that line brings it straight back, and nothing failed. `MealPlanServiceTests`
  drives a FAKE generator, so a scope opened in the real one is structurally invisible to it, and the
  price-list scan built a `HashSet` and threw away which file each site came from — a second site for an
  action already in the set changes no set. `AiActionScopeSiteTests.Each_action_has_exactly_one_place_its_charge_begins`
  now counts boundary files per action against a written-down map. Verified by re-adding the line: it fails.
- ⚠️ **The scanner was a regex over lines, and both of its rules had holes.** It skipped every `.razor` file
  — the 18,500 lines §8 says still hold logic — so a charge begun in an `@code` block was invisible to both
  rules. Its async check walked backwards looking for a line that "looked like" a signature, which missed an
  expression-bodied member (the walk sails past the whole previous method body and reads THAT signature) and
  flagged correct code (any guard clause containing a parenthesis). It now parses with Roslyn and lifts
  `@code` blocks out of `.razor`.
- **`ReleaseCharge` could double-charge.** The release fired on any exception, including one raised after
  the INSERT committed — a connection dying on dispose — and the retry would write a second ledger line for
  one act, with no idempotency key to net them. `CreditLedger.RecordConsumptionAsync` now absorbs a
  post-commit failure and reports the row as written; it throws only when nothing landed.
- **A failed money write was recorded as NOT billable**, which drops its cost out of cost-per-charge and
  flatters the margin on exactly the calls where money went wrong. The throw can only come from past every
  billable gate, so it is `Free` (billable, drew nothing) now.
- **The pack check refused the whole app to boot on a box that sells nothing.** `BillingOptions`' own doc
  promises rates are retunable in appsettings; an unconditional pack check made that false everywhere, and
  the family box has no Dashboard, no pantry and no receipt upload if it trips. The anchor rules stay
  absolute — they guard the irreversible write — and the pack rule is now conditional on payments being
  configured. The rules moved out of `Program.cs` lambdas into `BillingOptionsValidation`, which is tested.
- **The migration's guard was narrower than its own comment.** It tested the two inputs; the harm is the
  clamped product. `CostDollarsPerCredit = 0.0000001` is positive, passes every boot check, and still prices
  a credit at one micro. Both the boot rule and the migration now ask about the computed price.

⚠️ **Still open, and Jordan's call:** text-to-speech is live, costs real ElevenLabs money, and is neither
metered nor gated — a household at zero balance can still have recipes read aloud. Its published price is
withdrawn (above), which removes the false statement but not the gap. Wiring it needs a real invoice to
price against; see `docs/backlog.md`.

**Decided (Jordan, 2026-09-19): a meal plan is charged by the MEAL.** The open question above — a plan
charged once, on an act whose size the household picks — is closed: *"A meal plan should charge based on the
number of meals being generated."* The unit is one planned meal, priced 1 credit per 3 meals, so a week of
dinners is 3 credits and a 31-day four-meals-a-day plan is 42. That sits in the upper-middle of the measured
band: a batch of seven full recipes costs about what one recipe-suggest call does (~$0.01–0.03, 1–3 credits
at the anchor), so the price recovers the work without pricing a plan out of reach.

Three things the shape of that change had to get right, and all three are held by a test rather than by this
paragraph:

- **The charging boundary moved down**, below the line that counts the slots, because the price now depends
  on them (`MealPlanService.GenerateAsync`). Loading the setup spends nothing; everything under the scope
  can. The eighteen-times defect stays fixed — the scope is still around the whole plan, not the batch.
- **Plan size got ONE definition.** `MealPlanSettings.SlotCountFor` answers "how big is this plan?" for the
  page quoting the price and for the service opening the scope. Two answers would mean a quote the charge
  then contradicted, which is the "one prediction, one story" rule wearing a different hat.
- **The unit is what the household asked for, not what we spent.** `AiActionScope.Begin(action, units:)` is
  only legal for an action the price list prices by the unit — `AiActionScopeSiteTests` fails the build
  otherwise — so nobody can quietly start passing a provider round count and turn a per-act price into a
  per-call one.

`CreditPricing.QuotePrice` renders the rate ("1 per 3 meals") on the Settings price list, and the meal-plan
page quotes the whole plan's price above the Generate button before it is pressed. Rerolling one meal
(`ServiceAction.MealReroll`) is its own 1-credit act, since it is one provider call for one slot.

### The third gate pass, on the pricing change itself

Both gates were run over the per-meal commit and both led with the same finding, which is the one worth
recording: **the change multiplied an existing exposure by twenty-one, and the note that would have stopped
anyone looking again described a milder failure than the real one.** The note said a 42-credit plan could
begin on a 5-credit balance and "land the household in the red". It could not. What actually happened:

1. The page quotes 42 credits for 124 meals and the household presses Generate.
2. `IsAiAllowedAsync` asked only whether any credit was left, so 5 > 0 passed.
3. Batch 1 succeeds and the metering tail takes the whole plan's price — the charge is claimed on the FIRST
   provider call, not the last. Balance −37.
4. Batches 2 to 18 hit the same gate, now failing, and `AiCreditsExhaustedException` is swallowed by the
   generator's catch-and-retry. Seven meals of a hundred and twenty-four.
5. `planned.Count` is 7, not 0, so the fast-fail does not trip: the plan persists and the page reports
   **"Planned 7 meals."** No error anywhere.

A household paid 42 credits, received 6% of what it bought, and was told it worked. At a flat 2 credits
this shape needed a balance of 1 to reach; at 42 it catches an ordinary paying household on its first
full-month plan. What closed it:

- **The gate asks what the act costs.** `IEntitlements.IsAiAllowedAsync(creditsNeeded)` compares the
  balance against the price of the act about to run, with a floor of one credit so a free-priced action is
  still refused at zero exactly as before. Every caller passes what it is about to do; the meal-plan page
  passes the same number it quoted. This is the "once, at the gate, for every action" fix the backlog asked
  for rather than a check bolted onto one page.
- **And it stops asking once the act is paid for.** `AiActionScope.ChargeClaimed` lets the gate tell "about
  to spend" from "already has". Without this half the fix produces the SAME truncated plan — the gate
  refuses batches 2 to 18 of a plan the household bought outright.

Four more findings, all confirmed and all fixed in the same pass:

- **"Plan size got ONE definition" was written and not adopted.** `MealPlanSettings.SlotCount` had zero
  production callers; the page asked `SlotCountFor` and the service's `SlotsFor` loop re-derived the same
  number with its own day clamp, its own empty-meals fallback and its own cap. They agreed at every input
  either reviewer could construct, and nothing held them to it — the exact half-conversion CLAUDE.md calls
  the repo's most expensive failure, with a quoted price and a charge on the two ends of it. `SlotsFor` now
  fills a count it is given, and a theory pins the plan built to the plan priced at the cap and both clamps.
- **The quote was shown to households that are never charged.** "This plan is 42 credits" is a statement
  about one household's bill, not a rate card, and a Founder pays nothing — on the box the operator demos
  from. It now follows Settings' BALANCE line rather than its price list.
- **The new number had no test and could not render under one.** `PageTestContext` registers no
  `IOptions<PaymentsOptions>`, so the block was invisible in every existing meal-plan test and nothing
  would have failed if it vanished.
- **The scanner held one direction of its rule.** It rejected a unit count on a per-act price but not a
  missing count on a per-unit one — delete `units:` from the meal plan and every plan costs one credit with
  the price list and the page both still saying 42. It now asserts the biconditional, and the two rules
  that walked the syntax trees directly no longer skip the vacuity guard.

The mutation gate also came back at **94.38%** against a break threshold of 100: `SlotCountFor` had no test
at all (Core code exercised only from the Web suite, which Stryker does not run), `AiActionScope.Units` had
none either — `Math.Max(1, units)` mutated to `Math.Min` and nothing noticed, which is a 124-meal plan
charged as one — and a third survivor was a harness defect worth knowing about: a `static readonly
BillingOptions Default` is built once per test host, and Stryker reuses that host across mutants, so no
assertion against it can ever kill a mutant in `BillingOptions`' own field initialisers.

### The fourth pass: the fix's own half-conversion

Both gates ran again over that commit and both led with the same finding, which is worth recording because
it is the repo's named failure mode *committed by the pass that was fixing an instance of it*. The
enforcement gate learned to ask an act's real price. The fourteen SURFACE pre-checks did not — the new
parameter was optional and sat behind the cancellation token, so the compiler asked nothing of them and
thirteen went on asking whether the household had any credit at all.

So the two halves of one question answered it differently. A household holding one credit was waved through
by the page for a two-credit chat turn and refused by the gate, and because every AI service fails soft the
refusal it read was **"Couldn't reach the assistant just now"** — the product looking broken to precisely
the households closest to buying more credit. The receipt extractor's retry loop made the doomed call twice
before saying it.

And the refusal message itself had become false. `OutOfCredits` / `SubscribeToUse` were reachable only at a
zero balance before; once the gate priced acts, they also fired at 41 credits against a 42-credit plan,
telling a household with 41 credits that its trial was used up. The Settings copy promising "AI pauses at
zero" was false for the same reason.

What closed it:

- **`IEntitlements.CheckAiAsync(act, units)`** is the one definition both halves ask, and it takes the ACT
  rather than a credit count so neither side can price it differently. It returns the two numbers a refusal
  needs, not a bare bool.
- **`BlockedReasonAsync`'s act parameter is REQUIRED**, which is what made the compiler ask all fourteen
  sites. That is the same mechanism that made the `IsAiAllowedAsync` half complete the first time round:
  the parameter went first there, and that half was the one that didn't drift.
- **A third message**: "This one needs 42 credits and you have 41 — add a credit pack in Settings, or ask
  for something smaller." A spent household still gets the old wording, because there is nothing smaller to
  suggest.
- **`Every_surface_pre_check_names_a_published_action`** fails the build if a pre-check stops naming a
  literal action, or names one the price list doesn't publish — verified by re-breaking a site.
- **`The_pre_check_and_the_server_gate_answer_the_same_question`** asserts the biconditional over a grid of
  balances and acts, which is the thing that was missing when the two drifted.

Also fixed in the pass: a Founder saw the price for one render (`tier` defaulted to `Free` until the first
await resolved — it is nullable now), and Settings re-derived "never charged" as `!= Founder` rather than
asking `IsUnlimited()`.

Left open by this pass, and put to Jordan: **a plan that under-delivers has still been charged in full.**
The price is taken on the first provider call, so a plan that fails outright, or comes back short, has
already cost up to 42 credits. He answered *"yeah any call that fails should probably be refunded"*, which
is the fifth pass below.

### The fifth pass: an act settles up for what it didn't deliver

The decision is one sentence — an act that was charged and did not deliver gives the credits back — and
almost all of the work was in making that sentence true in one place rather than thirteen.

**Where the shortfall is known.** Only the service running the act knows how much of it arrived; the meter
sees provider calls and cannot tell a short plan from a full one. So the act carries both numbers.
`AiActionScope` already knew `Units` (what was asked for, and what it was priced on); it now also takes
`Delivered(n)` and settles the difference when it is disposed.

**Where the money is known.** Only `MeteredChatClient` knows whether a charge was actually written, and
for how much. It hands the scope a settlement callback at the moment it writes the ledger row
(`ChargeRecorded`), so the refund is keyed to a charge that really happened. This is what stops a Founder,
or a box with no `Payments` section, minting credit by failing: nothing was charged, no callback was
handed over, and disposal settles nothing.

**What comes back.** The kept amount is `CreditPricing.CreditsFor(options, action, delivered)` — the same
call that priced the charge, not a ratio of it — so the refund cannot disagree with the charge by
arithmetic. Seven meals of a hundred-and-twenty-four keeps 3 credits of the 42 and returns 39. An act that
delivered nothing returns all of it.

**The pieces:**

- **`CreditEntryKind.Reversal`** — positive, and deliberately NOT `Refund`, which is negative and reverses
  a *purchase*. ⚠️ `UnspentAllowanceCreditsAsync` counts `Reversal` alongside `Consumption` and `Expiry`:
  a refunded allowance credit has to expire with its month, or a household could bank an allowance by
  provoking failures.
- **`AiActionScope` is `IAsyncDisposable` and NOT `IDisposable`** — that is the mechanism again, the same
  one §"the fourth pass" names. Dropping the sync interface made the compiler ask every one of the twelve
  charging sites to become `await using`, so none could keep the old non-settling shape.
- ⚠️ **`DisposeAsync` is not an `async` method, and must not become one.** An `AsyncLocal` written inside
  an `async` method does not flow back to its caller — the ambient scope would never be restored, and the
  next unlabelled provider call would be charged as this act. The restore happens synchronously and the
  settlement is handed back as a task for the caller's `await using` to await. This was found the hard
  way: the first version was `async`, and two tests caught it. ⚠️ And for the same reason the test asserting
  it calls `DisposeAsync()` straight from the test body — a lambda handed to `Assert.ThrowsAsync` would run
  the restore inside that lambda's copied execution context and the assertion would pass over a real leak.
- **`Every_act_reports_what_it_delivered`** fails the build if a scope is begun without a `Delivered` call
  in the same body. Without it a new charging site would silently refund every act in full, which is the
  more expensive direction of this bug.
- **`CreditPricing.DescribeCharge` / `DescribeReversal`** put the ledger's wording in one place, so the
  reversal line names the same act as the charge line: *"A meal plan — refunded, 117 of 124 meals never
  came back"*.
- **The reversal never takes the act down with it.** `ReverseUndeliveredAsync` logs a failed give-back at
  `Error` rather than throwing, because a household that got its plan should not see it fail over a refund
  it doesn't know it is owed. ⚠️ Including cancellation, which is this repo's house rule deliberately not
  applied — the one caller is `DisposeAsync`, which passes `CancellationToken.None`, so there is no
  cancellation to honour and the usual rethrow clause could only carry a spontaneous provider-layer
  cancellation out of a `finally`, replacing a delivered answer with a crash. `RecordUsageAsync` in the
  same file had already made that call for the same reason; the first version of this method contradicted
  it, and a gate caught the pair.


### The fifth pass got its own gate, and it found the pass's own worst habit

Both gates ran over the settlement commit. The design held — tenancy, the once-only settlement, the
ambient restore, the reversal arithmetic and the allowance interaction were all checked adversarially and
all stood. What they found instead is worth recording, because it is the same shape as the fourth pass's
finding, committed by the pass that was fixing an instance of it.

⚠️ **The refund was applied to acts that did not fail.** Seven sites reported delivery only when the result
was non-empty, so a model that correctly answered *"there is no recipe in this photo"* — its own
anti-hallucination floor, a successful call the host paid for — refunded the household in full. That is
not "any call that fails is refunded"; it is "refunded unless it produced rows", and the difference is
that the second one is free on demand. Upload something unreadable, get the credits back, repeat: the
credit balance, which is the *designed* money bound, stops bounding those acts entirely, leaving only
`AiUsageMeter`'s 1000-calls-a-day abuse cap. Worse, `AiUsageMeter`'s own comment reasons that failing its
cap open is safe *because* "the real MONEY bound is the household's credit balance" — a sentence the
settlement commit quietly made false. **Four of the seven are priced at zero today** (`TagSuggest`,
`SubstituteSuggest`, `IngredientAlternatives` twice over), **so only the recipe trio bites** —
`RecipeSuggest`, `RecipeAdapt` and `RecipeImport`, 2 credits each. The other four are latent, waiting for
an operator to price a tag suggestion above free. `ReceiptExtraction` and `CensusPhoto` are clean: both
deliver on any parse success, zero rows included.

The root cause is one conflation: *the act failed* and *the act answered, and the answer was empty* are
different things, and only the first is a refund. Put to Jordan as a decision rather than patched, because
which one a household should pay for is a product call, not an engineering one.

**What the gates found that was simply wrong, and was fixed in the same pass:**

- ⚠️ **`RerollAsync` reported delivery before its write.** Its sibling in the same file settles after
  `PersistAsync` with a comment explaining why — and was contradicted by the other method in the very
  commit that wrote the comment. A reroll whose `SaveChangesAsync` threw charged the household for a meal
  it never got, and settled nothing, because the act looked complete. The rule now lives once, on the
  class, and both sites point at it.
- ⚠️ **A chat turn refunded in full after it had already written to the pantry.** Round one creates three
  products, round two loses the provider: the turn refunded 2 credits for work the household can see in
  its own pantry. The same method already stated the opposite rule fifty-eight lines further down, for the
  turn-limit exit.
- **The ledger arithmetic had no tests at all.** `CreditLedgerTests` was not in the commit. Deleting the
  new `Reversal` term turned nothing red in four suites — the exact risk the commit message asserted was
  handled. Five tests now hold it, and both new terms were verified by re-breaking them.
- ⚠️ **A reversal that lands after a month rollover was swept as the new month's allowance.** An act can
  straddle the boundary — 124 meals is eighteen provider calls — so its reversal gets a higher id than the
  *new* allowance and reads as credit returned to a month that never paid it out. The sweep then computed
  an unspent larger than the grant and took the difference out of *purchased* credit: the household's own
  money, silently. `unspent` is now clamped at both ends.
- ⚠️ **`/admin`'s margin table kept the gross charge on refunded acts** — the one place an operator could
  have noticed any of this. A 124-meal plan delivering seven recorded 42 credits and one charge while the
  ledger netted to 3, so the table flatters worst exactly when the failure rate is worst, and a household
  running the refund hole would read as the best customer on the box. Two surfaces answering "what did
  this act earn?" with their own arithmetic, again.
- **The build rule only held half the guarantee.** Dropping `IDisposable` forces the *existing* sites to
  convert, and asks nothing of a new one written as a plain `var action = Begin(...)` — which compiles
  clean, since this tree has no analyzer config. That is worse than a missed refund: the scope is never
  disposed, so the ambient one is never restored and every later AI call on that flow rides a stale,
  already-claimed scope **for free**. `Every_scope_is_opened_with_await_using` now holds it, and
  `Every_act_reports_what_it_delivered` pins `Delivered` to the scope's own identifier rather than
  matching any method of that name in the body.
- **`DisposeAsync` restored unconditionally**, so a stray second close reinstated an enclosing scope that
  had itself closed. The close is now taken the way the charge and the settlement are taken, and
  `ChargeRecorded` refuses loudly on a closed scope rather than attaching a callback that can never run.
- **A `catch (OperationCanceledException) { throw; }` that could only ever hurt.** The one caller passes
  `CancellationToken.None`, so the clause could only carry a spontaneous provider-layer cancellation out
  of a `finally` — the exact outcome the method's own ⚠️ paragraph forbids, and the opposite of the call
  `RecordUsageAsync` makes 140 lines up for the same reason. The plan text claiming "cancellation still
  propagates" was an artifact describing a path that could not occur; §6's warning, earned again.
- Smaller: the refund logs carried no household id; `ReverseConsumptionAsync`'s result was discarded under
  a log line asserting a give-back; `CreditLedgerEntry`'s own docs never learned about `Reversal` and
  declared it out of value order; a new test pinned a sentence a test fifty lines above rejects as reading
  like a bug; and six files picked up a UTF-8 BOM from the editor.

**What it cost to find:** nothing shipped. Every item above was caught by reading the diff against the
question *"who pays for this, and does the screen beside it agree?"* — and every one of them was under a
fully green four-suite run, a clean non-incremental build, and a 100% scoped mutation score.

### The sixth pass: the fix pass needed its own

Both gates read the fix commit, and this is the part worth keeping. **The fixes introduced two new
defects**, which is the cascade `CLAUDE.md` describes — rounds of 5 → 3 → 8 findings on the census branch —
happening again, to a pass that was itself closing a round of findings.

⚠️ **`ChargeRecorded` was given the power to throw, inside the `try` whose `catch` hands the act's charge
back.** That catch's own comment states the invariant it rests on: releasing is safe *only* because
`RecordConsumptionAsync` throws exclusively when the row did not land. The new throw fires after it lands.
So on the leaked-scope shape the guard was written for, the sequence became: charge the household, refuse
the settlement, release the claim, and let the next call claim and charge **again** — unbounded, and with
the exception swallowed upstream as "this call didn't draw the balance", which is the opposite of what
happened. A guard against a silent free call became a repeated over-charge. It is out of that `try` now,
and a test holds that a refused late charge keeps its claim.

⚠️ **The clamp did not close the hole its own comment claimed.** `Math.Clamp(unspent, 0, granted)` bounds
the total, and the damage is done by a *misattributed* reversal **smaller** than the grant, which clears the
clamp untouched. Both gates walked the same sequence by hand and got the same answer: an act charged in
October, settling in November, still lets December's sweep take credit out of a purchased pack. And the
test named for that defect passed identically with and without the clamp — a coverage claim not held, in
the branch's own vocabulary.

The real fix is attribution: a `Reversal` row now records **which consumption it hands back**
(`CreditLedgerEntry.ReversesEntryId`), `RecordConsumptionAsync` returns the id of the row it wrote, and the
unspent-allowance sum counts a reversal by the period its **charge** drew on rather than by where the
reversal happened to land. The clamp stays as a bound on a sum gone wrong some other way, with a comment
that no longer overstates it. A reversal that cannot be attributed is left out, which is the only direction
that cannot eat purchased credit.

Also in the pass:

- **`actions.Count > 0` was the wrong predicate** for "this chat turn did something". That list is what the
  turn will *tell* the household it did, and the read-only tools put their lines in it too ("opened
  reports", "reading Chili") — so a turn that navigated and then lost the provider was charged 2 credits
  for a turn that wrote nothing and, because the failure exit discards the navigation, showed nothing
  either. `TurnWrites` is marked beside each write instead.

  ⚠️ **And the first version of that got it wrong in the way this repo keeps getting it wrong.** The rule
  was implemented as "the `_store` write calls", which is ten sites — but the question is *"did the
  household get something it can see afterwards"*, and `adapt_recipe` saves a recipe variant through
  `IRecipeAdapter`, not through `IPantryStore`. Eleven writes, ten converted, and this paragraph asserted
  the conversion was complete. A turn that adapted a recipe then refunded in full, which the *previous*
  predicate had charged correctly — a regression created by the fix.
- **The margin reversal was landing on the wrong day and could go negative.** It took the correction off
  *today's* row when the charge might sit on yesterday's — correcting the wrong day against the right
  number, leaving both permanently wrong — and `CostPerCharge` reads null below zero charges, so the
  operator's "is this price right?" answer would have quietly disappeared for that action. The charge's own
  day is stamped into the settlement now, the decrements are floored, and a reversal that finds no row says
  so instead of vanishing.
- **The refund re-priced itself from live config** while the charge had been stamped at charge time. ⚠️ Not
  actually reachable, and the first version of this line claimed it was: `billing` is `IOptions<T>`, whose
  `.Value` is resolved once for the process, so there is no mid-act edit to disagree with. The capture is
  still the right shape — it survives a later move to `IOptionsMonitor` — but it closed nothing, and a
  changelog that claims a fix nobody needed is the §6 problem in miniature.
- **The `await using` rule could be satisfied by an unrelated `await using` block** somewhere up the
  ancestor chain, and neither it nor the settlement rule had the non-vacuity guard the rest of the file
  carries. Both anchored and guarded, and both verified by planting each shape and watching the build fail.
- **A new test asserted only zeroes**, which "nothing happened" satisfies just as well as "charged, then
  refunded". It pins the charge inside the scope now and reads both ledger rows back.

**What it cost to find:** nothing shipped, again. But the lesson is the one already written at the top of
`CLAUDE.md` and worth the second entry: a fix pass is not a safe pass. Two of these were *created* by the
round that was fixing the previous round's findings, and both were in the money path, and both had a green
four-suite run and a 100% mutation score over them.

### Fifth pass: what an act has to do to keep its money (Jordan, 2026-09-19)

The open question from the refund design — *does a successful call that honestly answers "there's nothing
here" charge the household?* — is decided: **yes**. The household pays when the assistant answered,
whatever the answer said; the refund is for the act that **failed**. §4.w of `docs/subscription-plan.md`
carries the rule and the reasoning (refunding an honest "no recipe in that photo" prices the assistant's
honesty — it would pay more for inventing a recipe than for telling the truth about a blurry photo).

What that took was not seven `if`s deleted. It was the nine-sites problem again:

- **"Did this act deliver?" was answered nine times, each with its own arithmetic** —
  `suggestions.Count > 0`, `adapted is not null`, `parsed.Recipe is not null`, `tags.Count > 0`,
  `match is not null`, `substitutes.Count > 0`, `alternatives.Count > 0`, and two that settled on a
  successful parse and were therefore *already right by accident* (the receipt extractor and the census
  reader ignore how many lines came back). One `AiActionScope.Answered()` now, at every site asking that
  question — the partial-conversion rule from `CLAUDE.md`, which this branch has already paid for once.
  ⚠️ **Two single-unit `Delivered(1)` calls deliberately remain**, and the first write-up of this pass
  said "every caller" without saying so — then the correction said *three*, counting the chat's turn-limit
  exit, which the same commit had already changed to `Answered()`. A correction that is itself wrong is
  the §6 failure with an extra step. The two are the meal-plan reroll, which settles on its write being
  *durable* (after the commit, per that class's own rule), and `TurnWrites.Mark`, which settles on a
  pantry write landing. Neither is "the provider answered", and renaming them would be a worse kind of
  uniformity.
- **`Answered()` is `Delivered(Units)`, and on a per-unit act that is a real money bug** — a meal plan
  that produced three meals out of twelve would keep the credits for nine that never arrived. Held by a
  new build rule (`An_act_priced_by_the_unit_counts_what_it_delivered`) rather than by a runtime throw,
  because a throw on the money path turns a billing mistake into a failed meal plan for the household
  that asked for one. Verified by planting `Answered()` on the meal plan and watching the build fail.
- **Three of the four prose advisors settled *after* the `NONE` sentinel's early `return`** — so the
  first version of this change shipped comments saying "NONE is an answer" over code that still refunded
  every one of them. Caught by writing the test before believing the diff.
- **Nothing pinned any of it.** Before this pass, no test in `ShelfAware.Llm.Tests` mentioned
  `AiActionScope`: all nine services could have their settlement branch changed, or deleted, with the
  four suites still green. `AiDeliveryTests` holds each site now, and ten of its cases were verified by
  planting a content-conditioned settle and watching them go red. ⚠️ The first write-up said "restoring
  the old condition", which is wrong for two of them — the receipt extractor and the census reader never
  had one, so what was planted there is a plausible mistake rather than the previous code. The wording
  mattered because the count was offered as the evidence the conversion was right.
- **The empty-reply line is drawn deliberately.** `NONE` is an answer; no text at all is not, and that
  act refunds. One test proves the line exists rather than leaving it to read as an accident.

Also in the pass: the last mutation survivor from the fourth round is gone, and not by suppressing it. The
late-charge guard was written as *"return early on the benign case, then throw"*, and the benign case is
only reachable when a concurrent `DisposeAsync` lands between two adjacent instructions — a window no
deterministic test can open, so the `return` was a statement nothing executed. Inverted to *"throw on the
harmful case"*, the benign case became the absence of a statement and the gate went green. Worth writing
down as a general move: an untestable early-out is often a guard written the wrong way round, not a case
that needs a racing test. (This file rejected a racing test once already — a killer that lands four runs
in six is coverage claimed and not held.)

⚠️ **Two things the first write-up of that got wrong, and both are the §6 failure.** It said "the
behaviour is identical", which holds only for the inversion in isolation: measured against the previous
commit, `ChargeRecorded` threw *unconditionally* on a closed scope, and the round-4 take-back narrowed
that. Narrowing it is the point — it stops the "charged but un-refundable" alarm firing on acts that
settled fine — but the changelog described a refactor where there was a behaviour change. And "every line
is exercised" is a claim about lines: the benign case is still untested, as a condition's false arm now
rather than a dead statement. A gate going green is not the same as ground being covered, and saying so
is what this section is for.

### Sixth pass: what the two gates found in the fifth (2026-09-19)

Both gates ran over `c240d56`. Nothing they found had cost a household money, and the list is long
because a money commit earns a long list.

- **The chat's turn-limit exit charged a turn that delivered nothing.** Every tool call coming back as
  validation text ("No product matches X") reaches that exit with nothing written, nothing listed and
  nowhere navigated, tells the household *"Stopped after several steps without finishing"*, and charged a
  full chat turn for the sentence. It settles on what it carried out now. Found by the code gate noticing
  the fifth pass had removed the *failure* exit's guard while leaving this one bare.
- **Eight of eleven new tests passed vacuously.** `RefundedFor` is written only from inside the
  settlement, so "nothing was given back" and "nothing was ever charged" were indistinguishable — delete
  a service's `Begin` line and the test stayed green. Every case asserts `Charged` now. ⚠️ The second time
  in this arc a new test asserted only the absence of something.
- **The commit's largest behaviour change had no test at all.** `TurnWrites` moving from a flag read at
  the exit to a settle at the write is what stops a cancelled circuit refunding committed pantry writes,
  and nothing exercised it. Three chat cases do now.
- **The `ChargeRecorded` contract doc said the opposite of the body** — "refused once the scope has
  CLOSED … by then DisposeAsync has taken the settlement", which is exactly the case that is now *not*
  refused — and the `<exception>` tag with it.
- **`ReverseConsumptionAsync` validated *which* charge but not *how much* or *whether already given
  back*.** Both mint credit and neither nets afterwards. The scope enforces once and the metering layer
  bounds the amount, but both live in the caller and this is the layer that writes the money.
- **The pantry chat was a sixth site leaking provider exception text to the screen**, missed when the
  other five were converted the same day. It is the worst one to leak from: the dashboard chat box and
  push-to-talk both render that string, and a chat turn is the surface a household uses most.
- **A recipe import claiming `found: true` with no name was charged** — a self-contradicting reply
  sharing a branch with the honest "no recipe here". It throws into the retry now, like every other
  invalid shape, and refunds if the retry is no better.
- **Four advisors, four spellings of "did the model say anything?"** — three testing the raw reply, one
  testing it with trailing punctuation stripped, so a reply of `"."` refunded in one and was paid for in
  the other three, under a doc paragraph saying all four drew the line in the same place. `ProviderReply`
  is the one definition. ⚠️ The fifth pass *considered* extracting this and decided it was too small to
  be worth it; one commit later it had diverged. The rule has no exemption for small facts.
- **The new build rule could not see a scope handed to a helper** — the shape this very commit
  introduces for the chat turn. A per-unit act whose scope escapes its method is the finding now, rather
  than a gap the rule passes over in silence.

**What it cost to find:** nothing shipped, again — the fourth pass in a row where that is true, and the
fourth in a row that had a green four-suite run and a 100% mutation score over it when the gates started.

### Seventh pass: what the gates found in the sixth (2026-09-19)

The fix pass introduced one of its own, which is now the pattern rather than the exception — four fix
passes in this arc, three of them with a new defect in the money path.

- **The turn-limit settlement I had just written read one of the three navigation facts its own exit
  carried out.** `go_to_step` moves a hands-free cook-along and sets `nav.Step` and nothing else — no
  actions line, no URL — so a cook-along turn that ran out of steps moved the reader on screen and was
  refunded in full for work the household watched happen. ⚠️ It is the repo's oldest failure shape, in a
  guard written *in the commit that quotes that rule*: the `ChatResult.Ok` two lines below said three
  things had been carried out and the guard beside it asked about one. `NavigationTarget.Moved` answers
  it in one place now.
- **Normalizing the reply for the money question also normalized it for the tag MATCH.** A household tag
  that legitimately ends in a period could no longer be matched back by its own spelling, so the dedup
  would coin the near-duplicate it exists to prevent.
- **The new "already given back" backstop reads stronger than it is** — a read and then a write, with no
  transaction and no unique index on `ReversesEntryId`, so it catches a repeat and not a simultaneous
  duplicate. The race it misses is the one the scope's `Interlocked` take already prevents, so the check
  stays as it is and the comment now says what it holds instead of implying more.
- **The build rule's escape check was narrower than its own remarks** (a cast or a `!` walked past it).
  Widened, and the remaining hole — a lambda closing over the scope — is written down rather than
  papered over. Its new "units: 1 is fine" exemption read the argument positionally, which decides
  whether a money guard applies; it reads the argument by name now.
- **The guard I had just written carried a term that could not do any work.** Every `actions.Add` in the
  chat sits beside a write or a navigation, so `actions.Count > 0` could never change the outcome — and
  it came with eight lines explaining why this exit was allowed to consult it. A dead term with a
  rationale attached is worse than a dead term: the rationale is what the next reader trusts. Gone, and
  the exit asks only whether it navigated.
- **The chat's final-reply exit was a FIFTH site answering "did the model say anything?"** — one method
  above the guard introduced to stop that, settling unconditionally. A round with no tool calls and no
  text tells the household "Done." when nothing was done, and charged for it.
- **The shared answer rule drew the line in the wrong place.** `IsAnAnswer` asked "is the reply empty
  once periods and spaces are stripped?" — a parser's convenience promoted into a billing predicate, so
  `"."` refunded and `"!"` was charged in full. ⚠️ Worth keeping: centralising a rule makes every caller
  agree, which is worth nothing if the rule they agree on is wrong. The first version of the shared
  definition was less correct than three of the four sites it replaced.
- **`AnthropicTagAdvisor` had no sentinel branch**, on the reasoning that "NONE" matches no tag — true
  until a household names a tag "None", after which the model's way of saying "these are different" comes
  back as a synonym. And the comment asserted the protection it lacked.
- **A correction that was itself wrong.** The sixth pass said three bare `Delivered(1)` calls remained,
  counting the turn-limit exit that the same commit had changed to `Answered()`. Two remain.

Also recorded rather than fixed: **every refund is a provider call the operator paid for**, and a few of
them are reachable on purpose because household text goes into these prompts. `docs/subscription-plan.md`
§4.y names them, says why charging for them would be worse, and names the surface that would show it
if the balance ever tips (`CostPerCharge` on `/admin`).

### Eighth pass: what the gates found in the seventh (2026-09-19)

Both gates ran over the seventh pass. The headline is that **three of the six findings are the same
finding**: a rule was moved into one place, and the one place was narrower than what it replaced.

- **The tag match was fixed in the wrong direction, and the test agreed with it.** The seventh pass
  stopped normalizing the reply so a tag ending in "." could be found — and thereby stopped finding a
  clean tag when the model appended a period, which is the *routine* case the file's own neighbouring
  comment says happens ("the model routinely appends a period"). It traded a rare miss for a common one
  and shipped a comment explaining why its half mattered. ⚠️ The test written to pin it put the period
  on **both** sides, so it passed either way: a test that cannot distinguish the fix from the bug. One
  definition now reads both sides (`ProviderReply.Names`), under a four-case `[Theory]` that fails on
  each direction separately.
- **The money predicate could not see an emoji.** `Any(char.IsLetterOrDigit)` enumerates UTF-16 code
  units and is false for *both halves* of a surrogate pair, so a model that answered `"👍"` had its
  reply rendered in the chat box, spoken on the voice surfaces, and refunded in full — as were `"✓"`,
  `"→"` and every reply written in an astral script. ⚠️ **This is the second consecutive pass in which
  the shared definition was wrong**, and both times the comment above it argued for the narrower
  question. The predicate reads runes now and asks whether the reply contains anything the household
  would read as content: symbols yes, punctuation no.
- **And the test the gate asked for found a sixth site one line below the fifth.** Pinning the chat's
  final-reply exit to `"."` and `"!"` failed immediately — not on the settlement, which was right, but on
  the display beside it: `text.Length > 0 ? text : "Done."` billed `"."` as silence while handing the
  household a bare period as the assistant's answer. Asked once, used twice now. ⚠️ The seventh pass's
  commit message claimed "every new rule verified by breaking it and watching a test fail"; this rule was
  verified on whitespace only, where the private reading and the shared one agree.
- **The build rule's own reader had an unexercised branch.** `UnitsArgument` was converted to name-first
  for the per-unit guard, but the twin rule two hundred lines away still counted arguments positionally —
  two answers to "does this site name a unit count?" in one file, agreeing only because the repo has
  exactly one per-unit site today. Converted together; and since that one site writes `units:` by name,
  *breaking the positional fallback left all nine rules green*, so the reader is now pinned directly by a
  `[Theory]` over the five legal shapes of the call.
- **Three doc claims asserted what the code did not do**, all written in the same pass that changed the
  code: `TurnWrites` settles on any write and not a *pantry* write (`adapt_recipe` goes through
  `IRecipeAdapter`, which an earlier version got wrong and the doc re-introduced); the turn-limit exit
  never asks whether the model said anything; and the seventh pass's own bullet above claimed a fix that
  made the failure more reachable. Corrected in place rather than appended to — see §6 on artifacts that
  claim to be true, which this arc has now hit five times.
- **Cancellation, swallowed in three of four advisors.** `AnthropicRecipeTagAdvisor` rethrows
  `OperationCanceledException`; the other three logged it as a degraded provider. Same four-file set the
  pass was cleaning up, and CLAUDE.md names it explicitly. Billing was correct either way (nothing had
  settled, so the act refunded); the log line was not.

⚠️ **The pattern across passes seven and eight is worth stating plainly**, because it is the argument for
the gate rather than for any individual fix: *both* passes consolidated a rule into one definition, and
*both* times the single definition was less correct than the sites it replaced, and *both* times a test
was written that could not tell the fix from the bug. Consolidation is right and the tests around it have
to be written against the inputs that distinguish the readings, not against the input that made the
original bug visible.


### Ninth pass: what the gates found in the eighth (2026-09-19)

Both gates recommended against merging the eighth pass. They converged on four of five findings, and the
two they found independently are the two that matter.

- **The rethrow I added to stop swallowing cancellation was wrong in every reachable case.** An
  unconditional `catch (OperationCanceledException) { throw; }` also catches an `HttpClient` timeout,
  which arrives as the same type — and **not one of the three call sites passes a token**
  (`Upload.razor`, `ProductDetail.razor`, `Recipes.razor` all take the default), so 100% of real
  cancellations there are provider timeouts. Each call site is a `try/finally` with no `catch`, and the
  app has no `ErrorBoundary`, so the rethrow escaped an `@onclick` handler and tore down the Blazor
  circuit — losing an in-progress receipt review, against three class summaries that promise "fails open
  so a flaky API never blocks tag creation", and taking the operator's only degraded-provider log line
  with it. Filtered on `cancellationToken.IsCancellationRequested` now, which is the form
  `AiUsageMeter`, `ElevenLabsTextToSpeech` and `LocalTextToSpeech` already used. ⚠️ Billing was correct
  throughout, which is why neither the suite nor I caught it: the defect was entirely in what a household
  loses when a provider is slow.
- **`ProviderReply.Names` was a THIRD answer to "which existing tag does this name mean?"** —
  `TagVocabulary` says in its own remarks that it is *"THE one place the dedup/canonicalization policy
  lives"*, and the private helper knew about a trailing period and nothing else. So a model that
  pluralized ("Soft Drinks"), doubled a space, or slipped a character returned null and coined the
  duplicate — one that `Upload.razor`'s plain-code stage, eight lines above the call that charged for the
  act, would have caught. ⚠️ **This is item 41's cascade re-opened, in the commit whose own notes call
  consolidation the lesson of the arc.** `Names` is deleted and the advisor asks `TagVocabulary`.
- **The money predicate's deny-list was wrong in both halves at once.** It carried an arm for
  `UnicodeCategory.Surrogate` that can never fire (a `Rune` cannot hold a surrogate) over the case that
  arm was written to refuse: `EnumerateRunes` substitutes U+FFFD for ill-formed UTF-16, and U+FFFD's own
  category is `OtherSymbol`, so mojibake fell through as content and was charged, rendered and spoken.
  Bare combining marks and orphaned variation selectors went the same way. It is an **allow-list** now:
  whatever a deny-list forgets is billed.
- **Three stale counts, one of them written by that same commit into a new code comment.** §4.y gained a
  fourth bullet under a heading reading "the three". The §6 failure, committed in the pass that documents
  it. ⚠️ **And the fix re-introduced the count into §4.y's own lead-in in the same breath** — "the five
  worth naming" — under a sentence here claiming it was "no longer counted in prose anywhere". Corrected
  in the tenth pass; the count is gone and the claim with it.
- **§4.y was understating the accepted exposure.** It omitted the two most expensive refunds in the app —
  a receipt and a shelf census each retry once at `MaxOutputTokens = 8192` on household-supplied images,
  twice the recipe importer's budget. And its cap claim was unconditional where the code's cap is not:
  `EffectiveDailyCallLimit` is null on a box with no payments config and no explicit key.

⚠️ **Three rounds running, the shared money predicate was wrong, and every time the comment above it
argued for the narrower question.** It now has a direct test file of its own
(`ProviderReplyTests`) pinning each category arm — it had none before, every input reached it through an
advisor, and the mutation gate is scoped to Core and does not reach it. That absence is the single best
explanation for why three consecutive versions shipped wrong.


### Tenth pass: what the gates found in the ninth (2026-09-19)

Both gates again recommended against merging, and both led with the same finding: **the ninth pass is
itself a partial conversion.**

- ⚠️ **Nine sites, four converted.** The ninth pass proved that an unconditional
  `catch (OperationCanceledException) { throw; }` around a provider call is wrong — a timeout arrives as
  the same type, no call site passes a token, and the rethrow escapes a Blazor handler with no catch and
  no `ErrorBoundary` behind it. It then fixed four advisors and left five siblings in the same assembly,
  **including the chat turn** (the most expensive act in the app, whose own comment says its callers
  don't wrap it) **and the meal-plan reroll** (which runs inline on the circuit, so a slow provider ate
  the plan edits on screen). The commit message quoting CLAUDE.md's "every caller in the same change"
  rule was in the commit that broke it.
  - All nine are converted now, plus `RecipeAdapter` and `RecipeTagService`. More to the point, the
    argument moved out of prose: `ProviderCancellationSiteTests` fails the build on an unfiltered
    cancellation catch anywhere at the provider boundary. The four advisors carried a ten-line copy of
    the same reasoning each, and **two of those copies named the wrong call sites** — which is the
    argument for a rule that cannot be re-typed rather than a paragraph that must be.
- **A test asserted the behaviour the same commit called a bug.** `ShelfCensusReaderTests` pinned that a
  parameterless `OperationCanceledException` must escape — exactly the timeout shape — while the new
  advisor test pinned that it must be absorbed. Two suites, opposite rules, same input, both green.
- ⚠️ **And a second test encoded a meaning the contract does not carry.** `A_recipe_that_cannot_be_
  adapted_to_what_is_on_hand_is_an_answer` asserted that an empty adaptation is paid for. But
  `recipe-adapt-system.txt` never offers the model that answer — rule 1 mandates a single recipe and
  rule 7 says return it even when nothing needs swapping. So an empty array is the model failing, not
  declining, and `RecipeAdapter` turns it into "Couldn't adapt … right now" with an invitation to press
  the button again and be charged again. **The test's NAME is where that went unnoticed**, which is a
  narrower lesson than the fix: a test can assert a semantics nobody ever agreed to.
- **My own normalization opened a denial-of-service on a free path.** NFC's canonical ordering is
  quadratic in the length of one run of combining marks, and `FindNearDuplicate` runs at stage one of
  `Upload.AddTag` — before the advisor, so before any credit gate or usage cap — against a box with no
  `maxlength` and a column with no length, behind a 4 MB SignalR limit. `TagVocabulary.MaxLength` caps
  it at 64 now. ⚠️ Worth keeping: the ninth pass added `Fold` as a *correctness* fix to the one place
  that owns tag identity, which was right, and made a linear path input-sensitive without noticing.
- **`FindNearDuplicate` let list order decide the answer** — one pass checking both conditions meant a
  one-edit neighbour earlier beat an identical tag later ("Pans" → "Pants" over "Pan"). Tolerable while
  it only read text a person typed; a wrong answer once the ninth pass pointed the LLM advisor at it.
  Two passes now.
- **The new `ProviderReplyTests` missed four of its twelve arms**, and one of them was `UppercaseLetter`
  — the arm over `"NONE"`, the most common reply these advisors get and one §4.w pays for. Deleting it
  left every suite green, because an advisor answers a sentinel and a refund with the same null. ⚠️ That
  is this arc's signature failure reproduced **inside the file written to end it**.
- **§4.y called the two vision refunds "the most expensive in the app". They are not.** A meal plan is
  up to eighteen batches at 8192 output tokens, charged per meal up front and settling on what landed.
  Added, along with the reroll and first-batch fast-fail, and the quiet-chat bullet now says it is
  steerable by the household's own words rather than a matter of luck.
- **A correction that was itself wrong, again.** The ninth pass's commit message said "TagVocabulary
  joins the mutation scope". It was already in scope — `stryker-config.json` names the Core project with
  no filter — so the claim described work that was neither done nor needed. The file genuinely was added
  to the *sweep command's* `--mutate` list, which is not the same thing.

⚠️ **The pattern to state, four passes on:** consolidating a rule is right, and every time this arc has
done it the single definition shipped narrower than the sites it replaced, or the conversion stopped
part-way. The two things that actually changed the outcome were not better prose — they were
`ProviderReplyTests` (a direct test where there had been none) and `ProviderCancellationSiteTests` (a
build rule where there had been a comment repeated four times, wrong twice). Rules that only live in
prose get broken; that is already in this repo's memory, and it has now cost four rounds to relearn.


## 10. Sequencing

```
1. docs reset          ──┐
2. toolchain pin       ──┤  independent; any order; each its own gate
3. presentation layer  ──┤
4. error copy          ──┤
5. operational floor   ──┤
6. truth automation    ──┘

7. the credit unit        ← unblocked: anchor decided 2026-09-19 (§7.5)
   (then, separately)
   EF Migrations          ← its own arc, after 7
   logic out of .razor    ← continuous, never a phase
```

Every phase merges to `master` only through `/pre-push` — code review *and* security review — per the
house rule. Phase 7 gets two independent gate passes on different models, like the census and
email-confirmation arcs did, because it is money.
