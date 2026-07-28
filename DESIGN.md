# Shelf Aware — Design Document
*LLM-powered pantry replenishment tracker. Display name: **Shelf Aware**. Repo/solution/namespace: `ShelfAware`.*

**Author:** Jordan Curran · **Status:** Approved for build · **Target:** 2-weekend core — then kept going over later weekends because it was fun (and enjoying the work matters)
*As-built deviations and environment notes live in [CLAUDE.md](CLAUDE.md).*

---

## 0. Instructions to the AI coding assistant
1. **Don't expand scope.** No auth, multi-user, mobile, notifications, or retailer integration. Not in this doc → ask first.
2. **Prefer boring solutions.** Minimal packages, no speculative abstractions, no microservices/CQRS/MediatR. One solution, three projects (§3).
3. **Keep all LLM interaction behind interfaces** (`IReceiptExtractor`, `IPantryChat`, and — added later — `ITagAdvisor`, `IRecipeAdvisor`) — provider swappable, the rest of the app testable without API calls.
4. **The prediction engine must be pure, deterministic C#** with unit tests. No LLM in the prediction path.
5. Build in §10 phase order; don't start a phase until the previous one's acceptance passes.

## 1. Product summary
A single-user web app answering one question: **"What am I about to run out of?"**
- Photograph a receipt → LLM extracts + normalizes line items into structured purchase events (after a human confirm step).
- A deterministic engine computes each product's typical repurchase interval and predicts run-out dates.
- Dashboard shows a "Running Low" list; a natural-language box ("we're out of dog food, almost out of coffee") adjusts state via LLM tool calls.

**Design principle (say this in the README):** LLMs where language understanding is genuinely required — parsing messy receipt text, interpreting NL updates. Prediction is plain statistics, because statistics suffice there.

## 2. Tech stack
| Layer | Choice |
|---|---|
| Runtime | .NET 10 (LTS) |
| Web/UI | Blazor (Interactive Server) — single project, C# end-to-end |
| Data | EF Core + SQLite (single file, no migrations) |
| LLM | Anthropic Messages API; extraction + chat on `claude-haiku-4-5-20251001` (cheap, vision). Pin versioned IDs, never aliases. Config-switchable to Sonnet for hard receipts. |
| Secrets | `dotnet user-secrets` locally; App Service settings on Azure. Never commit keys. |
| Hosting | Azure App Service (Linux, F1/B1); SQLite under `/home/data/` (persisted) |
| Tests | xUnit — prediction engine + extraction eval harness |

> Tool/function calling was speced as Semantic Kernel (preferred) or a manual loop; the build uses a manual tool-call loop over the SDK — shipping beats purity (see CLAUDE.md).

## 3. Solution layout
```
ShelfAware.slnx
  src/ShelfAware.Web/     # Blazor app: pages, components, DI wiring
  src/ShelfAware.Core/    # Domain: entities, prediction engine, interfaces (no LLM, no EF)
  src/ShelfAware.Llm/     # LLM impls (receipt extractor, pantry chat, tag + recipe advisors), prompts, schemas
  tests/ShelfAware.Tests/ # xUnit: prediction engine unit tests
  tests/ShelfAware.Evals/ # Console eval harness for extraction accuracy (§9)
  DESIGN.md               # this file
```

