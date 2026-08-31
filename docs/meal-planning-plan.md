# Meal planning — design plan

Status: **design, pre-implementation.** Owners: Jordan (+ wife). Drafted 2026-08-31.

This is the consolidated design before any code, in the mold of `subscription-plan.md` /
`undo-history-plan.md`. It records decisions *and their reasons* so a build session (or a future
reader) doesn't re-litigate them. File paths below are current as of drafting (verified by reading
the code).

---

## 1. Why — this is a weight-loss tool, not a pantry optimizer

The real driver is **weight loss**. With no plan, "what's for dinner?" collapses into snacking —
unstructured grazing is the enemy. And the household **doesn't like planning meals, so they don't**.
So the feature has to do the planning *for* them.

Consequences that shape everything:
- **Success = they cook the planned meals instead of snacking.** This is a behavior-change tool.
- **Its core value is removing the daily decision**, not culinary cleverness.
- **Its failure mode is a plan people abandon** — so *low friction* and *realism* beat ambition.

---

## 2. Core principles (settled with Jordan — build on these, don't re-open)

1. **The plan is a *secondary* prediction that defers to reality.** For stock you *have*, the plan
   predicts consumption ("this chicken is spoken for Thursday") and **stops the instant the item is
   actually removed** (an OutNow, a recount, a consumption). Same philosophy as Restocked/OutNow: a
   soft overlay that yields to a hard fact.
2. **The plan never teaches the predictor.** The learned cadence is taught by **receipts only** —
   actual purchases. Buying a planned ingredient teaches the predictor the way it always has, via the
   receipt. The plan mutates no prediction state.
3. **Therefore the plan's pantry effects are *derived at read time*, never stored** (§6). This is
   *how* principles 1–2 hold: nothing persisted can outlive the fact it rested on, so the plan can't
   contradict a real signal and can't leak into the learned model.
4. **Two stories, labeled — not merged.** Plan-driven and predictor-driven shopping coexist on one
   list, visibly distinct (§5), rather than averaged into one number. This is "one prediction, one
   story" satisfied by *labeling*, not reconciliation.
5. **Use what's about to expire first** (§7).
6. **The setup is all-optional** (§3). Zero input → "real cooked meals, high variety, use my pantry +
   expiring first." Any field filled → a constraint.

---

## 3. Setup screen — everything optional

- **Meal count / shape** — meals per day and which slots (breakfast / lunch / dinner / snack). Snacks
  are just small meals (§4).
- **Calories** — daily and/or per-meal target.
- **Protein / carbs** — targets.
- **Food-group balance** — *tunable target proportions* across groups (cover/balance, not
  include/avoid). Adjust the balance to taste.
- **Time & effort** — one slider, "Quick & simple ↔ Ambitious," bundling cook time + technique
  difficulty. The realism lever.
- **Appliances** — optional checklist (slow cooker, pressure cooker, air fryer, grill, blender).
  Default = oven + stovetop only. Doubles as a realism lever (slow cooker = dump-and-go, the
  weeknight win for a low-effort household).
- **"Invent something new"** — off by default: the generator sticks to your pantry + commonly-bought
  ingredients and avoids exotic ones. On = creative/novel meals.
- **Avoided foods** — *not a new field.* Reuse the existing `ExcludedFood` won't-eat list
  (`src/ShelfAware.Core/Domain/ExcludedFood.cs`), always hard-respected, never a soft knob. It's
  already hard-excluded by `recipe-suggest-system.txt` rule 3 / `recipe-adapt-system.txt` rule 4.

---

## 4. Generation — adapt known recipes, don't invent (default)

