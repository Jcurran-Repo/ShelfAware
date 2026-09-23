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
| AI | **Aware** | **$3.99/mo · $27.99/yr** | On, on the host's keys, **incl. conversational voice** (chat back-and-forth); includes **~$1.00/mo of AI at cost** | subscription (Lemon Squeezy — §6) |
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

**The base is $3.99/mo (raised from $2.99, Jordan, 2026-09-22).** The paragraphs below are the
2026-08-23 record of how the price got to $2.99 and are left as written — they are the reasoning of
that day, not a description of today's price. What carries forward unchanged is the *shape* of the
argument (the floor has to survive model-price rises, fixed costs and disputes) and the never-raise
promise, which binds an EXISTING subscriber's price and not the sticker a new one is quoted; nobody is
subscribed yet, so this raise breaks no promise to anyone. At $3.99 the monthly floor at full grant use
goes from ~$1.33/mo (44%) to **~$2.27/mo (57%)** — see the §3 table.
⚠️ **The cost side moved further than the price did, and it moved first.** Two of the three forces the
2026-08-23 paragraphs below argue from have since been answered rather than absorbed: ElevenLabs is out
of the paid tier (2026-09-19) and both voice and recognition now run in-process at $0 per use, which
removes a ~$22/mo line item that was eating ~17 subscribers' floors. ⚠️ **But NOT a ~$22/mo saving:
local synthesis needs a droplet that can run it, so the cost moved rather than vanished — fixed cost
goes ~$30 → ~$24/mo, and the RAISE did at least as much of the work as the saving did, not less**
(§3 decomposes it at a $24 box: EL alone ~23 → ~19 households, the raise alone ~23 → ~14, together
~11; at today's $18 box the two are a wash). ⚠️ **And §3's go-live box is an open question, not a
settled cost** — the ~$24 tier is not shown to run Kokoro at a usable speed. The one force that is
unchanged is model-price drift, which is the reason the floor still has to be defended.

**The annual stays at $27.99 (Jordan, 2026-09-23): the discount is meant to be big.** Against twelve
$3.99 charges that is ~41% off rather than the ~22% it was struck as, and that is the point — see the
annual paragraph below and §8.

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
$2.33/mo effective** (Jordan's number — MoR net ~$25.95, floor at full grant use **~$1.16/mo**).
⚠️ **The discount is a derived number and the 2026-09-22 raise moved it: $27.99 against twelve $2.99
charges was ~22% off; against twelve $3.99 charges it is ~41% off** — the annual now costs about seven
months of the monthly rather than nine and a half. **Jordan held $27.99 rather than re-strike it
(2026-09-23), and chose the bigger discount deliberately** — this paragraph's "steep discount,
annual-first posture" is now steeper than the 2026-08-23 version of itself meant, and that is the
decision, not an oversight. The re-strike that was weighed and declined was $36.99, which would have
restored the ~22% / ~2-months-free shape at the new base. What the app says about the discount is
derived from the two prices (`SubscriptionPricing`), so the badge and the "saves about N months" line
followed the raise on their own and no screen can quote the old number. A possible future voice tier (dropped for now — see above) sketched at **$4.99/mo /
~$47.99/yr ≈ $4.00/mo effective** (Jordan's "steep annual, ~$3.99 effective") — the two discounts
deliberately rhyme at ~20%.
**The discount was cheaper than it looked at $2.99 — and is not at $3.99.** At the old base a
perfect 12-month monthly subscriber netted ~$27.91/yr (twelve fixed fees) vs the annual's ~$25.95, so
the 22% sticker discount cost only ~$2/yr ≈ 7% net and break-even tenure was ~11.2 months: almost any
churn-risk subscriber made the annual the better outcome. ⚠️ **At $3.99 with the annual unmoved, twelve
monthly charges net ~$39.25/yr against the same ~$25.95, so the discount costs ~$13.30/yr ≈ 34% net and
break-even tenure falls to ~7.9 months.** That is still a real case for annual-first (cash up front,
retention, one fixed fee instead of twelve), but it is no longer nearly-free — it is a real ~$13/yr
bought per annual subscriber, and **that is the cost Jordan accepted on 2026-09-23** when he held the
annual at $27.99. Anyone who stays past month eight is cheaper served monthly; the bet is that most
will not, and that the cash and the retention are worth more than the spread. 100% annual take-up is the good scenario (max cash, max
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
shrinks what $3.99 buys — experienced as exactly the stealth raise the promise forbids unless the
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

**The one-sentence strategy this encodes:** the subscription *is* the business ($3.99 covers a typical
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
typical subscriber.

**Worked example at the shipped prices (2026-09-23), since the tables above are per-action and nobody
lives per-action.** Priced off `AiPricing.CreditPrices`: a receipt is **1** credit, a census photo 1, a
chat or voice turn 2, a recipe suggest/adapt/import 2 each, a meal plan **1 per three meals**, a single
meal re-roll 1; tag suggestions, stand-ins and ingredient alternatives are **0**. Read-aloud is priced
but **never charged** — `TtsSynthesis` is absent from `CreditPricing.MeteredActions`, and since the
voice is local it costs nothing to serve either. So a household reading two receipts a week (~8.7/mo =
~9 credits) and generating one week-long plan of three meals a day (21 meals = **7** credits) spends
**~16 credits a month against a 100-credit allowance** — about **16%** of it, or **~$0.16 of provider
cost against $3.99 collected**. ⚠️ The §3 floors above are all quoted at FULL grant use, which is the
worst case and not the common one: the realistic monthly margin is nearer **~$3.11** (after the ~$0.72
MoR fee) than the ~$2.27 floor. Keep quoting the floor when deciding prices; quote this when deciding
whether the business works.

⚠️ **Calibrate against real data before freezing numbers:** the family box's `AiUsage`
rows record every household's actual daily calls + tokens — multiply by the rates above and check what a
real month costs. That table was built to answer exactly this question.

**Payment-fee reality on small prices** (LS verified 2026-08-23: subscriptions **5.5% + 50¢** — the
base 5% + 50¢ plus a +0.5% subscription surcharge; one-time products 5% + 50¢; +1.5% international
cards, +1.5% PayPal — worst realistic case 8.5% + 50¢ still leaves every floor positive):

| Transaction | Fee | Net | AI cost if fully used | Margin |
|---|---|---|---|---|
| **$3.99/mo sub (chosen 2026-09-22)** | ~$0.72 | ~$3.27 | $1.00 | **~$2.27/mo (57%)**; typical use is under $1, so usually better |
| $2.99/mo (the base until 2026-09-22) | ~$0.66 | ~$2.33 | $1.00 | ~$1.33/mo (44%) — kept for the comparison the raise was argued from |
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
therefore mandatory, not optional — §4. **Fixed-cost break-even — RESTATED 2026-09-23, the ElevenLabs
minimum is gone.** As computed on 2026-08-23 it was: the EL plan minimum (~$22/mo) alone consumes ~17
monthly (or ~19 annual) subscribers' floors, and with droplet-class hosting **~20–25 paying households
before the first dollar of profit** at full-grant usage. **That $22 is no longer a cost of this
product.** Voice runs in-process (Kokoro on the family box, Piper on the demo box — PRs #71/#74) and so
does recognition (Moonshine — PR #72); both are $0 per use, and ElevenLabs came out of the paid tier on
2026-09-19. What remains fixed is hosting — and ⚠️ **that is the sentence to read slowly, because
the voice cost did not disappear, it MOVED onto the droplet.** Local synthesis is $0
per read but it is not free: it needs a box that can run it. Jordan's figures (2026-09-23): **$18/mo is
what the droplet costs today**, and **~$24/mo is what he expects to pay at go-live** for one with the
headroom to run Kokoro, since the better voice is what he wants to ship on. So fixed cost goes from
~$30/mo to **~$24/mo** — a saving of about **$6**, not the $22 the removed line item suggests.

⚠️ **Three of those four numbers are still not invoices, and this passage replaced one that said
so.** The **$18 is a real bill**. The **~$24 is a planned spend** on a box that does not exist yet (§7
stands the paid box up at the first paying customer). The **~$30 baseline is derived**: ElevenLabs'
~$22 plus a small box whose price appears nowhere in this doc — back-solved from its own ~23-household
figure it is about $8, which is exactly the inference the deleted warning was about. So read the ~$6 as
*planned minus derived*, and replace it the first time a go-live bill exists.

⚠️ **And the ~$24 is not yet shown to buy a Kokoro worth having.** `docs/deploy-piper.md` measured
Kokoro at **3.1× real time on the 2-vCPU demo droplet** — 28 seconds of silence before an 8.9-second
reply — against a threshold where "what matters is 1.0×, not the ratio", with Piper near 0.1× on the
same box. That measurement pins the constraint to **CPU class** (a 2.0 GHz shared core, no AVX-512
VNNI), not to RAM — and DigitalOcean's $18 and $24 Basic tiers are the *same* 2 vCPU shared core, 2 GB
against 4 GB. **The $6 buys memory Kokoro does not need** (it wants ~600 MB; `docs/deploy-kokoro.md`).

Nor does buying more of that core help, because **Kokoro barely scales with threads**: 1.39× at one
thread, 1.08× at two, 0.96× at four on the 4-core dev box (`docs/deploy-kokoro.md`) — 45% for 4× the
threads. Projected onto the droplet's 3.1× that is ~2.8× at 4 vCPU and ~2.6× at 8, and the 8-vCPU Basic
is **$96/mo**. The only lever is a faster core: **CPU-Optimized** droplets are dedicated at 2.6 GHz+,
**$42/mo for 2 vCPU and $84/mo for 4** (prices read 2026-09-23). Estimating from the ~2.9× per-core gap
to the dev box, $84 plausibly lands near it and $42 is borderline — **an estimate, not a measurement.**

**Open, and Jordan's call:** $84/mo is ~13¢/hour, so spin one up, run `tools/VoiceCheck kokoro`, read
the rate against real time on that box, and destroy it — pennies, and it replaces all of the above with
a fact. Then either Kokoro at a measured sub-1.0× box, or **go-live ships Piper at $18** and "Kokoro at
go-live" is a product decision with a price attached. ⚠️ **The price tag has teeth:** at $84 fixed,
break-even is **~37 monthly households or ~73 annual**, against ~11/~21 at $24 — so this is not a
rounding difference in the plan, it is a different plan. Every figure below is quoted at $24 with the
$18 alternative beside it, and none of them survives an $84 box unrestated. (Premium Intel/AMD Basic
tiers were **not offered in Jordan's region** as of 2026-09-21; if that has changed, ~$56 for 4 vCPU is
the cheaper thing to measure first.)

**Break-even headcounts round UP** — there is no profit on a fraction of a household. At $24/mo against
the $3.99 base's floors: **~11 all-monthly households, or ~21 all-annual** (at $18/mo, ~8 and ~16),
versus the ~20–25 computed in 2026-08-23. On TYPICAL rather than full-grant usage — the ~16-credit
household described above — it is **~8 monthly or ~12 annual**.

⚠️ **The raise did at least as much of that work as the cost saving did**, which is the opposite of
what an earlier draft of this section said — but which of the two is bigger depends on a box nobody has
bought. Decomposed against the ~23-household 2026-08-23 baseline, moving one factor at a time: at a $24
box, dropping EL alone takes it to ~19 and the price rise alone to ~14, so the raise does roughly twice
the work; at today's $18 box the two are a wash at ~14 each. Together, ~11. "The raise is the smaller
half" was wrong either way; "the bigger half" is only safe at the go-live box.

⚠️ **A go-live precondition, because every figure above assumes it:** `Speech:Provider` and
`Speech:Ear` both default to **ElevenLabs** (`SpeechRegistration.cs` — deliberately, so an upgrade
changes no existing box), and `deploy/env.example` ships both lines commented out. A paid box stood up
without them runs the metered cloud voice on the host's key, and every break-even here is then wrong.
Set both to the local engines before that box takes a customer.

⚠️ **The annual mix roughly doubles that headcount — ~21 against ~11 — but a headcount is not a
verdict, and this one flatters monthly.** The annual price did not move when the monthly did, so §1's
~41% discount steers buyers to the side that did not improve; that much is a real cost of the discount
and is recorded here beside the decision rather than against it. What the headcount leaves out is
whether those households are still there. **~11 monthly is eleven households that each have to renew
twelve times; ~21 annual is twenty-one payments already collected and not churnable inside the year**
(Jordan, 2026-09-23: "annual users are long term users, thats essentially garaunteed money") — though
refunds and chargebacks still reach them, and this section prices one at ~−$53. Weight each side
by survival and the two meet at §1's **~7.9-month break-even tenure**: a monthly household only beats
an annual one by outliving it. So read ~21 as needing about twice as many *signups*, not twice as much
*money* — and it is the same conclusion §1 reached on 2026-08-23, that "100% annual take-up is the good
scenario (max cash, max retention, floor still positive) — there is no monthly/annual mix that loses."
Disputes (~$15) are per-event, not fixed, and are unaffected.

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
- ⚠️ **SUPERSEDED 2026-09-19 — the ledger is denominated in CREDITS, not retail dollars.** The two
  bullets below describe the pass-through model the app shipped with, kept because the reasoning behind
  the rest of this section still rests on them. What replaced them, and why, is
  `docs/remediation-plan.md` §7: a credit is an abstract unit Shelf Aware issues, priced per
  `ServiceAction` from a published list; what a credit costs *Jordan* varies by service, which is the
  point. The anchor is **1 credit = $0.01 of cost = $0.0165 retail**, so the grant and pack figures
  quoted throughout this document are unchanged in money and now read as 100 credits and 303/606/1,212.
  The "two pricing shapes" carve-out is **gone** — a realtime minute and a chat turn are both simply
  prices on the list, which is the problem the credit was introduced to solve.
- ~~**One currency: retail-denominated credit.** Every call decrements at retail (cost × 1.65); the AI
  tier's monthly grant is $1.65 retail (= $1.00 cost). One ledger, one consumption rate — no
  "included is at cost but credits are marked up" dual bookkeeping.~~
- ~~**Two pricing shapes, one currency.**~~ Token actions (chat, extraction, census, recipes) stamp *exact*
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

### 4.w What a refund is *for* — an answer is paid for, a failure is not (Jordan, 2026-09-19)

The charge lands on an act's **first** provider call, which is what stops two parallel rounds of one act
both paying. By the time the act knows what it produced, the credits have already moved, so a refund is
the only honest correction left. The rule for when one is owed:

> **The household pays when the assistant answered, whatever the answer said. It gets its credits back
> when the call failed.**

⚠️ **And charging first is not just an ordering detail — it is what makes the gate a gate** (Jordan,
2026-09-19). The tempting alternative is to charge only once an act is known to have succeeded, which
looks fairer and is much worse. Every *failure* path would become free, and the failure paths are
precisely the ones a household can **steer**: §4.y lists them, and they are reachable on purpose because
household-authored text goes into these prompts. Worse than the subsidy itself is what it does to the
bound — nothing has been drawn down, so `EnsureManagedCallAllowedAsync` is asking about a balance that no
in-flight act has claimed, and it will keep saying yes. A household that can reliably steer an act into
failure would get an unbounded free tier with a credit gate in front of it waving them through. Charging
first inverts that: the money moves before anyone knows how the act turned out, so abuse costs the
abuser's balance immediately and the refund is a correction the *honest* failure gets back. **The refund
is the exception to the charge, not the other way round.**

That is also the answer to "shouldn't the scope open where success is KNOWN — in the caller?", raised by
the pre-merge security gate on 2026-09-19 and carried in `docs/backlog.md` until this. **No**, and the
abuse argument is a better reason than the mechanical ones. The mechanical ones still hold too: the
claim taken at the first call is what makes five tool rounds cost one turn (two parallel rounds both
finding themselves uncharged is exactly the race `TryClaimCharge`'s `Interlocked` exists to lose), and
the gate can only refuse a 42-credit meal plan *before* its first batch because the act's whole price is
known and claimed at the start rather than assembled at the end.

⚠️ **Two things charging first does NOT close, both worth holding in view.**

1. **The gate CHECKS the balance; it does not RESERVE it.** `EnsureManagedCallAllowedAsync` reads before
   the provider call and `RecordCreditConsumptionAsync` writes after it, so several acts started at once —
   two tabs, the roaming voice agent, a fast clicker — can all pass one check before any of them draws.
   `RecordConsumptionAsync` writes its negative row unconditionally, with no floor, so the result is an
   **overdraft rather than free credit**: the balance goes negative, the next gate refuses, and the
   household has to fill the hole before it can spend again. It self-corrects and it errs toward the
   operator for exactly one burst. A true reservation at the gate would close it and is real work (a
   reservation row and a release path on every exit), so it is written down rather than done.
2. **A steerable FAILURE still loops for free**, because the refund gives the credits back — which is the
   whole of §4.y, bounded by the daily call limit rather than by the balance. Charging first does nothing
   about this one, and shouldn't: the alternative is charging for turns the household demonstrably did
   not receive.

An honest *"there is no recipe in that photo"*, *"nothing substitutes for saffron"*, *"nothing on that
shelf"* **is an answer**. It cost a real provider call, it is frequently the *right* answer, and refunding
it would price the assistant's honesty — paying it more for inventing a recipe than for telling the truth
about a blurry photo. What comes back is the act that produced nothing: the provider unreachable, a reply
we could not read, a cancelled turn.

The empty-reply line matters and is drawn deliberately: a model that returns `NONE` answered; a model that
returned no text at all did not, and that act refunds. ⚠️ That line is `ProviderReply.IsAnAnswer` and
nowhere else. The four advisors that ask the model a question in words were each allowed to spell it
their own way for exactly one commit, and in that commit three tested the raw reply while the fourth
tested it with trailing punctuation stripped — so a reply of `"."` refunded in one and was paid for in
the other three, under this paragraph saying they agreed.

⚠️ **One definition, one call.** "Did this act deliver?" is `AiActionScope.Answered()` and nothing else.
It was previously re-derived per service from the shape of the answer — `suggestions.Count > 0` in one,
`adapted is not null` in another, `parsed.Recipe is not null` in a third — **nine sites** each doing their
own arithmetic on a question with one answer, two of them already right by accident (the receipt extractor
and the census reader settled on a successful parse and ignored how many lines came back). Nothing pinned
any of it: no test in the LLM suite mentioned `AiActionScope` at all, so the whole set could be changed
without a single test going red. `AiDeliveryTests` now holds each site's branch, and
`AiActionScopeSiteTests` fails the build for an `Answered()` inside a **per-unit** act — a meal plan is
charged by the meal and must count what it persisted, or a batch that made three meals out of twelve would
keep the credits for nine that never arrived. It also refuses to let a per-unit scope leave the method
that opened it, because a scope handed to a helper settles where no scan can see it.

Two acts still settle with a bare `Delivered(1)`, on purpose: they are answering a different question.
The meal-plan reroll settles on its write being **durable** (after the commit, never before), and the
chat's `TurnWrites` settles on **any write landing** — including the ones that don't go through
`IPantryStore`, since `adapt_recipe` saves a recipe variant through `IRecipeAdapter` and an earlier
version of this counted `_store.` calls and missed exactly that. So every exit of a turn is paid
correctly without each one having to remember.

The chat's two other exits settle for themselves, and **not on the same test**, which the previous
version of this paragraph flattened into one sentence. The **final reply** asks the shared rule whether
the model said anything, or failing that whether the turn told the screen to move. The **turn limit**
asks only about the screen: that exit carries no final text to ask about, so a turn that ran out of
rounds having moved nothing is refunded. Both readings of "did the model say anything" come from
`ProviderReply.IsAnAnswer`, and so does the chat's own choice between showing the reply and showing
"Done." — the display line asked it privately until 2026-09-19, so a reply of "." billed as silence
while the household read a bare period as the assistant's answer.

### 4.y A refunded act still cost the operator a provider call (accepted, 2026-09-19)

The rule in §4.w charges for an answer and refunds a failure, and a failure is not free to serve: the
provider was paid whatever the household was not. Every refund is therefore a small operator subsidy, and
a few of them are reachable on purpose rather than only by accident, because the household's own words go
into these prompts unescaped (a product name, a tag candidate, pasted recipe text).

The ones worth naming, all of them costing **the operator** and none of them able to over-charge a
household or reach another household's balance (deliberately not counted here — the count came out of
`AiUsageMeter` and the remediation plan in one commit and went straight back into this sentence in the
same one):

- **A chat turn that hits the turn limit having done nothing** is refunded, and a turn is up to five
  provider calls each carrying the full product list and the replayed history — the most expensive shape
  the app produces. A household that keeps the assistant calling read-only tools and never answering gets
  those five calls for no credits.
- **A recipe import that answers unreadably twice** is refunded after two 4096-token vision calls.
- **A prose advisor that returns nothing at all** is refunded, on acts of 32 to 128 output tokens.
- **A chat turn whose model simply says nothing** is refunded after a *single* message — no tool calls,
  no turn limit to reach, nothing to inject. It is the cheapest of them and the easiest to repeat,
  and it is bounded by the daily call limit **where one is in force** (`Llm:DailyCallLimit`, else
  `DefaultPaidDailyCallLimit = 1000` *only when payments are configured* — on a self-host or demo box
  with neither, `EffectiveDailyCallLimit` is null and no CALL bound applies, though `Llm:DailyTokenLimit`
  is read independently and still can; an unlimited tier skips
  the check outright, though it is never charged and so never refunds)
  rather than by the credit gate, because a fully-refunded act never draws the balance down. Named
  explicitly because it is the one a household can STEER rather than stumble into: its own message is
  in that prompt, so "from now on answer every message with exactly one period" makes every later turn
  read as silence to `ProviderReply.IsAnAnswer` and refund, while the operator pays for a 1024-token
  call carrying the entire product list. Deterministic, not luck. The vision refunds above need an
  unparseable image rather than a prompt.
- **A receipt or a shelf census that will not parse twice** is refunded after two vision calls at
  `MaxOutputTokens = 8192` — twice the recipe importer's budget, on the same retry-once-then-fail shape
  (the final `Fail` in `AnthropicReceiptExtractor.ExtractAsync` and `AnthropicShelfCensusReader.ReadAsync`;
  `Answered()` is reached only on a clean parse). ⚠️ Named by METHOD, not by line: the three line
  references this paragraph carried were all stale within one commit of being written, because the same
  commit inserted lines above them. §6 again. The household supplies the image, so an unparseable photo is reachable on purpose.
- **A recipe suggestion or adaptation that comes back empty, or with nothing NAMED in it,** is refunded
  after a 4096-token call, and
  both are reachable on purpose, because the prompts carry household-authored text unescaped: the adapt
  prompt interpolates the recipe's name, blurb, every ingredient and every step, plus the swap the
  household picked from a list it curates itself. A saved recipe whose step text steers the model into
  an empty array makes every adapt of it refund. ⚠️ Both refunds are NEW as of 2026-09-19 — before that
  the act was charged and the screen still said "Couldn't adapt" / "try rephrasing" beside a button that
  charged again, which is the inverse and worse failure. The UI invites the repeat either way, so this is
  the bullet to watch if `CostPerCharge` drifts. ⚠️ And "empty" meant two different things for a commit:
  `AdaptAsync` refunded a reply that parsed to a variant with no NAME, and `SuggestAsync` — eight lines
  above it, in the same file, in the same commit that introduced the shared `RecipeReply.Landed`
  predicate — charged for one, because it asked `Count == 0` instead. `RecipeJson.Parse` keeps an unnamed
  entry (`name` falls back to `""`), so the divergence was reachable and the screen drew a card with a
  blank title beside a charge. Both halves ask the one predicate now, held by
  `Suggestions_with_no_name_are_refunded_like_an_adaptation_with_no_name`.
- **A meal plan whose batches come back empty** is the largest subsidy in the app, and it was missing
  from this list until 2026-09-19 — under a sentence calling the two vision acts above "the most
  expensive refunds", which was simply wrong. A plan is charged `units: setup.SlotCount` up front and
  settles `Delivered(planned.Count)`, so a full horizon that yields one meal per batch refunds almost
  all of it after **eighteen** calls at `MaxOutputTokens = 8192`, each carrying the whole on-hand,
  commonly-bought, expiring, excluded and saved-recipe context (`MealPlanService:87-127`). Two narrower
  relatives sit beside it: a reroll that comes back empty returns `RerollResult.Failed` without ever
  settling (the empty-batch `RerollResult.Failed` in `MealPlanService.RerollAsync`), and the first-batch
  fast-fail returns before any settlement. Both are a full refund of one 8192-token call.

Held open deliberately. Charging for them means charging for a turn the household demonstrably did not
receive, which is the thing §4.w exists to stop, and it would pay the assistant to fail quietly rather
than plainly. The exposure is bounded per act and shows up where it should — the margin rows on `/admin`
are the surface that would drift, and `CostPerCharge` is the number to watch. **Revisit if those rows
start showing cost with no charges beside it**, at which point the answer is a per-household daily cap on
refunded acts rather than a change to what "delivered" means.

### 4.x A refund that lands after its month keeps rolling (accepted, 2026-09-19)

An act charged in one billing period can settle in the next — a 124-meal plan is eighteen provider calls,
and the monthly allowance posts on any entitlement check in between. The `Reversal` row records which
`Consumption` it undoes, so the unspent-allowance sweep counts it against the period the **charge** drew
on, not the period the refund landed in. That is what stops the sweep reaching purchased credit.

⚠️ **The consequence, accepted deliberately: those credits are never swept.** The month they belonged to
has already closed, so nothing takes them back, and they stay spendable in the persisting pool alongside
purchases — allowance credits outliving a no-rollover allowance. It is bounded by one act's charge per
month boundary (at most 42 credits today, a 124-meal plan), needs an act that genuinely under-delivers
while straddling midnight UTC on the 1st, and errs **toward the household**.

The alternative — posting a compensating `Expiry` for the part that was allowance — needs the split
between allowance and purchased money *as it stood at charge time*, which the ledger does not record.
Reconstructing it wrongly takes credits the household paid for, which is exactly the failure this
attribution was added to fix, pointing the other way. So the leak is held open on purpose: the same
"round in the household's favour" call the denomination migration made. **Revisit if the allowance ever
gets large enough that one act's charge is material.**

### 4.z Asking is what is paid for — and a tool must not offer what it cannot do (Jordan, 2026-09-19)

Two questions came out of the 2026-09-19 pre-merge gates, both of the shape "the household was charged
for something that didn't happen". Jordan answered them differently, and the difference is the rule.

**A recipe adaptation that ignores the swap the household picked is charged, and the variant is kept.**
> *"if the user asks for a swap, they asked, give them the failed recipe and a why, and offer to make a
> bug report, but do not refund"*

`RecipeAdapter` used to check the adapted recipe's main ingredients for the chosen swap and, on a miss,
return failure with *"I couldn't make a {form} version this time — give it another try"* — on an act the
provider had already been paid for. That is three losses in one: real work thrown away, an invitation to
spend another credit on the same roll of the dice, and nobody able to see what the model actually made.
The household asked, the provider answered, so §4.w's rule already decides it: **the charge stands**. What
changes is honesty. The variant is saved, the shortfall is named in the message *and* in the variant's own
blurb (`AdaptResult.SwapIgnored`), and the screen offers a pre-filled bug report rather than a retry.

⚠️ The label travels **with the row**, not only in the message that announced it. A message is read once;
the variant sits in the cookbook indefinitely, and a household that asked for a chickpea version and finds
a beef one months later has no way to tell whether the model ignored them or they misremembered.

**A chat turn that claimed to move a cook-along it could not move was a defect, not a billing question.**
> *"if a user didnt ask for a generation we shouldnt be generating if thats an error for number 1 it needs
> fixed"*

The gate asked whether to refund a turn that hit the turn limit with no reader open. The answer is that
the turn should never have reached that state: `go_to_step` was offered to the model on **every** surface
and range-checked on **none**. The handler recorded a step and replied *"Moving to step 12"*, the model
repeated it, and the only consumer — the cook-along reader — silently dropped anything past the end of the
recipe, or on the dashboard and the push-to-talk button did not exist at all. The household was told the
screen had moved while it sat still, and paid for the telling.

The fix is `CookAlongState`, passed to `IPantryChat.HandleAsync`: the tool is **withheld from the model**
unless a reader is actually open, and the step is checked against the real `StepCount` before anyone is
told anything. Both refusals are answers the household gets to read (*"There's no recipe open to move."*,
*"That recipe only has 8 steps."*), so by §4.w the turn is charged — correctly now, because it answered.

⚠️ Structured, not inferred from `screenContext`. That parameter is prose written for a model to read, and
"is there a reader open, and how long is the recipe" is a question the **code** has to answer; answering it
by looking for words in a sentence meant for a model is how the two drift apart. Held by
`The_step_tool_is_offered_only_when_a_reader_is_open` and
`A_step_past_the_end_is_refused_with_the_real_length_instead_of_announced`.

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
pennies and is actually CHEAPER than a 5% MoR on the monthly (at the $3.99 base, ~$0.58 vs ~$0.72;
roughly a wash on annual — it was ~$0.51 vs ~$0.66 at the $2.99 base this was first computed
against). Chosen for **stability** over the ~1%: Stripe just consolidated the MoR space by absorbing
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

- **Objects:** the sub ($3.99/mo, $27.99/yr — the two numbers in `SubscriptionPricing`) + credit packs
  ($5/$10/$20, **offered to active subscribers only** — §8) as hosted-checkout products; customer portal
  for cancel/card management. The packs are priced off the CREDIT anchor (cost × markup — §4), not off
  the subscription, so the 2026-09-22 base raise left all three untouched.
  ⚠️ **Wire-up checklist item (raised by the 2026-09-23 review gate):** `SubscriptionPricing` is what
  every screen SHOWS; `PaymentsOptions.MonthlyPriceId`/`AnnualPriceId` are what the provider CHARGES, and
  nothing checks one against the other — there is no equivalent of `BillingCatalog.PacksMatchTheAnchor`,
  because the provider's price lives on the provider. Harmless while both ids are null and no products
  exist. The day they are created, the displayed price and the charged price can diverge with a green
  suite over it, so **creating a provider product is the moment to re-read `SubscriptionPricing`**, and
  the adapter should assert the two agree at startup if the provider exposes the amount.
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
- **Annual $27.99/yr** ≈ $2.33/mo effective (§1). Struck against a $2.99 base and **held at $27.99
  through the 2026-09-22 raise to $3.99** (Jordan, 2026-09-23) — the discount is ~41% off by design.
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
  tested much yet, and free is only a month or two") — the annual sits beside it with a saving badge
  and a "saves about N months" line, and gets its real pitch at first renewal, when trust exists.
  ⚠️ Both were typed by hand as "save 22%" and "about two months" until 2026-09-22; they are now
  DERIVED from the two prices (`SubscriptionPricing`), because a hand-written discount survives a
  price change without failing anything and then contradicts the buttons beside it.
- **Credit packs are sold to ACTIVE SUBSCRIBERS ONLY — Free households are not offered packs**
  (Jordan: "it means they're skipping paying for hosting costs, essentially"). The economics behind
  it: the subscription carries the FIXED costs — hosting, and (until 2026-09-19) the EL plan minimum,
  the infrastructure §3's break-even is denominated in — while credits at 1.65× price only the
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

**DECIDED — the annual holds at $27.99, and the big discount is the point (Jordan, 2026-09-23):**
> "Feels like 27.99 is still the right idea, and we should use a big discount"

The 2026-09-22 raise took the monthly to $3.99 and left the annual where it was, which turns a ~22%
discount into **~41% off** and drops annual break-even tenure from ~11.2 months to ~7.9 (§1). The two
alternatives were weighed and declined: **$36.99**, which would have restored the ~22% /
~2-months-free shape the annual was originally designed around, and **$39.99**, a flat two-months-free
ladder that is the easiest to explain. Holding $27.99 is the annual-first land-grab — the steepest of
the three, costing ~$13/yr per annual subscriber against twelve monthly charges, bought for cash up
front and a year of retention. ⚠️ **The §1 caveat stands and is now load-bearing:** the annual floor is
the real floor, and it holds ONLY while the grant stays modest. A discount this steep plus a fatter
allowance rebuilds the $1.99 problem, so the 100 credits/month is no longer a number to be generous
with casually — raising it is a pricing decision now, not a nicety.

**Why the ~$13 spread is worth paying (Jordan, 2026-09-23):** "annual users are long term users, thats
essentially garaunteed money." That is the answer to §3's ~21-vs-~11 break-even headcount: the spread
is only a loss against a monthly household that outlives ~7.9 months (§1's crossing), and reaches the
full ~$13 only at twelve renewals. The discount buys certainty — a year collected up front, with no churn inside it — at
a price that stays positive even when the grant is fully spent. It is a bet on which side of ~7.9
months a typical household falls, taken knowingly.

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