## 4. Data model (EF Core entities)
```
Product         Id · Name (canonical item, brand-stripped) · Category (store-aisle enum below) ·
                DefaultUnit (string?) · IsTracked (bool, default true) · Tags (ProductTag[])
PurchaseEvent   Id · ProductId · PurchasedAt (DateOnly) · Quantity (decimal=1) ·
                Brand (string?) · Size (string?) · Source (Receipt|Manual|Chat) · ReceiptId (FK?)
Receipt         Id · Merchant (string?) · PurchasedAt (DateOnly?) · ImagePath ·
                RawModelJson (full extraction output, audit) · Status (PendingReview|Confirmed|Discarded)
ReceiptLine     Id · ReceiptId · RawText (verbatim) · NormalizedName · Brand (string?) · Size (string?) ·
                Quantity · UnitPrice (decimal?) · Category · Confidence (0–1) · ProductId (FK?, set at confirm)
ProductAlias    Id · Merchant · RawText (unique with Merchant) · ProductId   # deterministic repeat-match memory
InventorySignal Id · ProductId · SignaledAt (DateTimeOffset) · Kind (OutNow|RunningLow|Restocked)
ProductTag      Id · ProductId · Value                            # free-form descriptive tag (2nd category layer)

# Recipes feature (§8) — beyond the original spec:
ExcludedFood    Id · Value                                        # foods the user won't eat; hard-excluded from suggestions
Recipe          Id · Name · Blurb (string?) · SavedAt (DateTimeOffset) · TimesEaten (int) · Ingredients (RecipeIngredient[])
RecipeIngredient Id · RecipeId · Name · IsMain (bool) · MatchedProduct (string?)  # LLM's ingredient→product map, captured once at save
GroceryExtra    Id · Name                                         # manual / from-recipe one-off grocery-list item

Category enum (store aisle): Dairy, Meat, Produce, Pantry, Frozen, Beverage, Household, PetCare, PersonalCare, Other
```
**Two-layer categories:** `Category` is the single store-aisle (drives grocery-list order); `ProductTag`s are free-form descriptive labels (Condiment, Canned, Snack, …) powering a browsable, filterable tag cloud. See CLAUDE.md for the two-stage tag dedup and the recipe availability model.

**Alias flow:** before LLM normalization, match `(Merchant, RawText)` against `ProductAlias`; pre-matched lines skip the LLM (cheaper, deterministic). On confirm, write/refresh aliases.

> Brand and Size are per-purchase metadata on both ReceiptLine and PurchaseEvent — the product is the brand/size-agnostic item, so the same item bought across brands/sizes rolls up. See CLAUDE.md for the matching + dominant-size prediction model.

> **v4.0 adds an opt-in stock count to `Product`** (`TrackQuantity`, `QuantityOnHand`, `QuantityCountedAt`) — the first thing in the model that measures *stock* rather than *flow*. Spec + invariants in **§13**; not built yet.

## 5. Receipt extraction (the AI centerpiece)
**Flow:** Upload → client resize (longest edge ≤ 1568px, JPEG q≈80) → `IReceiptExtractor.ExtractAsync(images)` → editable review table → user confirms → persist `PurchaseEvent`s + aliases.

**A receipt = one or more images.** Paper receipts are one image; digital order pages can span several screenshots — send all images for one receipt in a single call and merge into one line list. **Print-to-PDF order pages too:** pass the PDF as a document content block (Anthropic ingests PDFs natively — no rasterizing/resize); barcode/payment pages are noise the prompt discards.

**Output contract** — strict JSON Schema: `merchant`, `purchase_date`, `lines[]`; each line `raw_text`, `normalized_name`, `brand?`, `quantity`, `size?`, `unit_price?`, `category` (store-aisle enum), `tags[]` (descriptive labels for the second category layer), `confidence` (0–1). Validated server-side and in C#. **Extraction is also fed the existing product names (LLM-assisted matching → per-line `existing_product`) and the live tag vocabulary (so the model reuses tags instead of coining near-dupes).**

**System prompt** — the live prompt is an embedded resource in `src/ShelfAware.Llm/Prompts/`; iterate there, not in code. Key rules: output ONLY schema JSON (no prose/fences); `raw_text` verbatim; `normalized_name` = short canonical item — EXPAND paper abbreviations ("GV WHL MLK 1GAL" → "Whole Milk"), COMPRESS verbose digital titles, keep the item's distinguishing words, put size in `size` and brand in `brand`; don't invent items, skip non-product lines (subtotal/tax/coupons/loyalty/fuel); "2 @ 3.99" = qty 2, unit_price 3.99; digital "Qty N" + one price = line total → unit_price = price ÷ qty; weight-priced → quantity = weight, unit in `size`; `confidence` = certainty in the normalization (< 0.6 when guessing); non-receipt image → empty lines; handle paper OR digital, ignore UI chrome, record a substitution as the item actually received.