New seam `IMealPlanGenerator` (Core) → `AnthropicMealPlanGenerator` (Llm) + prompt
`meal-plan-system.txt`, built **on the adapt path** rather than as a from-scratch generator
(Jordan's call — it unifies with "don't invent by default").

**Generation model — three tiers, in order:**
1. **Adapt your saved recipes** to fit the pantry + constraints — literally the existing adapt path
   (`RecipeAdapter` / `AdaptAsync`), at plan scale.
2. **Fit a known/standard dish** to the pantry + constraints when the saved base runs out — the model
   knows thousands of standard recipes; this is a *constrained generate* (a known dish written
   correctly for your ingredients), not invention.
3. **Invent a novel dish** — only when the "invent this meal" box is on.

Default = tiers 1–2 (known food, familiar ingredients). "Don't invent" spans **dish novelty** (known
dishes) *and* **ingredient familiarity** (commonly-bought/ordinary ingredients — chicken, rice, peppers
can hit the shopping list; gochujang, za'atar can't) until the invent box flips both on.

⚠️ **Every adapted/generated recipe respects ALL the setup constraints, not just the pantry** (Jordan's
refinement): calories / protein / carbs / food-group balance / time-effort / appliances / avoided
foods. Today's *manual* adapt only knows on-hand + `ExcludedFood`; the planner's adapt is a **superset**.
Keep one definition — extend `AdaptAsync` with an **optional constraint bundle** (manual adapt passes
none → unchanged; the planner passes the full setup) and add the conditional constraint rules to the
adapt prompt (applied only when provided). Don't fork a second adapter.

**Escape hatch — sparse cookbook → suggest invent.** When the household has no / very few saved recipes
(their real day-one state, ~15 saved), tier 1 is thin. The planner still has tier-2 known dishes, but we
**suggest enabling "invent"** for a fuller, more varied month — a nudge, the user's choice, never an
auto-switch.

Other constraints: **expiring-first** bias (§7); **hard-exclude** `ExcludedFood` (always, never a knob);
**unique across the horizon** (high variety, controlled repeats — a loved meal may recur, identical
meals shouldn't).

⚠️ **Because generation rides on the adapt path, phase 0 (§8) is load-bearing** — the planner's meals
are only as method-correct as adapt is, so adapt getting braise-vs-sear right *is* the plan getting it
right, at scale.

Output: each planned meal is a real **`Recipe`** (`src/ShelfAware.Core/Domain/Recipe.cs`) — reuse the
entity; it already carries `Steps`, `Ingredients`, `EstimatedCaloriesPerServing`, and every recipe
surface for free (read-aloud, print, cook-along, makeability). Ingredients carry the grounded
`MatchedProduct` so pantry math works. Generated meals are flagged **plan-generated** so the Cookbook
hides them until "kept" (no 30-meal clutter).

**Snacks = the same primitive.** A "meal" is `(calorie target, food-group target, slot)`; a snack is
that configured small. No special-casing anywhere.

**Reroll** — every calendar slot regenerates that one meal against the same constraints, plus
"different from the meals already in this plan." Reuses the same adapt/generation path.

---

## 5. The plan's surfaces

### 5a. The calendar (the plan's home)
Month-at-a-glance: what's cooking each day, per slot. Each slot → its `Recipe` (tap to
cook/read-aloud/print), with reroll.

### 5b. The shopping list — the same page, enriched (NOT a twin page)
Jordan's call, and the safer one: the plan's shop items live **on the existing grocery list**
(`src/ShelfAware.Web/Components/Pages/GroceryList.razor`, `/list`), not a cloned page — two shopping
surfaces are exactly the "two screens, two stories" failure the earliest-card dedup exists to prevent.

The list gains a **third provenance**. Today it has two: predictor items (the table, `ProductEstimate`
rows) and manual Extras (`GroceryExtra` chips, rendered as a separate section). Plan items become the
third, **intermixed** into Buy now / Coming up, sorted by due date, each row **tinted + labeled by
source**.
- ⚠️ Color can't be the only signal (a11y, colorblind, screen readers) — a one-word **source tag**
  rides with the tint. There is **no per-row source badge anywhere today**; this is net-new UI. Extend
  the existing color system (`app.css` token triplets `--overdue/duesoon/stocked/unknown` + `.chip-*` /
  `.card.accent-*`) with a "plan" provenance color.
- **Plan items = ingredients the plan needs that you don't have** (on-hand ingredients are handled by
  the projection, §6). Each carries a **due date = the shopping trip before the meal** (v1: meal date −
  a small lead; refine later). It shows the real countdown ("due in 6 days") and drops into **Coming up**
  only when it's more than ~7 days out — matching the dashboard's existing `ComingUpHorizonDays = 7`.
  ⚠️ This is a *fixed* window, deliberately distinct from the predictor's cadence-aware `StatusFor`
  window (`max(3, 0.2·median, IQR)` capped at `interval−1`) — plan items have a *declared* due date, no
  rhythm.
