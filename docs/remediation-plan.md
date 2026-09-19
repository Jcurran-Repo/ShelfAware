# Remediation plan — the 2026-09-18 audit and the architecture retrospective

Jordan's ask (2026-09-18): *"Id like you to fix the findings AND the what youd do differently... feel free
to split it up as needed and design first."* This is the design.

The audit produced two lists — seven findings and nine "what I'd do differently" items. They overlap
heavily: five of the nine retrospective items are the *general form* of a specific finding. So the real
arc is **seven phases, not sixteen**, each its own branch and its own `/pre-push` gate.

Two items are deliberately **not** in the arc, with reasons in §9. Read that before asking where they went.

---

## 0. The map

| # | Phase | Closes | Size | Risk |
|---|---|---|---|---|
| 1 | The docs reset | F5, D8 | M | none — no code |
| 2 | Pin the toolchain, unbreak the parse | F2, D7 | S | low |
| 3 | The presentation layer | F3, D2 | M | low |
| 4 | Errors stop leaking provider text | F4 | S | low |
| 5 | The operational floor | F6, F7a, F7b, D6 | M | low |
| 6 | Make the truth-claiming artifacts self-maintaining | F1, D9 | S | low |
| 7 | The Shelf Aware credit | D5 | L | **high — money** |
| — | Logic out of `.razor` | D1 | XL | see §9 |
| — | EF Migrations | D3 | L | see §9 |
| — | One database | D4 | XL | **rejected — see §9** |

Phases 1–6 are independent and can land in any order. Phase 7 depends on nothing but deserves to go last
because it is the only one that touches money, and because it is *cheapest to do before the first paying
customer* — see §7.

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

**The fix, both halves:**

1. `global.json` pinning the SDK with `rollForward: latestPatch`, and CI reading the version from it rather
   than from a wildcard in the workflow. One declared toolchain, used by every machine.
2. Rewrite the five relational-pattern sites so they parse under any SDK — `_ when days < 0 =>` is
   equivalent, unambiguous, and verified to compile on 10.0.112. The sites:
   `Home.razor:517`, `GroceryList.razor:730`, `Products.razor:423`, `ProductDetail.razor:1493`,
   `SpendInsight.razor:194`.

Four of those five disappear anyway in phase 3 — but the pin and the rewrite should land first, because
until they do, a fresh machine cannot build the repo.

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
attempt to drain the pages of logic wholesale — see §9 on D1 — it establishes the place that logic goes,
and moves the demonstrated duplicates into it.

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

### 7.5 Open, for Jordan

1. **The anchor.** $0.01-of-cost per credit is proposed above because it makes the price list read in small
   whole numbers and keeps §3's economics intact. A cheaper credit (say $0.001) gives finer granularity at
   the price of four-digit balances. This is a product-feel call, not a technical one.
2. **The two placeholders.** TTS-per-read and realtime-per-minute cannot be priced honestly until EL
   invoices are measured against real usage — §8's standing open item. They ship as config with a comment
   saying they are estimates, and get corrected from evidence.
3. **Naming.** "Credits" is the safe default. If they get a Shelf Aware name, the abstraction is more
   obviously deliberate — but it also has to survive being said out loud.

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

## 9. Sequencing

```
1. docs reset          ──┐
2. toolchain pin       ──┤  independent; any order; each its own gate
3. presentation layer  ──┤
4. error copy          ──┤
5. operational floor   ──┤
6. truth automation    ──┘

7. the credit unit        ← after Jordan signs off on §7.5
   (then, separately)
   EF Migrations          ← its own arc, after 7
   logic out of .razor    ← continuous, never a phase
```

Every phase merges to `master` only through `/pre-push` — code review *and* security review — per the
house rule. Phase 7 gets two independent gate passes on different models, like the census and
email-confirmation arcs did, because it is money.