**Robustness:** validate output against the schema; on failure retry once with the error appended. Two failures → friendly error, keep the image, mark `PendingReview`.

## 6. Prediction engine (pure C#, `ShelfAware.Core`)
For each tracked `Product`, **two purchase-anchored rhythms** — learned from real purchases only, never restocks:
1. Distinct `PurchasedAt` dates, sorted; collapse same-day events. `< 2` purchases → **Unknown** ("still learning").
2. **Rebuy rhythm** = median gap between consecutive purchases (robust to a stock-up outlier; `≥ 4` dates → discard gaps > 3× median, re-take). "You buy this ~every N days."
3. **Burn rate** = for each purchase, days to the *first* `OutNow` after it (before the next purchase), one cycle per purchase; median once there are `≥ 2` completed cycles. "One lasts ~N days."
4. **Hybrid:** burn rate drives the prediction when available (the truer run-out signal), else fall back to the rebuy rhythm. `DueDate = LastStockBack + Floor(drivingMedian)`, where **LastStockBack** = the most recent purchase *or* restock.
5. Status: **Overdue** today > DueDate · **DueSoon** today ≥ DueDate − max(3 days, 20% of median) · **Stocked** otherwise.
6. `InventorySignal` overrides: `OutNow` → **Overdue** (pinned), DueDate = the outage date, until a later purchase/restock; `RunningLow` → at least **DueSoon**; `Restocked` → clears an out/overdue state and re-anchors the due date (it's a "last stock-back"), but is **status-only** — it does NOT feed either rhythm (only real purchases do — "count it if I bought one, not if I found one").
7. `PredictionResult { ProductId, Status, DueDate?, MedianIntervalDays? (the winning one), RebuyIntervalDays?, BurnRateDays?, Basis }`. Product Detail shows both rhythms + the gap ("out ~N days before you restock"); everywhere else shows the winning number.

**Unit tests required:** 2-purchase minimum, median vs outlier trim, each status boundary (±1 day), every signal override, same-day collapse, burn-rate pairing + hybrid switchover, restock-not-in-rhythm.

> **v4.0 (§13, not built):** for a product with a tracked count, a positive count *suppresses* the buy recommendation (real evidence beats a learned guess) and the rhythms above take a second job — **auditing the count for drift**. The engine never edits a count and the backtest never sees one.

## 7. Natural-language updates (tool calling)
Single-turn dashboard box. `IPantryChat.HandleAsync(userText)` runs a tool-calling loop with:
```
record_signal(product_name, kind: OutNow|RunningLow|Restocked)
add_purchase(product_name, date?, quantity?)
query_status(product_name?)                 # null = return the Running Low list
create_product(name, category)              # only when no fuzzy match exists
set_tracking(product_name, tracked)         # start/stop tracking a product (untrack)
```
Chat prompt: resolve names against the provided product list with fuzzy matching; clarify ONLY when two products are plausibly intended; multiple statements → multiple tool calls; reply with a one-line confirmation. Same pinned Haiku ID.

## 8. UI
Spec baseline was three pages — Dashboard (`/`), Upload (`/receipt`), Products (`/products`); the build added Grocery List (`/list`), Trends (`/trends`), Product Detail (`/product/{id}`), Accuracy (`/accuracy`), and Recipes (`/recipes`) (CLAUDE.md). Dashboard = "Running Low" (Overdue + DueSoon, signal-pinned first), each row name / status chip / `Basis` / [Bought today][Restocked], plus the chat box and a collapsed "everything else" table. Upload = image → spinner → editable review table (name, qty, category, tags editor, product-match dropdown w/ "create new", low-confidence highlight) → [Confirm all]. Products grid = filters + an always-available **[Out]** button + a clickable **tag cloud** that filters the grid (deep-linkable `?tag=`). Grocery List = by aisle + copy/print + a manual **Extras** section. Recipes = won't-eat list, NL "what can I make?" suggestions grounded in on-hand products, saved recipes with "Ready to make"/"Missing items" badges, "Ate it", "Pick for me", and "Add missing to list". Visual polish deferred until after Phase 4.

## 9. Eval harness (`tests/ShelfAware.Evals`)
Console app: `dotnet run --project tests/ShelfAware.Evals`.
- `fixtures/` holds real receipts (digital screenshots + a few paper; a multi-screenshot order = one fixture) with `<name>.expected.json` hand-labeled ground truth.
- Per fixture: run extraction, score **line recall** (found/expected), **line precision** (real found/found), **field accuracy** on quantity + category for matched lines. Names are matched fuzzily by the **token containment coefficient** (|A∩B| / min(|A|,|B|)) ≥ 0.6 — robust to the descriptor-word differences real product names carry ("Lean Ground Beef" vs "All Natural 93% Lean Ground Beef"); symmetric Jaccard wrongly penalized those. Print a table + aggregate; `EVAL_VERBOSE=1` lists every matched pair + unmatched line.
- **Targets: ≥ 90% recall, ≥ 90% precision, ≥ 85% field accuracy.** Below target → iterate the prompt, not the code. Screenshot the table for the README.

## 10. Build phases & acceptance criteria
1. **Skeleton + data** — solution, entities, EF/SQLite, Products CRUD. ✅ *Create a product, add a purchase, data survives restart.*
2. **Extraction pipeline** — `IReceiptExtractor` + Anthropic call, upload + review/confirm, alias write-back. ✅ *Real receipt round-trips to confirmed PurchaseEvents; re-upload pre-matches via aliases; bad image fails gracefully.*
3. **Prediction + dashboard** — engine + unit tests, Running Low + quick buttons. ✅ *All engine tests pass; dashboard reflects history.*
4. **Chat tools** — `IPantryChat` + 4 tools on the dashboard box. ✅ *"out of dog food, almost out of coffee" → two correct signals + a one-line confirmation.*
5. **Deploy + README** — Azure App Service (SQLite under `/home/data/`), README with Mermaid diagram, demo GIF, eval screenshot, the "statistics where statistics suffice" thesis. ◑ *Public URL works end-to-end; README presentable to a hiring manager.* — **README ✅ done + pushed** (demo GIF + `/accuracy` screenshot + live-demo URL still placeholders); **Azure deploy deferred** pending Jordan's account (see CLAUDE.md).

**Stretch (only after Phase 5):** GitHub Actions deploy; daily email digest; Walmart *catalog search* deep links. **Never:** checkout automation.

## 11. Cost & config
One `appsettings` section: `Llm: { Provider, ExtractionModel, ChatModel, MaxImageEdgePx }`. Receipts ~1–2k tokens on Haiku → single-digit dollars total. Set a monthly provider spend cap anyway.

## 12. Explicitly out of scope (do not build)
Auth/accounts · multi-user · mobile apps · push/SMS/email (digest is stretch-only) · barcode scanning · budgeting / price-comparison / deal-hunting · retailer checkout automation · background OCR queues · Docker/K8s.

> **Deliberate scope decisions (pulled in during the build).** Both were scoped out at the start to protect the timebox, then consciously brought in when they proved high-value and low-cost given what the pipeline already captured. Judgment calls — promoting a backlog item once the data made it nearly free — not scope slips.
> - **Spend insight / price history** — the **Trends** page (§8) + Product Detail price chart: per-item price movement, spend totals, and a next-month forecast, all derived from data already sitting on confirmed receipts. Cheap to add (the price data was already there), high personal value (where the grocery money actually goes). Still deliberately bounded — informational only: no budgeting/limits, no price-comparison or deal-hunting.
> - **Recipes / meal planning** — shipped as the inventory-aware **Recipes** feature (§8): recipes from what you have + what to grab, not a generic generator, so it stays on-mission.

---

## 13. Quantity on hand (v4.0 — spec, not yet built)

**Why it exists.** §6 models *flow* — purchases in, a learned rhythm out. A backed-up pantry/freezer is
a *stock* problem, and the household's actual goal is to answer **"do we have it?" without walking to the
garage freezer.** A rhythm can never answer that; a count can. Counts normally fail because nobody logs
the decrements — but ShelfAware already automates the hard half: a confirmed receipt is an exact, dated
`+N` with dedupe detection and an undo behind it.

**Opt-in per product** (`Product.TrackQuantity`, default **false**). The hoard is ~30 items — freezer
meat, canned goods, bulk buys. Every other product keeps running on §6 exactly as it does today. A
feature that demands you count the salt is dead inside a week, and §13.7 is how you pick the 30.

### 13.1 What a quantity is
- **Packages, not volume.** `QuantityOnHand` is a `decimal`, and it counts *containers*. Four milk means
  four jugs, two of which may be gallons — consistent with the standing no-unit-arithmetic rule (§4, and
  the dominant-size model in CLAUDE.md). **The UI must say "packages" plainly**; an unqualified "4" reads
  as a claim about volume, which would be a lie.
- **Decimal because weight items are already fractional.** `PurchaseEvent.Quantity` carries 2.34 for
  2.34 lb of ground beef. For those the count is fractional *in the item's own unit*, which is meaningful
  — not "2.34 packages of beef". Display follows `Product.DefaultUnit`: unit-less items render whole
  ("4 packages"), weight items render in their unit ("2.34 lb"). No normalization between the two, ever.
- **`QuantityCountedAt` is an attestation date, not a modification date.** Only a human act (setting the
  count, confirming a prompt, tapping a correction) advances it. Automated `+`/`-` move the number and
  leave the date alone — that gap is precisely what §13.5 measures.

### 13.2 Receipts move the count by the quantity actually bought
- **The one confirm path owns the `+`.** `ReceiptConfirmationService` adds **the confirmed line's own
  quantity** for any product with `TrackQuantity` — buying three cans adds 3, never 1. For a weight item
  that quantity is a weight, so it adds 2.34 in the item's own unit (§13.1). No second path (the
  standing rule).