- **Earliest-card dedup:** when a product is wanted by both the plan and the predictor, show one row —
  the earlier due date — and suppress the later. The surviving row wears whichever source was earlier.
  (So occasionally a "plan" item shows as "predictor" because the predictor needed it sooner. Expected.)

⚠️ **One-definition risk to design around from day one:** `GroceryList.razor` (table) and `Home.razor`
(cards) currently compute status with **copy-pasted** `ChipClass`/`StatusLabel` helpers and read
products independently. The provenance concept — color, label, and the dedup rule — must live in **one
Core place** consumed by both, never pasted into each. (Copy-pasted-fact-in-two-places is this repo's
single most expensive defect class.)

Plan items may also surface on the dashboard's "Running Low" / "Coming up this week" panels
(`Home.razor`) via that same shared definition.

### 5c. Provenance = derived, so almost no new state
Plan shop items are **derived** (§6), so they need no `GroceryExtra`-style table. They appear because a
planned meal in the horizon needs them and you don't have them; they vanish when the meal is cooked,
rerolled, or removed, or when you buy the item. Principle 3, made visible.

---

## 6. The pantry projection — how the plan touches stock without lying

Everything the plan "knows" about your pantry is **computed at read time** from
`(planned meals in the horizon) + (current stock & signals)` — never stored:

- **Have the ingredient →** a *secondary consumption prediction*: "spoken for by Thursday's meal."
  Recomputed every read, so the moment the item is marked out / recounted / consumed, the overlay
  retires (principle 1). Writes **no** signal, purchase, or prediction state.
- **Don't have it →** a plan shop item on the list (§5b) with a buy-before due date.

Because it's derived, it **cannot contradict a real signal** (reality is an input, recomputed each
read) and **cannot teach the predictor** (it writes nothing). That's why principles 1–3 are one idea.

Reuse: `IngredientMatcher` / `PantryOnHand` (`src/ShelfAware.Core/Recipes/`) decide "have it?" exactly
as the recipe pages already do, so the plan and the recipes can't disagree about coverage.

---

## 7. Census populates expiration dates (v3.6 extension) — with a real storage constraint

**Decision (settled):** the shelf-photo census reads a package's printed best-by date and offers it as
a **suggested date the user confirms** in the review grid — never silently written. Consistent with the
census's own model: a printed date is a `Label`-evidence case, exactly what the census already reads
(`CensusEvidence.Label`). v3.6 made expiration human-only because *receipts* don't print it; *packages
do*, so the census is a genuinely different case. The planner **uses expiration when present and
degrades when absent** — never requires it (v3.6's fail-inert rule).

⚠️ **The constraint the code read surfaced — this is more than "add a column."** `ExpirationDate`
today lives **only on `PurchaseEvent` / `ReceiptLine`**, and the one write path
(`IPantryStore.SetExpirationAsync`) writes onto the *latest purchase* and returns false when there are
none. But the census **never creates a `PurchaseEvent`** (the ★ rule — it writes `StockLedger.Attest` +
an `OutNow` for asserted zeros, nothing else), and a census-counted product may have **zero purchases**.
**So there is nowhere for a census expiration date to land today.**

**Decision (agreed 2026-08-31):** let expiration attach to the **attested count**, not just a purchase
— a `Product.CountExpiration DateOnly?` set alongside `StockLedger.Attest`. This generalizes expiration from
"purchase-borne" to "stock-borne," which is exactly what a census provides ("I have 3, best-by March").
The predictor's `honorExpirations` step (today: the longest date among the latest buy's purchases) also
consults the count-borne date; the governing date wins. Contained, but it touches the expiration
model's assumption that dates ride on purchases — decide it deliberately, don't slide into it.

