# Shelf Aware — subscription & credits plan

**Status: product decisions made (Jordan, 2026-08-23); nothing built.** This is the spec-in-progress for
the billing workstream that CLAUDE.md item 9 deferred ("billing/pricing = Jordan's separate workstream")
and that the founder-tier design (2026-08-23, same day) was parked into. When the arc starts, this doc is
the handoff — same role `graphql-api-plan.md` and `undo-history-plan.md` played for theirs.

Open questions for Jordan are collected in §8; everything else is either his stated decision or a
recommendation labeled as such.

**Review gate (2026-08-23): three independent agent reviews — economics audit, business red-team,
codebase-integration — each ran with fresh context and an adversarial brief. All three verdicts:
SOUND-WITH-FIXES.** The economics audit re-derived every internal number exactly (fees, floors,
discount math, break-even, token estimates — all confirmed to the cent) and found the defects only
where the doc had quoted the outside world from memory; the red-team and integration reviews
independently converged on the same three headline gaps (voice enforcement, refund/clawback, the
Lemon Squeezy transition). Every accepted finding is folded into the sections below; the fee basis
throughout is now LS's real subscription rate (5.5% + 50¢ — §6).

---

## 1. The product

| Tier | Display name | Price | Managed AI | Granted by |
|---|---|---|---|---|
| Free | **Shelf** | $0 | None past the welcome grant (§1/§2) — the full manual app | default |
| AI | **Aware** | **$2.99/mo · $27.99/yr** | On, on the host's keys, **incl. conversational voice** (chat back-and-forth); includes **~$1.00/mo of AI at cost** | subscription (Lemon Squeezy — §6) |
| Founder | **Founder** | $0 | **Unlimited**, usage still recorded | admin toggle (the thank-you tier for early helpers) |