- **Removal subtracts the same amount it added.** v3.9's `ReceiptRemovalService` is the confirm's
  inverse and has to stay exactly that: a removed receipt's line quantities come back off the count,
  or a removed duplicate leaves the count permanently inflated — the very error that service exists to
  undo.
- `add_purchase` (chat/manual) increments by its quantity the same way.
- **Symmetry is the invariant worth testing:** confirm-then-remove must return every affected count to
  the number it started at, for count and weight items alike.

### 13.3 Decrements
- **"Ate it" auto-decrements each MAIN ingredient's matched product by one package.** Recipe quantities
  are free-form strings by deliberate design ("2 lbs", "3 cloves"), so the app cannot know you used half
  a package and **must not start parsing them** — that would re-open a locked decision.
- **For a weight item, "one package" is the household's typical package, not a round number.** Deducting
  1 lb would be arbitrary — a pound is not a unit of anything about how this household buys. Instead:
  **the median of that product's per-purchase quantities**, so a household whose ground beef arrives in
  1.24 lb packs deducts 1.24. Two details this depends on:
  - **Per-PURCHASE, not the trip-summed median.** v3.5 sums same-day lines into a trip's worth for buy
    recommendations (3 Gala + 3 Honeycrisp → buy 6); that is deliberately a different number. Two packs
    of beef in one trip is 2.48 lb of *shopping* and 1.24 lb of *package*, and a decrement is about the
    package. Don't reuse the estimator's median here.
  - **Median, not mode.** Continuous weights rarely repeat exactly (1.24 vs 1.26 is the same package in
    practice), so "most common" isn't well defined for them, and the app is median-based throughout.
    Fallback ladder when there's no history to take a median of: the single known purchase quantity → 1.