Otherwise the read is straightforward: a field on the census reader schema
(`AnthropicShelfCensusReader.OutputSchemaJson`) + a `shelf-census-system.txt` rule + `CensusItem` + an
8th review-grid column beside "How many" in `PantryPhoto.razor`, confirmed like everything else.

---

## 8. Prerequisite: the substitution-method fix — refined diagnosis

Earlier guess: "probably the adapt prompt." Reading the code, it's sharper — and mostly a *different*
path than adapt:

- **The real culprit is the makeability tick, not adapt.** A steak recipe (steps: "sear 2 min/side")
  turns **green / "you have this"** the moment an on-hand **Chuck Roast** lists "steak" on its curated
  *also-works-as* list — via `IngredientMatcher` → `Recipe.IsMakeableWith`. **No adapt runs, so the
  saved steps show unchanged.** That path was only ever designed to answer "is this covered?", never to
  touch steps. Result: the app says "you can make this" beside sear-the-chuck-roast steps.
- **Adapt is *also* weak on method**, secondarily. `recipe-adapt-system.txt` rule 3 does say "rewrite
  the steps," but every concrete cue is about cook *time* ("thighs take longer," "fish cooks faster,"
  "ground vs. whole") — nothing names a *technique* change (braise a tough cut vs. sear a tender one).
  So even when adapt runs, braise-vs-sear isn't reliably instructed.
- Not the culprit: the recipe-*suggestion* advisor (it generates fresh steps from scratch, so there's
  no original method to retain) — which is also why the *planner's* generated meals are safe.

**Fix (Jordan's call — route substitute-covered recipes through Adapt), three parts:**
1. **Detect literal-vs-substitute coverage cheaply, in Core.** `IngredientMatcher` /
   `Recipe.IsMakeableWith` already distinguish a match on the literal ingredient / `MatchedProduct`
   from a match via a product's *also-works-as* substitute — surface that distinction (still plain
   code, no AI, still fine to run per-render). This is the one definition of "covered, but by a swap."
2. **Present substitute-only coverage honestly, and route its *cook action* through Adapt.** A recipe
   makeable only via a substitute shows as *"makeable with a swap"* — not a plain green that implies
   the written steps are right — and acting on it (cook / read-aloud / make) goes through the existing
   `RecipeAdapter` / `AdaptAsync` path so the steps + method are rewritten for what you actually have.
   ⚠️ **Adapt fires on demand, never per render** — it's an AI call that creates a variant, so it can't
   run on every makeability computation (cost, latency, keyless visitors, cookbook clutter). Cheap
   detection (step 1) drives the *display*; the adapt fires when the user *acts*.
   - **Keyless / no-AI degrade:** if adapt can't run (no key), still show "makeable with a swap" and
     warn that the written steps assume the original ingredient — never silently show wrong steps.
3. **Strengthen `recipe-adapt-system.txt`** to explicitly require a *technique/method* change when the
   substitute demands it (braise a tough cut vs. sear a tender one — not just cook *time*), pinned by a
   mutation-checked test on a method-change swap (steak→chuck / breast→whole).

⚠️ **"Also works as" means "fine in the same *meal*," not "cooked the same" — Jordan's definition, and
it drives the whole fix.** A substitute *always* triggers the rebuild; it makes **no difference** whether
the substitute was AI-seeded or the user's own curation. Neither the makeability routing (steps 1–2) nor
the adapt prompt (step 3) may treat a user-declared "works the same" as license to keep the original
steps — marking chuck as "works as steak" says "I'll eat chuck in this dish," never "braising = searing."
This forecloses the tempting shortcut of trusting a user-blessed swap.

**Decision (agreed): not automatic.** A swap-covered recipe shows "makeable with a swap"; the user
triggers the fix through the **existing manual adapt** — the "🔀 Adapt to what I have" button, a
swap-cloud bubble click, or the `adapt_recipe` chat tool. No new adapt UI, and never a silent adapt
while browsing.

This is **phase 0** because the planner leans on substitution (use-what-you-have / expiring-first),
and it's a pre-existing bug worth fixing regardless.

---

## 9. Data model & tenancy