**Sous Chef removed — voice folds into Aware (2026-09-02, Jordan's call, supersedes the 2026-08-30 split):**
there is no separate voice tier. The *conversational* voice — talking with it back and forth — sits in
**Aware**, and the premium realtime **"Live agent"** cook-along (the ElevenLabs realtime agent) has been
**hidden** (the rebrand pass), so the feature Sous Chef was going to charge for no longer ships as a paid
add-on. The **built-in cook-along reader stays free** — only the paid EL realtime agent was retired; a
future free-voice swap (Jordan's plan) keeps the brain and just changes the TTS/STT seam. The `souschef`
reserve tier is gone from `WishlistTiers` and `/about`; any reservation stored under it before today stays
as historical signal (the wishlist `Tier` is a string, never a `HouseholdTier`). Founder stays an
admin-granted GIFT — never a selectable/purchasable tier (so it's absent from the /about reserve picker;
the paid early-supporter funnel there is a generic "back it early" link, not "become a Founder").

**Names decided (2026-08-23, "set A" — Jordan: "it's fun"):** the free→paid upgrade reassembles the
app's name — *your Shelf becomes Aware*. (The set once had a third "voice tier" — Sous Chef, the
cook-along made literal — dropped 2026-09-02; see §1.) The doc below keeps descriptive labels
(Free tier / AI tier) for clarity; the display names are what surfaces render.

**Why $2.99, not the $1.99 first floated (decided 2026-08-23):** Jordan's constraint is *never raise
prices on early supporters*, and $1.99 couldn't keep that promise — its margin floor after payment
fees is ~$0.39/mo on merchant-of-record fees (§3/§6), with three near-term forces pressing on it:
Haiku-tier model pricing has **risen 4× across two generations** (3: $0.25/$1.25 → 3.5: $0.80/$4 →
4.5: $1/$5), so the next model upgrade squeezes COGS with no room to absorb it; EL realtime voice is
so expensive (~one 15-min session > the whole monthly grant) that only a thicker cushion ever lets
the tier include meaningful voice; and fixed costs (EL plan minimum, hosting, disputes at ~$15 each)
need per-sub margin to amortize. $2.99 raises the floor to ~$1.33/mo (44%) while staying impulse-tier. **Same logic applied to
the grant:** it stays modest (~$1 at cost) at launch — shrinking a grant later feels like a price
hike, so generosity gets added after real `AiUsage` calibration, not promised up front.

**$4.99 was considered and declined for the BASE (2026-08-23):** the sufficiency argument that forced
$1.99→$2.99 is satisfied at $2.99, pantry-app category anchors are low (AnyList's household plan is
~$15/yr), and the one legitimate case for $4.99 — voice *included* in the promise — is a product
decision that waits for EL instrumentation. Under the never-raise constraint tiers can always be ADDED,
never lifted, so the base launches text-centric at $2.99 and a richer paid voice tier could still be
ADDED later if wanted — breaking no promise to anyone. ⚠️ **The specific Sous Chef tier that occupied
this slot was DROPPED (2026-09-02 — see the tier table above):** the realtime Live agent is hidden and
voice folds into Aware, so there is no planned voice tier today; the mechanism (add-a-tier-later) is what
survives, not the tier.

**Annual pricing (decided 2026-08-23): steep discount, annual-first posture.** Base **$27.99/yr ≈
$2.33/mo effective** (Jordan's number; ~22% off — MoR net ~$25.95, floor at full grant use
**~$1.16/mo**). A possible future voice tier (dropped for now — see above) sketched at **$4.99/mo /
~$47.99/yr ≈ $4.00/mo effective** (Jordan's "steep annual, ~$3.99 effective") — the two discounts
deliberately rhyme at ~20%.
**The discount is cheaper than it looks:** a perfect 12-month monthly subscriber nets ~$27.91/yr
(twelve fixed fees) vs the annual's ~$25.95 — so the 22% sticker discount costs only **~$2/yr ≈ 7%
net**, and break-even tenure is ~11.2 months: any monthly subscriber who'd churn before month 11
makes the annual the BETTER outcome. 100% annual take-up is the good scenario (max cash, max
retention, floor still positive) — there is no monthly/annual mix that loses. **On annual billing
the grant still drips monthly** — no $12 lump to binge through in week one. ⚠️ A
discount this steep means most subscribers take annual, so **the annual floor is the real floor** —
acceptable because typical usage runs under the grant, annual pays the fixed payment fee once instead
of twelve times, fronts the cash, and locks in retention for a history-compounding app; but it holds
ONLY while the grant stays modest. A steep annual plus a fat grant quietly rebuilds the $1.99
problem. Corollary worth naming: under never-raise, the two safe levers are both structural — **add
tiers; soften discounts for new subscribers** — neither touches an existing subscriber's price.
⚠️ **Define the promise publicly as PRICE, not action-count, from day one** (review finding): the
grant is dollar-denominated and consumption reprices at call time, so a model-price rise silently
shrinks what $2.99 buys — experienced as exactly the stealth raise the promise forbids unless the
promise was always "your price never rises," with the option kept open of pinning included usage to
a cheaper model.

**Credits:** prepaid balance, consumed once an AI-tier household exhausts its monthly included allowance.
Priced at **65% markup** over raw API cost. **Credit cost is tiered by what the action really costs**
(Jordan, 2026-08-23): text generation + receipt reading are the cheapest class, **voice costs more, and
realtime agents (the EL live cook-along / conversational agent) cost the most** — see §3/§4 for how the
hierarchy is implemented without inventing three markup rates.

**Confirmed by Jordan (2026-08-23):** "65% markup" = **retail is cost × 1.65** (~39% gross margin on
credit spend), and the included/granted dollars are denominated at **Jordan's COST** ($1 of cost =
$1.65 of retail credit). See §4 for the one-currency mechanics.

**The welcome grant (decided 2026-08-23): every new HOUSEHOLD gets $1.00 of AI at cost ($1.65 retail
credit), one-time, at signup — Free tier included.** Jordan's rationale, recorded because it IS the
conversion design: ~2 months of typical usage means the habit forms before the paywall — the
renewal-moment question becomes "I'm already familiar, should I keep it?" instead of "I barely
understand it." Free's AI-off posture therefore begins when the welcome dollar runs out; **exhaustion
is the upsell moment**. Mechanically it's just an initial ledger grant, and a later subscription's
monthly grants simply stack on the same balance. ⚠️ Per HOUSEHOLD, not per account: joining an
existing household via invite code must not mint another dollar; the one choke point is
`HouseholdService.CreateForAsync` (all four creation paths — Register, ExternalLogin, ChooseHousehold,
DevAuth — flow through it; joins don't), pinned by a test (§9 phase 4).

Three review findings harden the grant (2026-08-23, accepted):
- ⚠️ **DECIDED (Jordan, 2026-08-23): realtime agents draw from PURCHASED CREDITS ONLY — for everyone.
  No grant money (welcome or monthly) ever funds a live session.** This closes the review finding
  outright: one cook-along session costs more than the whole welcome dollar, so a voice-first new
  user — the most impressed kind — would have burned the entire trial in one session and hit the
  paywall having formed zero habit. It also softens the runaway-session exposure: the session cap
  derives from money someone deliberately paid (§4). Plain read-aloud TTS stays grant-spendable
  (cheap, cache makes repeats free — a subscriber's "read me this" must not hit a credits wall). The
  grant funds the habit loop it was sized for (scans + chat). Honest corollary: an engaged family at
  ~5 text actions/day exhausts it in ~3 weeks, not 2 months.
- **Anti-farming controls are LAUNCH DEFAULTS on any open-registration deployment, not contingencies**
  (the per-IP /Account limit exists and covers registration, but IP rotation is cheap, and
  `RequireConfirmedAccount` is currently false): flip `RequireConfirmedAccount` ON so the email is verified
  at registration (the verify-once principle below), the per-household daily caps ON, and — the piece nothing
  provided — **a global managed-spend ceiling with an alert**, because without detection the operator learns
  about a grant farm from the Anthropic invoice.
  Optional: drip the grant (~25¢/day unlocked). Closed-registration boxes (family) need none of it.
- **Verify each email ONCE, at first capture — then rely on it everywhere, no re-verification** (Jordan,
  2026-09-01). Registration is the main capture point: with `RequireConfirmedAccount` ON the login email is
  confirmed before the account is usable, and that single verification then stands for every later use of it
  — the welcome grant, subscription + credit-pack purchase (§6), receipts, the MoR customer + portal,
  dunning — none of which re-confirm. The one path that can introduce a fresh, never-verified email is the
  anonymous **wishlist / pre-order** capture on `/about` (a visitor with no account): verify THAT once, in
  place (double opt-in), before it counts as a reservation or joins the launch list — the double-opt-in that
  item 62 shipped without. A visitor who has or later creates an account uses their already-verified account
  email and is never asked again.
- **Exhaustion is a RAMP, not a wall:** ambient remaining-grant meter, nudges at ~50%/~90% with the
  subscription offered BEFORE the wall, and the wall itself holds work rather than refusing it —
  "your receipt is saved; it'll extract when you subscribe" — because the wall fires mid-chore,
  receipt in hand, the moment most likely to read as breakage (expect /bugs reports otherwise).

**The one-sentence strategy this encodes:** the subscription *is* the business ($2.99 covers a typical
household's whole month — §3); credits exist so a heavy user can never cost more than they paid, not as a
revenue stream. Self-host + BYOK stays free forever (the source-available posture) — the paid product is
*hosted convenience on the host's keys*: **"if it's my box, it's my keys, and you need to pay me"**
(Jordan, the deployment rule in one sentence — §2). His stated ethos, which several §8 recommendations
follow from: **"trying to be generous and just make enough money to make it worth it."**

## 2. What "no AI" means — and why Free is nearly free to build

The app was built keyless-first (BYOK arc, item 8): every AI surface already has a no-key state, the
prediction engine is pure C# (no AI), and cached TTS lets sample recipes talk without a key. **Free tier ≈
the existing keyless posture with upsell copy instead of "add a key."** Concretely, Free keeps: the
predictor + dashboard + grocery list, manual product/purchase entry, reports, history/undo, the cookbook
(manual entry), receipts *review* of anything already extracted. Free loses (managed): receipt extraction,
chat/voice, census, recipe AI (suggest/adapt/import/tags), self-eval, cook-along — once the one-time
welcome grant (§1) is spent.

- **BYOK — final rule (Jordan, 2026-08-23, refined after the integration finding): BYOK belongs to
  boxes the USER runs; the paid box is the host's keys, full stop.** In Jordan's words: *"self-hosted
  BYOK should always work — if it's my box, it's my keys, and you need to pay me."* Deployment mode
  (the existing `KeyMode`) already IS the rule: BYOK-mode boxes (self-host, the demo droplet) take
  browser keys exactly as today, free forever — that's where "openly usable by people with their own
  keys" is delivered, via the source-available posture. The MANAGED paid box takes no browser keys:
  `CircuitAiSettings.Apply`'s Managed no-op — the guard against a devtools-injected key — **stands
  untouched, and the per-tier relaxation the earlier "BYOK on Free: yes" would have required is
  DELETED from the plan**. The review had flagged that relaxation as a security-reviewed work item;
  resolving a finding by not building the thing beats building it carefully. Free on the paid box is
  therefore: the full manual app + the welcome grant, then subscribe.
- The demo droplet (BYOK) and self-host are **untouched** — billing is config-gated off (§7).

## 3. Unit economics (directional — calibrate before launch)

Model pricing (both pinned modules run `claude-haiku-4-5`): **$1.00/MTok input, $5.00/MTok output**
(verified 2026-08-23). Image input ≈ (w×h)/750 tokens; receipts resize to 1568px max edge
(`LlmOptions.MaxImageEdgePx`), so ~1.2–3.3k tokens per photo.

**Per-action cost estimates** (input includes system prompt + product catalog + tools; treat as ranges):

| Action | Rough tokens (in / out) | Cost |
|---|---|---|
| Receipt page extraction | 4–8k / 0.5–1.5k | ~$0.005–0.015 |
| Chat/voice turn (incl. tool rounds) | 4–10k × 1–3 calls / small | ~$0.01–0.04 |
| Census photo | 4–8k / 0.5–1.5k | ~$0.01 |
| Recipe suggest / adapt / import | 3–8k / 1–3k | ~$0.01–0.03 |

So **$1.00 of cost ≈ 50–150 AI actions/month ≈ 2–5/day** — comfortably covers a normal household
(a few receipts a week + regular chat). Credits are genuinely for outliers, so the margin holds for the
typical subscriber. ⚠️ **Calibrate against real data before freezing numbers:** the family box's `AiUsage`
rows record every household's actual daily calls + tokens — multiply by the rates above and check what a
real month costs. That table was built to answer exactly this question.

**Payment-fee reality on small prices** (LS verified 2026-08-23: subscriptions **5.5% + 50¢** — the
base 5% + 50¢ plus a +0.5% subscription surcharge; one-time products 5% + 50¢; +1.5% international
cards, +1.5% PayPal — worst realistic case 8.5% + 50¢ still leaves every floor positive):

| Transaction | Fee | Net | AI cost if fully used | Margin |
|---|---|---|---|---|
| **$2.99/mo sub (chosen)** | ~$0.66 | ~$2.33 | $1.00 | **~$1.33/mo (44%)**; typical use is under $1, so usually better |
| **$27.99/yr annual (chosen)** | ~$2.04 | ~$25.95 | $12.00 | ~$13.95/yr ≈ **$1.16/mo** — the fixed fee paid once instead of twelve times, cash up front, annual retention (§1 ⚠️) |
| $1.99/mo (rejected — see §1) | ~$0.61 | ~$1.38 | $1.00 | ~$0.38/mo — untenable; MoR fees strengthen the rejection |
| $5 / $10 / $20 credit packs | $0.75 / $1.00 / $1.50 | $4.25 / $9.00 / $18.50 | $3.03 / $6.06 / $12.12 | **$1.22 (24%) / $2.94 (29%) / $6.38 (32%)** — the 1.65× is a 39% margin only PRE-fee; net-of-fee it's 24–32%, which argues for steering buyers to $10/$20 |

Consequences: **minimum credit pack ~$5** (a $1 pack would lose over half to the 50¢ fixed fee), and
annual is the lead offer, not a footnote. **Prices are tax-EXCLUSIVE** (stated assumption — under
tax-inclusive EU-style pricing, 20% VAT inside the sticker would cut the monthly floor to ~$0.87);
with that pinned, tax is the merchant of record's job end to end (§6) and the margins above need no
tax asterisk. **Disputes are worse than "months":** on a fully-used annual, one dispute ≈ −$26 clawed
− $15 fee − up to $12 of AI already served ≈ **−$53 — several YEARS of another annual subscriber's
floor** (and the $15 fee alone exceeds one annual's entire floor margin). Refund/clawback design is
therefore mandatory, not optional — §4. **Fixed-cost break-even, quantified:** the EL plan minimum
(~$22/mo) alone consumes ~17 monthly (or ~19 annual) subscribers' floors; with droplet-class hosting,
**~20–25 paying households before the first dollar of profit** at full-grant usage — the number
"make enough to make it worth it" turns on.

**The cost hierarchy (Jordan's call): text/receipts < voice < realtime agents.** This is mostly just
real costs showing through one markup rate — Haiku tokens are fractions of a cent per action; ElevenLabs
TTS is per-character and STT per-minute (a full recipe read-aloud plausibly costs more than a week of
receipt scans, though `CachingTextToSpeech` makes every repeat free); an EL realtime agent session is
per-minute at conversational-AI rates, easily 10–100× a chat turn. Verified 2026-08-23: EL agents ≈
$0.08–0.10/min on paid plans — **but burst pricing runs ~$0.16/min, and the agent's LLM + any
telephony are billed separately ON TOP**, so the flat prices must be set from real invoices, not the
headline rate. **The gap: none of the EL costs are
recorded today** — only mint counts, no dollar figure anywhere. So voice/agent actions can't stamp exact
cost the way token calls can (§4); they get **flat credit prices per action** (per read-aloud synthesis,
per realtime session or minute), set by measuring real EL invoices against usage and applying the same
1.65×. **Instrument before freezing those flat prices** — and cache hits should stay free (they cost
nothing; charging for them reads as unfair — §8 open #1).

## 4. Metering: from daily caps to dollar accounting

Today `AiUsageMeter` enforces one global daily call/token cap on managed circuits
(`MeteredChatClient` → `EnsureLlmCallAllowedAsync`). Subscriptions change the *unit* (dollars, not
calls), the *period* (billing month, not day), and the *limit source* (the household's tier + balance,
not global config). The pieces:

- **Cost is stamped at call time, never re-derived.** A pricing catalog (model → in/out $/MTok) converts
  each call's tokens to cost *when recorded* (`AiUsage` today records tokens but not model or cost).
  Historical rows must keep the price they were charged at when the catalog later changes — the
  "one prediction, one story" rule applied across time, same as item 49's stamp-what-you-saw lesson.
  ⚠️ **The column is `CostMicros` (integer), not TEXT-decimal** (review finding): the usage row is a
  per-(household, day) aggregate maintained by a race-safe SQL-side increment (`ExecuteUpdateAsync`
  with `u.X + x`), and a TEXT-decimal cannot ride that increment — EF's SQLite provider doesn't
  translate decimal arithmetic, and raw SQLite coerces TEXT to REAL. Only integer micros works.
- ⚠️ **The auth-side LEDGER is THE money record; the pantry `AiUsage` row is display-only.** They live
  in two different SQLite files with no shared transaction, and the usage write is *deliberately*
  best-effort (`MeteredChatClient` logs-and-continues on a failed record). One of them must be the
  authority — the top-of-CLAUDE.md one-definition rule — and it's the ledger: gate on it, bill from
  it, export it; the usage row feeds the Settings panel and nothing else.
- **One currency: retail-denominated credit.** Every call decrements at retail (cost × 1.65); the AI
  tier's monthly grant is $1.65 retail (= $1.00 cost). One ledger, one consumption rate — no
  "included is at cost but credits are marked up" dual bookkeeping.
- **Two pricing shapes, one currency.** Token actions (chat, extraction, census, recipes) stamp *exact*
  cost from the pricing catalog. Voice/agent actions (EL TTS synthesis, STT, realtime sessions) post
  **flat retail prices** per action — their provider costs are per-character/per-minute and not
  observable per-call from inside the app, so a measured flat price (§3) is the honest unit. Both land
  as ordinary ledger consumption entries, which also makes the ledger the *first* place EL spend is
  recorded at all (today `AiUsage` counts only mints). The user-visible hierarchy — text cheapest,
  voice more, agents most — falls out of the prices, not from per-category markup rates.
- **A credit ledger, not a mutable balance** (auth-side, beside the subscription): grant / purchase /
  consumption / expiry / **refund-reversal** entries. Auditable, webhook-friendly, and safe against
  the read-modify-write races the invite-code work already taught (item 12 — conditional updates).
  ⚠️ Review corrections to "balance = sum, no special case": (a) **no-rollover requires BUCKET
  accounting** — the period-end expiry entry must equal the *grant bucket's* unspent remainder, so
  consumption entries attribute grant-vs-purchased money (spend grant first); a plain sum can't
  express "the grant expires, purchases don't". (b) Order ledger reads by `Id`, never by timestamp
  (SQLite refuses `DateTimeOffset` in ORDER BY — item 47), and bucket periods by the PROVIDER's UTC
  billing period, not server-local `DateTime.Today` (the TZ gotcha). (c) auth.db has no query filter —
  every ledger query hand-scopes its WHERE to the household, the `ApiTokenService` pattern. (d) ⚠️
  **the balance must NOT inherit phase 1's per-circuit cache** (flagged by both gate reviewers): phase
  1's `IEntitlements` caches the boolean tier for the CIRCUIT's lifetime (safe — a tier rarely
  changes), but a Blazor circuit can be open for hours, so caching a *balance* that decrements every
  call would let one long session overspend. Extend `IEntitlements` for the tier read; read the
  balance FRESH on each gate check (or with a short TTL), never once-per-scope.
- **Two accounting edges phase 2's gate flagged, characterised honestly** (not deferred as "harmless
  because unenforced"): (a) the AiUsage (pantry) write and the ledger (auth) write span two SQLite
  files, so they can't share a transaction. The *cascade* — a pantry hiccup silently skipping the
  money write — was a real bug and is **FIXED**: the two are now independent best-effort writes
  (`MeteredChatClient.RecordAsync`), each logged on its own, pinned by
  `A_usage_write_failure_still_records_the_credit_consumption`. The residual — a write failing *after*
  its sibling landed — is inherent to two databases (bounded to one call, logged distinctly); when
  enforcement lands, decide whether the ledger becomes the single authoritative write or a reconciler
  backfills from AiUsage. (b) The orphaned-welcome-grant on a concurrent double-create was NOT a bug
  this branch introduced — `ChooseHousehold`/`Register` carried a pre-existing double-create race that
  orphaned a *household* regardless of the grant (the grant just rode the orphan, yielding dead,
  unspendable money — no leak, no spendable double-grant). It is now **FIXED at the root**:
  `HouseholdService.CreateForAsync` claims the user's household slot with a CONDITIONAL update
  (`HouseholdId == null`) — the same one-statement mechanism `JoinAsync` already uses for invite uses —
  so two concurrent creates can no longer both win, and the loser creates (and grants) nothing. Pinned
  by `Two_creates_for_one_user_make_one_household_never_an_orphan` (one household, one welcome grant),
  mutation-checked. ChooseHousehold's sequential guard stays as the first, cheaper line of defence.
- **Refunds/clawbacks are designed in, not hoped away** (both external reviews, independently): the
  MoR can refund unilaterally within ~60 days to pre-empt chargebacks, so a refund webhook posts
  reversal entries; **balances may go negative** — a negative balance gates usage and nets against
  future purchases (else: buy $20 pack → burn $12 of real API cost → refund → the operator pays
  twice, repeatably). Published refund policy: **unused credits only**.
- **The gate stays post-hoc, prepaid is the hard stop — for TOKEN calls.** Check balance before the
  call, record after — so overshoot is bounded at one call *per in-flight request* (concurrent calls
  each pass on the same balance; still cents). ⚠️ **Realtime voice breaks this bound and needs its own
  mechanism** (both external reviews, independently — the doc's sharpest gap): a session gated only at
  OPEN can run an hour past an empty balance at per-minute rates. Required design: **per-minute
  server-side decrement with a balance-derived session cap announced at session start** ("about 12
  minutes of cook-along left"), never a mid-recipe surprise kill. The natural debit seams: the signed-
  url mint + a duration accounting hook for realtime; the `CachingTextToSpeech` decorator for TTS —
  which also gives "cache hits are free" by construction. **Realtime debits the PURCHASED-credit
  bucket only** (§1 — grant money never funds a session, so the cap math reads one bucket). Until the
  flat prices exist, **managed voice for paid tiers stays OFF or hard-capped by the existing mint
  quota** (§9 phase-3 precondition) — a paying subscriber must not be able to run unmetered EL spend
  bounded by nothing.
- **Grant resets on the billing period** (the provider's period; calendar month acceptable v1), no
  rollover. The annual drip needs a TRIGGER the app doesn't have (annual billing = one webhook per
  YEAR, and no per-household scheduler exists): **lazy grant-on-first-check per period** — when the
  entitlement is consulted and the current period has no grant entry yet, post it. No background
  machinery. **Purchased credits never expire — DECIDED, for compliance** (§8).
- **Keep the existing daily caps as an optional abuse valve** on top (config, unset = off) — a
  compromised account shouldn't be able to burn a whole credit balance in an hour.
- **BYOK circuits: unchanged** — recorded, never charged, never limited (their wallet).
- **Free tier enforcement is a posture, not a gate — and the predicate is tier-AND-balance, async.**
  A Free household with welcome-grant (or leftover) balance must NOT behave keyless — §1 says AI works
  until the dollar runs out and §6 says purchased credits survive tier drops — so the posture consults
  "tier grants AI, OR balance > 0", which is an async entitlement read, cached per circuit with a
  staleness bound (~5 min, like the cookie revalidation). ⚠️ **"Managed" is decided in THREE places
  today, not one** (review finding): the `CircuitAiSettings` constructor (synchronous, config-only —
  a tier consult cannot live there), the cook-along endpoint's `IsManaged` read, and
  `CircuitVoiceCredentials` — plus `HasKey` is TRUE under managed (it holds the server key), so the
  "existing no-key states" don't fire on a managed box without new wiring. One shared entitlement
  service feeds all three, or a Free household still mints realtime sessions on the host's EL key.
- **New auth-side state walks the house schema + data-rights drill:** ledger + tier columns via
  `AdditiveSchema` (EnsureTable + the drop-column parity test — the ALTER path is what live boxes
  take); **export includes the ledger** (a household's grants/purchases are their data); **delete-my-
  data does NOT touch balances** — destroying purchased credits is destroying money, the `AiUsage`-
  survives asymmetry (item 33) with a stronger reason; and there is no query filter in auth.db, so
  every query hand-scopes (above).

## 5. Founder tier (from the parked 2026-08-23 design)

Tier #1 of this system; needs zero payment code. `Household.Tier` (auth.db — deliberately: no pantry
query filter to punch through, un-wipeable by "delete my data", un-self-grantable — enforced by there
being NO write path outside the admin service, since auth.db has no `EnforceHousehold` layer) +
`FounderSince`. For founders the meter SKIPS the limit gate while still recording — the gate runs
before the provider call, recording after, and only the gate is bypassed (unlimited-but-recorded —
the exact posture BYOK already has, pointed at the host's wallet as a gift). ⚠️ Phase-1 plumbing the
sketch hides (review finding): `AiUsageMeter` today knows neither the household id nor auth.db — the
tier check needs `ICurrentHousehold` + an `AuthDbContext` read (or a tier claim in the cookie, with
the 5-minute security-stamp revalidation as the grant/revoke propagation bound). Granted from a Households section on
`/admin` via the one `AdminOptions.IsAdmin` predicate — would be the second admin cross-household write
after `ReportResolutionService`, same review posture.

**The Households section is a full ROSTER (Jordan, 2026-08-23): every household · its members (emails)
· tier · FounderSince — the operator's view of who is on the box and what they're entitled to** — with
the Founder toggle on each row. Ships in phase 1 (the toggle needs the list anyway). The read is an
admin-gated reader service over auth.db (ordinary reads — auth.db has no query filter to punch
through; the gate is the service's own `RequireAdmin`, the `AdminReportReader` posture). Columns grow
with the phases: subscription state + period once billing exists (phase 3), credit balance once the
ledger exists (phase 2) — and if a per-household USAGE column is ever wanted, that read crosses into
the pantry db and follows the `AdminReportReader` `IgnoreQueryFilters` precedent, made explicitly,
not casually. Precedent for showing operator-side member info: /admin already lists bug reporters
with household names and the online-presence roster.

Optional Settings badge: "You're a Founder —
unlimited usage, thank you 💛". **Grandfathering decided (2026-08-23): every current family-box
household is a Founder** — Jordan grants them from /admin when phase 1 ships; no pre-work needed.

## 6. Payments: merchant of record (decided 2026-08-23)

**Jordan's call: the processor must handle tax end to end** — which means a **merchant-of-record**
(MoR) provider, not plain Stripe. (Stripe Tax *calculates* tax; registration, remittance, and filing
across US states + EU VAT remain the merchant's problem — exactly the work being bought away. An MoR
is legally the seller, so all of it is theirs.) The ~25¢/transaction premium over raw Stripe is the
price of never thinking about VAT, and the annual-first posture pays the fixed fee once a year.

**PHASE-3 DECISION (2026-08-24): Stripe Managed Payments (SMP) CHOSEN**, superseding LS-"for now"
(Jordan delegated: "I don't care which one, I just want it cheap and stable — recommend"). SMP is now
GA (the preview in the table below shipped): all-in ≈ 2.9% + $0.30 base card + 3.5% MoR surcharge +
~0.5–0.8% Billing on recurring ≈ **~7% + $0.30 per US charge**, $15/dispute (verify on
stripe.com/pricing at wire-up — the third-party fee blogs run pessimistic; the components check out on
Stripe's own pages). Highest *headline* rate of the MoRs, but the LOWEST *fixed* fee ($0.30 vs
everyone else's $0.50) — and on these sub-$28 tickets the fixed fee dominates, so SMP lands within
pennies and is actually CHEAPER than a 5% MoR on the $2.99 monthly (~$0.51 vs ~$0.66; roughly a wash
on annual). Chosen for **stability** over the ~1%: Stripe just consolidated the MoR space by absorbing
Lemon Squeezy into SMP, so the genuinely-cheaper options are startups (Creem/Dodo/Polar) carrying
re-integration risk a portfolio piece shouldn't take, and LS is now a transition path INTO SMP, not a
durable standalone. **No recurring/setup/minimum fee** — the integration is built and tested in test
mode for free; nothing is charged until a real customer pays. The provider seam below keeps the choice
swappable if the fee ever stings at scale.

| Candidate | Fee (verified 2026-08-23) | Disputes | Notes |
|---|---|---|---|
| **Lemon Squeezy** — **CHOSEN ("for now")** | 5% + 50¢; **5.5% + 50¢ on subscriptions**; +1.5% intl / PayPal | ~$15 passed through | Supports the exact product shape (subs + one-time packs, documented API-credit pattern). ⚠️ **In an announced transition**: Stripe launched its own MoR (**Stripe Managed Payments**, public preview 2/2026) and LS is building migration paths onto it — SMP preview pricing (~6.4% + 30¢ ≈ $0.49 on $2.99) would be CHEAPER than LS on these tickets. Decision stands as "for now" — and Jordan is positively disposed to Stripe ("I like Stripe"): **at phase 3, check Stripe Managed Payments FIRST; if it's available at MoR parity, prefer it**. Apply for whichever store EARLY (activation review is the long pole); the seam below is the insurance |
| Polar | 5% + 50¢ (Starter; the old 4% + 40¢ died 5/2026) + payout fees ($2/payout-month + 0.25% + 25¢ + FX) | $15 passed through | Pro ($20/mo, 3.8% + 40¢) only beats free LS above ~$800/mo revenue |
| Paddle | 5% + 50¢ advertised — but **sub-$10 products need custom-pricing contact**, and every price in this plan is under $10 | **Bundled in the fee** | The dispute bundling matters at floors this thin ($15 = more than one annual's entire floor margin) |

**Payout reality (LS):** twice-monthly with a ~2–4-week lag, **$50 minimum payout**, intl bank 1% /
PayPal fees — so "annual = cash up front" means net-30-ish, after ~2 annual subs have accrued.

Mechanics are provider-agnostic (all three offer them):

- **Objects:** the sub ($2.99/mo, $27.99/yr) + credit packs ($5/$10/$20, **offered to active
  subscribers only** — §8) as hosted-checkout products; customer portal for cancel/card management.
- **The subscription attaches to the HOUSEHOLD** (the tenancy unit — AI allowance is shared like the
  pantry is): provider customer id + `Tier` + period state on `Household`. Who may purchase:
  **any member — decided** (§8); the purchaser-departure lifecycle below is the safety net.
- **Webhooks** (checkout completed, subscription renewed/updated/cancelled, **refunds** — §4) update
  tier + write ledger entries. ⚠️ A webhook receiver is a new **unauthenticated public endpoint**
  (signature-verified via the provider's signing secret, idempotent by event id) — a named item for
  the `/pre-push` security review, like the census/photo endpoints were.
- **Webhook endpoint mechanics** (from item 54's scars, pre-answered): a raw-body `MapPost` (HMAC
  over the exact bytes) declares no form acceptance, so it needs NO `/graphql`-style antiforgery
  `UseWhen` exemption — but **every non-2xx response must carry a small JSON body**, or
  `UseStatusCodePagesWithReExecute` re-executes it into `POST /not-found` → antiforgery → a
  misleading 400, exactly as the GraphQL 401/429 fixes established. And the strict CSP
  (`form-action 'self'`, no third-party scripts) rules out any JS-overlay or form-post checkout —
  **hosted-redirect checkout links only**.
- **Lifecycle:** failed payment → provider dunning → on final failure tier drops to Free at period
  end (data untouched — Free is a posture, nothing is deleted). Cancel → runs out the paid period.
  Unused purchased credits survive tier drops (they were bought).
- ⚠️ **Checkout runs on the account's already-verified email — it does NOT re-verify.** The MoR keys the
  customer record, receipts, the cancel/card portal, and the refund/dispute contact on ONE email, and it's
  the member's app-account email — already format-valid + unique (Identity's `[EmailAddress]` +
  `RequireUniqueEmail`) AND already verified once at registration (§1's verify-once principle,
  `RequireConfirmedAccount` ON). So checkout simply requires that verified address and adds no second
  confirmation step; because it was verified, a typo'd or unowned address can't end up owning the
  household's billing (compounding the purchaser-departure finding below) or silently swallowing receipts
  and dispute notices. We never collect a SEPARATE billing email: the strict CSP is hosted-redirect-only
  (no form-post checkout, above), so the verified account email is what we pass to the provider — one
  address, nothing to keep in sync.
- ⚠️ **The purchaser can leave the household — item 54's MED-HIGH shape, one level up** (review
  finding): member removal revokes the cookie and API tokens, but the LS customer account + portal
  belong to the purchasing member's EMAIL, unreachable by `RemoveMemberAsync` — a removed member
  keeps cancel/card control over the household's subscription (entitlement DoS), or keeps being
  charged for a pantry they can no longer enter. Design: on removal of the purchasing member, the
  household can re-attach billing (a new checkout supersedes; the old sub is cancelled via provider
  API or a documented manual step), and the removed member's residual portal control is NAMED — they
  can cancel (acceptable: it's their card) but cancellation only ever downgrades at period end.
- Keep the provider integration behind one thin seam (checkout-link creation + webhook parsing) so
  the MoR choice stays swappable — but one provider, one adapter; no speculative abstraction.
- Keys live in config/user-secrets like every other secret; **no payments section configured =
  billing does not exist** (§7).

## 7. Deployment posture

Config-gated, the `Admin`/`Email`/`GraphQL:Enabled` pattern: **unset = the feature does not exist** —
no tier checks, no upsell copy, no endpoints, today's behavior exactly. Self-host stays unlimited by
default. Per box (updated per Jordan, 2026-08-23):
- **Droplet demo** — BYOK, billing off. Unchanged **today**. ⚠️ **Candidate change (Jordan, 2026-09-01,
  a Phase-4 item — recorded, not built): a small managed TRIAL on the demo.** The pure-BYOK demo hides the
  AI — nobody pastes an API key to try a demo — so grant a **verified** demo account ~**$0.50** of managed
  AI (Jordan's cost, on the host's key), **no agent rights**, so casual visitors actually see receipts/chat/
  recipes work. This makes the demo **managed, not BYOK** (the host's key on the public box — reversing "the
  demo ships no usable keys; keys never used live", item 8; Jordan accepts this). Preconditions, all real:
  (1) **balance ENFORCEMENT must exist** (phase 4) — without it a grant caps nothing, so a visitor or bot
  runs unlimited on the host's key; (2) a **GLOBAL managed-spend ceiling + alert** is the primary guard, not
  the per-account 50¢ — an open public demo means unbounded accounts (email/IP rotation is cheap), so the
  safe shape is a hard "$X/month total for the demo, then AI pauses"; (3) **email verification** (the §1
  verify-once / ops-gate item). "No agents" already matches §1 (realtime agents are purchased-credits-only
  for everyone — grant money never funds a session; cheap read-aloud TTS may stay). Build it in Phase 4 with
  the global demo budget as the wallet guard.
- **Family box** — **billing OFF, permanently.** Every household there is a Founder (admin-granted,
  phase 1), and Founders don't pay — so the family box never needs a payment surface, a webhook, or
  open registration hardening. It gets phase 1 (tiers + Founder + badge) and nothing else.
- **The pay-to-play box** — a **separate, fresh droplet-class public deployment, stood up when the
  first paying customer signs up** (Jordan's call). Billing on, open registration with §1's
  anti-farming launch defaults, no Cloudflare Access wall (it's a public product). This is where
  phases 3–4 actually deploy.

⚠️ Conditional warning, kept because it was found the hard way: **if billing ever sits behind
Cloudflare Access** (don't), the MoR webhook POST dies at the Access wall (302 + email-OTP a machine
can never pass), invisibly except in the Access log — it would need a service-auth bypass scoped to
the webhook path, verified by the newest Access log row per `docs/family-cloudflare.md`'s
judge-by-the-log rule. On the planned separate public box, this doesn't arise.

## 8. Decisions & open questions

**Decided (2026-08-23, all Jordan's calls):**
- **Markup = 1.65× cost** and **grants are denominated at Jordan's cost** ($1 cost = $1.65 retail
  credit) — confirmed (§1).
- **Welcome grant**: $1 at cost per new HOUSEHOLD, replacing the earlier 5-free-scans idea — the
  habit-formation trial (§1).
- **BYOK: by deployment mode, not by tier** (§2 — initially "BYOK on Free: yes", refined the same day
  after the integration finding): BYOK-mode boxes always take browser keys; the managed paid box never
  does. The guard-relaxation work item this deletes never gets built.
- **Annual $27.99/yr** ≈ $2.33/mo effective (§1).
- **Merchant of record: Lemon Squeezy** — "for now"; the §6 seam keeps it swappable. Verify current
  fees + payout terms at signup before locking the §3 tables.
- **Grandfathering: yes** — every current family-box household becomes a Founder, granted by Jordan
  from /admin when phase 1 ships (§5).
- **Tier names — set A** (§1): **Shelf** / **Aware** / Founder. (Sous Chef was dropped 2026-09-02 — voice folds into Aware, the Live agent is hidden; see §1.)
- **Credit packs: $5 / $10 / $20, flat rate** — $5 floor because the 50¢ fixed fee makes smaller
  packs wasteful; three options max; deliberately no bulk-bonus games — one honest price per credit
  matches the §1 ethos.

**Decided by the review gate (2026-08-23):**
- **Purchased credits NEVER expire — for compliance, not just goodwill** (closes old open #3): the
  balance is stored value; unclaimed-property obligations sit with the ISSUER of the obligation (the
  app), not the MoR — never-expire is the legal safe harbor (CARD Act floor; state escheat patchwork
  mostly exempts no-expiry instruments). Sell under written terms: non-transferable, not
  cash-redeemable, closed-loop (redeemable only for the app's own service — the classic
  money-transmitter exemption). An entity + one legal read before real scale.

**Decided after the review gate (Jordan, 2026-08-23):**
- **Realtime agents = purchased credits only, for everyone** (§1/§4) — no grant money ever funds a
  live session; read-aloud TTS stays grant-spendable.
- **Payments: check Stripe Managed Payments first at phase 3** ("I like Stripe"); Lemon Squeezy is
  the bridge if SMP isn't ready (§6).
- **The paid product deploys to its own fresh public box, stood up at the first paying customer**;
  the family box never gets billing — all its households are Founders (§7).
- **Checkout defaults to MONTHLY** (Jordan: "no one wants to fork out 30 for something they haven't
  tested much yet, and free is only a month or two") — the annual sits beside it with a
  "save 22% — ~2 months free" badge, and gets its real pitch at first renewal, when trust exists.
- **Credit packs are sold to ACTIVE SUBSCRIBERS ONLY — Free households are not offered packs**
  (Jordan: "it means they're skipping paying for hosting costs, essentially"). The economics behind
  it: the subscription carries the FIXED costs — hosting, the EL plan minimum, the infrastructure
  §3's ~20–25-household break-even is denominated in — while credits at 1.65× price only the
  *marginal* AI. An à-la-carte pack buyer would consume the infrastructure without contributing to
  it. The sub is the hosting fee; credits are fuel. (A Free household with a SURVIVING balance —
  bought while subscribed, kept through a tier drop — still spends it: §6's "they were bought" rule
  is untouched.)
- **Any member can purchase for their household** (Jordan: "if you have the card saved in your
  browser and you wanna pay, that's good for you — anyone can pay"), with §6's purchaser-departure
  lifecycle as the safety net.

**Still open:**
1. **Voice flat prices** — the *decision* is made (voice > text, agents most; credit-priced;
   **realtime agents = purchased credits only, decided** — §1); the *numbers* need EL invoices
   measured against real usage first — and until they exist, managed voice for paid tiers stays
   off/mint-capped (§4, phase-3 precondition). Standing: TTS read-aloud spendable from any balance;
   cache hits free.
**Parked (deliberately deferred, not forgotten — Jordan's call, 2026-08-23):** the lapsed-Free
"keep-warm" problem (a non-converter's data decays on manual-only Free, weakening any future
win-back). Left off for launch; to be decided "in the weeds" at build time. Two candidate shapes
recorded: the reviewer's one free scan/month (keeps data continuously warm, but softens "Free has no
AI" into a standing special case), or Jordan's own alternative — **"welcome back" credits**: a small
one-time grant when a lapsed household returns, which keeps Free cleanly AI-free and spends the money
only on someone who actually came back.

**Parked — the dormant-subscriber conscience nudge (Jordan's call, 2026-09-01):** proactively email an
ACTIVE, PAYING subscriber who has gone dormant (no app activity for ~a few months) to ask whether they
still want it, with a one-click unsubscribe. Deliberately revenue-*reducing* — the integrity move a
portfolio piece with real users should be seen making, and of a piece with this arc's other calls
(refund clawback not automated, billing infra deferred until real customers exist). Cheap to build: the
dormancy signal already exists (`AiUsage` per household/day, the `ActivityEntry` log, login recency), so
"subscribed AND no activity in N months" is a clean query. The one hard dependency is a **monitored
transactional email provider** — the same ops-launch-gate item (§9) that gates password reset and the
email-confirmation grant — so this lands **Phase 4+, right after that provider is in place**, never on
the Gmail-app-password family arrangement. Open sub-questions for build time: the exact dormancy window,
whether it's a plain "still want this?" or an outright pre-filled cancel, and frequency-capping so it
can't nag.

## 9. Build order (each phase gated by `/pre-push`, per the house rule)

1. **Entitlement seam + Founder** — `Household.Tier`/`FounderSince`, plan→limits indirection in the
   meter (⚠️ needs `ICurrentHousehold` + an auth.db read or a tier claim — §5), the `/admin`
   Households ROSTER (every household · members · tier · FounderSince — §5) with the Founder grant
   toggle, badge. No payments; immediately useful on the family box.
2. **Cost accounting** — pricing catalog, `CostMicros` stamped on usage rows, period rollup, credit
   ledger with **bucket accounting + refund-reversal entries + lazy per-period grant** (§4), Settings
   "this month" display in dollars. Still no payments; proves the math on real usage.
3. **Payments (MoR)** — hosted-redirect checkout (sub + packs), webhooks (raw-body + JSON-bodied
   errors — §6), portal, tier lifecycle, dunning→Free, purchaser-departure handling, on Lemon
   Squeezy or Stripe Managed Payments — **check SMP first at phase-3 time** (§6); apply for the
   chosen store early. ⚠️ **Precondition:** voice flat prices exist OR managed voice for paid tiers
   is off/mint-capped (§4). Deploys to the separate pay-to-play box, not the family box (§7).
4. **Free-tier UX + launch** — the tier-AND-balance entitlement posture wired through all three
   managed-decision sites (§4; no BYOK exception — the managed guard stands, §2), upsell
   ramp + exhaustion UX (§1), the welcome grant **inside `HouseholdService.CreateForAsync`** (the one
   choke point all four creation paths share — "on registration" would silently skip OAuth signups;
   pinned by a test), pricing page (price-not-allowance promise wording — §1; monthly-default
   checkout with the annual's savings badge — §8), grandfather pass.
   **Launch gate:** ToS + refund policy + privacy update + entity decision + account deletion with
   credit-balance disposition — none exists today, all required before a public deployment takes
   money.
   **Ops launch gate (added 2026-08-25 — the readiness pass; all deliberately deferred until
   promised customers exist, Jordan's call: "I can't justify a real email provider or a real
   deployment with backups until I have promised customers"):**
   - **Transactional email provider** (Postmark/SES/Resend-class) + SPF/DKIM on shelfaware.net for
     the pay-to-play box. The Gmail app-password SMTP is a family-box arrangement: daily send
     limits, deliverability, personal-account coupling — and §1's email-confirmation-before-grant
     makes every registration depend on a delivered email, so this gates the anti-farming defaults
     too. Password reset is the only self-serve recovery; it must not be flaky for people who pay.
   - **Automated offsite backups on the pay-to-play box** — cron + `sqlite3 .backup`/`VACUUM INTO`
     to object storage, in the deploy kit from day one. The process was dry-run on the family box
     2026-08-25: `deploy/backup-family.ps1` + `deploy/sqlite-snapshot/` (nightly live-DB snapshots
     with integrity checks + a rolling blob mirror) — see CLAUDE.md item 57. The droplet version is
     the same shape on cron.
   - **Uptime + error alerting.** The ErrorLog is pull-only (someone must visit /admin) and no
     health endpoint exists; a paid box that dies at 2am must page somebody. Minimum: a free
     external uptime ping against the sign-in page; better: mail the admin on a new error
     fingerprint (the `IAccountMailer` seam exists). §1's spend-ceiling alert covers cost, not
     availability.
   - **Pre-auth support contact** — a support email on the sign-in page/footer and named in the
     ToS. Bug reporting requires sign-in; a customer who can't sign in, or has a billing dispute,
     currently has no way to reach the operator (and MoR onboarding asks for one anyway).

   Settled by the same pass: the iPhone photo path is verified on a real device (2026-08-25 — the
   ⚠️ from CLAUDE.md items 39/48 is closed), and the self-removal message that named the
   not-yet-built "Delete my account" is fixed (`fix/self-removal-copy`) — when account deletion
   ships under this gate, that message's sole-member branch is the place to point at it.

## 10. Demo box: free receipts for demos (2026-09-03, Jordan's calls)

> ⚠️ **SUPERSEDED (2026-09-05) — the demo box went MANAGED, not BYOK.** Jordan's call: make the demo box
> a normal **managed** box (`Llm:KeyMode=Managed` + a dedicated spend-capped key) so AI just works for a
> fresh household with no key of their own — the whole "receipt-only BYOK host-key fallback + 9-lifetime
> free scans + 21/day free-scan cap" design **below was NOT built**. What shipped instead: a **box-wide
> daily AI valve** (`DemoUsageMeter` / `Demo:DailyGlobalCallLimit` / `Demo:AlertThreshold`) that caps
> host-key LLM calls across all households and hands the polite "come back tomorrow", plus email-confirmed
> registration + an account-creation cap. See `deploy/env.example` ("Demo box" section),
> `docs/deploy-droplet.md` ("The demo box on YOUR key"), and `deploy/demo-box.env.example`. The numbers /
> rationale below are kept for history only.

**Goal:** a visitor to the demo box (demo.shelfaware.net) can try the flagship feature — receipt
scanning — **without their own API key**, so the demo actually demonstrates the AI. Today the demo box
is `KeyMode=Byok` with open registration: no host key, so a keyless visitor can only *review* the
seeded pending receipt, never scan a new one. This carves out a **receipt-only host-key fallback** on
that box, bounded hard so a public box on the host's key can't run up the bill.

**This deviates from §2's "demo droplet ships no usable keys" — deliberately, Jordan's call:** a demo
you can't try is a weak demo. Everything except receipt extraction stays BYOK (chat, census, recipe AI
still need the visitor's own key). The mechanism is built config-gated + general, so the same
free-receipt grant can later power the **paid box's Free tier** (§1/§2) — but it's turned on for the
demo box first.

**The numbers (Jordan, 2026-09-03):**
- **9 free receipts per household, LIFETIME** (not monthly) — ≈ 2 months of normal use, then it's gone
  and stays gone: the conversion pressure ("want the app by the time they aren't using free anymore").
  A monthly refresh would never create that pressure, so lifetime is the point.
- **21/day global free-scan cap** — the box-wide circuit breaker (the real wallet bound; per-household
  alone is unbounded under open registration). At ~$0.01–0.03/receipt that's ≈ $0.20–0.65/day worst case.
- **10/day global account-creation cap** ("for now") — bounds how many free households appear per day.
- **Alert threshold, default 5, configurable** — when the day's *global* free-scan count crosses it
  (well under the 21 hard cap), **alert the admin** (surface on `/admin` + log at Warning so the admin
  ErrorLog catches it) and **track free usage**. An early "the demo is being used / cost is accruing"
  signal, not the hard cap. **Confirmed daily-global** (Jordan 2026-09-03: "I need to know if I suddenly
  get users") — NOT per-household.
- **Spend-capped host key** on the box — Jordan OK'd exposing a key there, bounded; use a DEDICATED
  Anthropic key with its own workspace spend limit (belt-and-suspenders under the app-level caps).

**Abuse controls (layered — Jordan prioritized these):**
- **Email-confirmed registration is the foundation** ("most importantly"): a real, distinct, VERIFIED
  inbox to make an account. Distinct is already enforced (`RequireUniqueEmail`); this adds *verified*.
  ⚠️ **As built, this is an ACTIVATION flow, not the token→confirm-page flow first sketched here:** on a
  confirmation-required box, registration creates a PASSWORDLESS account and emails a *set-password* link
  (the ResetPassword page); setting the password both establishes the credential and confirms the address.
  It evolved this way during the build to close a pre-registration account-hijack (an attacker who set the
  password at registration could take over a victim who clicked the link) — the passwordless design means
  only the inbox-holder ever holds a credential. See the as-built record in CLAUDE.md. Reuses the
  `IAccountMailer`/SMTP seam from the forgot-password arc (item 45), via the **Gmail app-password route**
  like the family box.
  - ⚠️ **Config-gated, demo-box only** (a new flag e.g. `Auth:RequireEmailConfirmation`, default false).
    Enabling `RequireConfirmedAccount` globally would lock out existing accounts (registered
    pre-confirmation → `EmailConfirmed=false`); the flag turns it on for the demo box only. Family +
    self-host keep direct registration.
  - **The family box does NOT need this flag — Cloudflare Access already verifies email at the edge**
    (a one-time PIN to the allow-listed address before anyone reaches the box, item 44). So app-level
    confirmation is redundant there; only the *public* demo box (no edge gate) needs it. Existing
    family accounts can be backfilled `EmailConfirmed = true` (a one-time
    `UPDATE AspNetUsers SET EmailConfirmed = 1` on the family `auth.db`) — legitimate precisely because
    Cloudflare's OTP already proved they control those emails — which tidies the data and future-proofs
    if the flag is ever flipped there. Do the backfill BEFORE enabling the flag on any box with users.
- **Per-IP friction** on free scans + registration (blocks a trivial bot loop from eating the daily
  caps and griefing real visitors). No heavier gate than email-confirm + the caps.
- **The polite cap message** (either daily cap hit — no scans left, or no new accounts left): *"This
  demo box is usage-limited and has hit today's limit — please come back tomorrow."*

**Build order (each gated by `/pre-push`, deployed separately; the demo-box SMTP config is Jordan's step):**
1. **Email-confirmed registration + the 10/day account-creation cap.** The abuse foundation the rest
   leans on. Build + gate; deploy needs the Gmail app-password configured on the demo box (Jordan).
2. **The free-receipt grant** — receipt-only host-key fallback (9 lifetime/household), the 21/day
   global circuit breaker, per-IP friction, the admin alert + free-usage tracking, and the "come back
   tomorrow" message. Build + gate + deploy.

⚠️ Only the demo box (or a future managed Free tier) uses any of this; self-host + the family box are
untouched (BYOK / all-Founder). All caps + the grant + the alert threshold are operator config.