- **This is approximate, and it fails SAFE.** Using half a package still costs a whole one, so the count
  reaches zero early and you rebuy early — the same direction as the app's existing safe-side rounding
  (intervals floor, buy quantities ceil). What it must never be is silent: the "Ate it" tap **shows what
  it is about to decrement and lets it be corrected there.**
- `set_quantity(product_name, quantity, relative?)` — the chat/voice tool. Absolute by default; with
  `relative: true` the number is a delta ("used two" → `-2`). Either way it is a human act, so it
  advances `QuantityCountedAt`.
- One-tap decrement on the dashboard/product card.
- **Decrements are household-wide** — tenancy already guarantees this, and it is the point: two people
  cook, one count. It is also the largest real risk to accuracy (an unlogged "took the last can" leaves
  the count wrong in the direction that stops a rebuy), which is the whole argument for making every
  decrement path above as cheap as possible, and for §13.5 running by default.

### 13.4 Zero — the load-bearing rule
An **asserted** zero and a **derived** zero are different facts and must not be conflated.
- **Asserted** (a human sets 0, taps Gone, or says "we're out") → writes a real `OutNow`
  `InventorySignal` → feeds the burn-rate rhythm (§6.3) like any other outage. This is *better* evidence
  than the button ever was: it is dated by the act of running out rather than by remembering to report it.