New household-owned tables (each walks the full tenancy drill below). **Built in phase 1a (2026-08-31):**
- **`MealPlan`** — `Id`, `HouseholdId`, `CreatedAt`, `StartDate` (DateOnly), `Days` (int), `Meals` (nav).
  **One active plan per household — regenerating replaces it** (the service deletes the old plan + its
  unkept plan-generated recipes; phase 1b). No config snapshot: the setup lives in AppSettings (below).
- **`PlannedMeal`** — `Id`, `HouseholdId`, `MealPlanId` (FK, cascade), `RecipeId` (FK, cascade), `Date`
  (DateOnly), `Slot` (`MealSlot` enum: Breakfast/Lunch/Dinner/Snack). ⚠️ **Two cascade parents** (MealPlan
  and Recipe), both by EF convention — SQLite has no multiple-cascade-path restriction, pinned by the
  schema-parity + delete-cascade test. **No `State`/`CookedAt` for v1** — cooking is recorded by
  `MealEvent` ("Ate it"); an adherence column is an additive change if phase 2 wants it. Indexed
  `(MealPlanId, Date)` for the calendar's ordering.
- **Config lives in AppSettings**, not a table (the §11 open question, resolved): a single
  `MealPlanSettings` JSON key, the way `LastRecipeSuggestions` stores structured per-household config. It's
  wiped by "delete my data" (all AppSettings are — item 33), correct for setup that has no meaning once
  the pantry is gone.

Additive columns (AdditiveSchema, like `Recipe.EstimatedCaloriesPerServing` already is):
- **`Recipe.PlanGenerated`** (bool) — hide plan meals from the Cookbook until "kept."
- **`Product.CountExpiration`** (DateOnly?) — the census/stock-borne expiration (§7); predictor reads it.

Reused as-is: `Recipe` (+ `EstimatedCaloriesPerServing`, already present), `RecipeIngredient`
(`MatchedProduct`, `IsMain`, `AlternativesJson`), `RecipeStep`, `MealEvent`, `ExcludedFood`,
`IngredientMatcher`, `PantryOnHand`, `MealStock`.

**Tenancy drill** (from the census/undo arcs — every new table does *all* of it):
`IHouseholdOwned`; `DbSet` + `ApplyHousehold<T>` in `ShelfAwareDbContext` (query filter + SaveChanges
stamping + cross-household write refusal come free); `AdditiveSchema.EnsureTable` + a schema-parity
test; export in `UserDataService` + the snapshot record; delete-my-data (FK order, child before
parent); `CountAll`; an isolation test in `HouseholdIsolationTests`.

Derived, **not** stored (principle 3): the plan's shop items and the consumption overlay — computed
each read from planned meals + current stock.

---

## 10. Honesty: nutrition is estimated

Once calorie/protein/carb targets are on, the app shows **AI-estimated** nutrition for generated meals
— rough numbers. Present them as estimates (the way recipes already carry a label-check disclaimer),
never as precise counts. This is a weight-loss tool; a hallucinated "420 kcal" quietly steering the
week is the failure to avoid. `Recipe.EstimatedCaloriesPerServing` (nullable, already exists) is the
field.

---

## 11. Suggested phasing & open questions

Phasing (each its own gated branch, `/pre-push` before any merge to master):
0. **Prereq — the substitution-method fix (§8).**
1. **Data + generation** — tables, `IMealPlanGenerator` + prompt, the setup screen; generate a plan
   (no pantry integration yet).
2. **Calendar + reroll.**
3. **Pantry projection + grocery-list provenance** (§5b/§6) — the shared-definition provenance in Core,
   earliest-card dedup, the third source on the list + dashboard.
4. **Census expiration** (§7) — the `CountExpiration` model change + the review-grid column.

Open (settle during build):
- Exact buy-before lead time (flat N days vs. tied to shopping cadence vs. per-perishability once we can
  sense it).
- ~~Config home: on `MealPlan` vs. AppSettings.~~ **Resolved: AppSettings (§9).**
- How much the plan-generated Recipes show in the Cookbook by default.
- Adherence: the plan can read `MealEvent`/`TimesEaten` for "cooked vs. planned," but must **not**
  nag/shame (counterproductive for the goal). Light touch, phase 2+.