- **Derived** (a count that reached 0 purely through automated decrements) → **writes nothing.** It
  raises one confirmation — "looks like you're out of chicken — right?" — and only the tap writes the
  signal. Left alone it stays a hypothesis: the suppression in §13.5 simply lifts and §6 resumes.
- **The principle, third instance.** `writeAliases` is set only by human-confirmed receipts;
  `Receipt.VerifiedForEval` can never be machine-set; a derived zero can never mint an `OutNow`.
  **Machine inference never becomes ground truth without a human touch** — here it also protects the
  standing rule that nothing may fire a fake `OutNow` into the cadence engine.

### 13.5 How the count meets the engine
- **A positive count suppresses the buy recommendation**, it does not rewrite the prediction. Real
  evidence beats a learned guess, so a counted product with stock stays off the list.
- **Suppression is always visible, never silent.** The item shows with its reason — "you have 3, counted
  Jul 28" — and a one-tap correction. An item that vanishes from a list without explanation is how a
  household stops trusting the app.
- **Threaded flag, inert by default** — `honorQuantity`, mirroring §6/v3.6's `honorExpirations`. A
  forgotten call site then ignores counts (a visible gap) rather than suppressing recommendations
  nobody asked it to suppress. Do not "fix" the default.
- **The rhythms audit the count.** This is what keeps a count from rotting unnoticed:
  **expected exhaustion = `QuantityCountedAt` + (driving median × `QuantityOnHand`)**. Past that date
  with the count still positive, the app asks once — "you counted 3 in March and one usually lasts ~9
  days; still have them?" The engine **never silently corrects a count**; it only ever raises a question.
  This is the answer to "an inventory decays": the drift is detected instead of assumed away, and the
  cost of being wrong is one tap, not a re-census.
- **The backtest stays count-blind**, exactly as it stays expiration-blind — it grades the learned
  rhythm, and a human-entered fact overwriting `DueDate` would be grading itself.
- **Untracked products are untouched.** No count, no suppression, no drift check — §6 verbatim.

### 13.6 Data
Additive, default-valued columns on `Product` → `AdditiveSchema` migrates live DBs on boot; **no fresh
DB** (unlike v3).
```
Product   + TrackQuantity (bool, default false)      # opt-in; false = today's behaviour exactly
          + QuantityOnHand (decimal?)                 # packages, or the item's own unit for weight items
          + QuantityCountedAt (DateTimeOffset?)       # last HUMAN attestation (§13.1), not last change
```

**`QuantityFormat.Describe` (Core/Shopping) already exists — use it, don't write a second one.** §13.1's
display rule is built: it labels a quantity with `Product.DefaultUnit` when the product declares one
("2.34 lb") and prints a bare number when it doesn't ("4"). **Null means UNKNOWN, never "packages"** —
`PurchaseEvent.Quantity` is a package count for a counted item and a *weight* for a weight item, so
"2.34 packages" of beef would be a confident lie where "2.34" is merely incomplete, and most products
have no unit set. The backlog check's Qty column runs through it now; the count's own display must too,
or the two surfaces drift the way the due dates did. Format is `0.##`, matching the recommended-quantity
displays — `0.#` silently rounds 2.34 lb to 2.3, which a test pins.
No change-log table in v4.0: purchases and `MealEvent`s are already dated, so every automated path is
self-documenting and only manual edits are unrecorded. Add the log if the household ever needs to ask
"why does it say 3?" and can't answer it.

**Owed here: editing a purchase's quantity after the fact.** There is no way to correct one today.
`Quantity` is typed once in the Upload review grid (or set by `add_purchase`), and after confirm
nothing edits a `PurchaseEvent` — Product Detail's "Recent purchases" table is read-only, so the only
recourse is removing the whole receipt (v3.9) and re-uploading it, or adding a second purchase to
average out the mistake. Neither is "fix that number".

That was survivable while `StockUpFactor` capped at 3×. It stopped being, the moment the ceiling came
off (CLAUDE.md item 19): a quantity misread on a receipt you already confirmed now stretches the due
date in proportion — bounded at `MaxProjectionDays`, but with no granular way to undo it. And the
counting feature makes it worse before it makes it better, because a wrong purchase quantity will now
also land in `QuantityOnHand` via §13.2's increment.

So this phase owns the edit. **One write path**, the way `SetExpirationAsync` is: it has to adjust the
count too, or the correction fixes the history and leaves the shelf wrong. Same lesson as §13.2's
confirm/remove symmetry — every road that moves a purchase quantity moves the count with it.

### 13.7 The backlog check — first, and free ✅ *built: "What's piling up"*
Pure Core (`BacklogSignals`), **no schema change and no data entry at all** — the signature is already
sitting in data the household collected for other reasons. Ships as a `/reports` preset beside the Gap
report. Its job is to name the handful of products worth turning `TrackQuantity` on for, which is what
makes every later phase cheap.

**Three conditions, all required:**
1. **≥3 distinct purchase dates** — at two, "never ran out" reads the same whether you're stockpiling or
   simply bought it twice.
2. **Zero completed burn cycles** — §6.3 never once paired a purchase with a following `OutNow`. Note
   this is *not* `BurnRateDays is null`, which is also true at ONE cycle; the count comes from
   `ReplenishmentPredictor.BurnCycles`, made public so there is exactly one definition of a cycle.
3. **Past the engine's `DueDate`** — §6 says it's time to rebuy this.

Together they make the report **the grocery list's skeptic**: everything on it is already on the buy
list, and this is the evidence some of it may not need to be. That's the v4.0 goal in miniature — stop
restocking what's already deep — and it's a claim neither condition makes alone.

**Condition 3 was added after measuring, and it is what makes the report mean anything.** On real data
conditions 1–2 alone flagged **26 of 27** regularly-bought products, because a household that rarely
taps `OutNow` leaves *everything* silent — the check was reporting an unused button as a pantry full of
backlogs. Being due needs no button, and cut the same household to **1**.

⚠️ **Condition 3 ASKS the engine; it must never re-derive the date.** The first build compared days
since the last buy against the rebuy median and called an item overdue that the product page was
calling **Stocked for another five days** — its last buy was ~1.5× the usual, so `StockUpFactor` had
stretched the due date. That is the app's own "you bought extra, it lasts longer" logic, which is
precisely what a backlog report must not override, and the hand-rolled median also missed the
dominant-size anchoring, outlier trimming, and restock handling that `Predict` already does. Two
surfaces of one app must not disagree about whether you need to buy something. Pinned by
`A_stock_up_that_pushed_the_due_date_out_is_respected`.

Same rule, second application: it **honours the household's expiration setting**, because the report's
entire claim is "the app says this is due", so it has to ask the question the dashboard asks. A first
version ran expiration-blind and made the page's own "the same number the dashboard shows" footnote
false for exactly the households that opted in. **The backtest is the deliberate opposite** and stays
blind — that one grades the learned rhythm, and a label is not something the rhythm predicted.

**Coverage is disclosed, not gated.** `BacklogReport.OutageCoverage` = what share of judgable products
have ever really closed a cycle. Below ~25% the report says so plainly ("this list is mostly reading
your buying pattern"), because at that point condition 2 is barely evidence — but the finding still
stands on condition 3, so hiding it would be the bigger lie. (Contrast PriceWatch, which *refuses*
below 3 items: there the math itself is meaningless, here only one input is weak.)

**Ranked by money committed** — spend already carries both how often you buy a thing and what it costs,
and it is one explainable number in dollars. (The earlier "suspicion × spend" was dropped: a product of
two different units ranks fine and explains nothing, and every other number in Reports is arithmetic a
reader can follow.) Trips break ties as the evidence column.

**Meals are reported, never scored.** "Cooked with" counts recent `MealEvent`s whose recipe lists the
item as a main ingredient — evidence it's moving. It doesn't suppress a finding: the meal log only sees
cooking that went through a saved recipe, so its absence proves nothing.

**It says "worth checking", never "you have 6"** — the same honesty rule as Waste watch, and for a real
reason: none of the three conditions is proof. A missing `OutNow` may only mean nobody taps the button,
and a rhythm that stopped may only mean tastes changed.

**Sample data:** the demo seeder's "Beef Chuck Roast" is the hoard hero — bought one at a time, then a
six-at-once freezer trip, then silence long past even the sixfold stretched projection, with nothing
ever marked out (its dates are load-bearing; see CLAUDE.md item 19). It exists because
every other seeded household is well behaved, so this report had no sample data showing the case it was
built for. `Seeds_a_hoard_hero_so_the_backlog_check_has_something_to_find` asserts the seed really reads
as a hoard once it's been through the engine, not merely that the rows exist.

⚠️ **Known limit: a FIRST-EVER bulk buy is invisible here, by construction.** Buy a quarter cow once and
there is one purchase, no rhythm, no due date, and nothing to have gone quiet against — the report needs
`MinPurchases` buys before it can say anything at all. That isn't a bug to fix in this report: with a
single purchase there is no behavioural signal to read, and the only thing that would see it is the
quantity on the purchase itself. **That case is exactly what `TrackQuantity` (§13.1–13.6) exists for**,
which is worth remembering when judging whether the counting feature earns its keep — the report covers
the repeat-buy pattern, counting covers the one-off pile, and neither substitutes for the other.

**Where the preset loads live, and why.** `ReportDataService.LoadBacklogAsync` / `LoadGapRowsAsync` /
`LoadLabelOutcomesAsync` — not the page. All three used to open their own `DbContext` inside
`Reports.razor`, which made the docstring on that service ("the one place reporting touches the
database") false and put the joins somewhere no test could reach. **The due-date bug lived in exactly
that layer and shipped past 714 green tests**; nothing but noticing two screens disagreeing would have
caught it. Moved down, they're covered by `ReportDataServiceTests` on the same real-EF-on-SQLite
harness as everything else in `ShelfAware.Web.Tests` — no new packages, no auth harness, no browser.
The page keeps only what is genuinely UI: which preset is open, and whether expiration tracking is on.

### 13.8 Shelf-photo census (later phase)
The intake answer for stock that receipts can never know about — bought pre-app, bought elsewhere,
gifted, bulk. Reuses the extraction shape end to end: photo → candidate items with confidence → the
review-grid pattern → confirm. Three photos of a freezer beats reading thirty items aloud.
- ★ **It must never create `PurchaseEvent`s.** You did not buy those today, and invented purchases would
  poison every rhythm in the app. A census writes products (if new) and an attested count — nothing else.
  Census input and purchase input are different doors.
- Honest limits, designed for rather than papered over: occlusion (the back row), stacking (one visible
  can may be five), and unlabeled freezer parcels. The photo **proposes** a front-row count; the human
  corrects it. Still an audit, not an authoring session.

### 13.9 Considered and rejected
- **Barcode scanning stays out of scope (§12).** Fast per item but still linear in item count, patchy
  US store-brand UPC coverage, useless for produce, meat in freezer bags, and anything repackaged — and
  the deeper objection is that a UPC *is* a brand+size identity, the exact axis `Product` is
  deliberately not keyed on (§4).
- **Coarse "depth" levels** (none/low/have/deep) — subsumed by a real count, and shipping both would be
  two truths about the same question. Uncounted products simply keep §6's derived in-stock/run-out state.
- **Unit arithmetic / normalized volumes** — re-opens a locked decision, and packages answer the
  household's question already.
