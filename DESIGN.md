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

> **v4.0 adds an opt-in stock count to `Product`** (`TrackQuantity`, `QuantityOnHand`, `QuantityCountedAt`) — the first thing in the model that measures *stock* rather than *flow*. Spec + invariants in **§13** (built).

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

> **v4.0 (§13):** for a product with a tracked count, a positive count *suppresses* the buy recommendation (real evidence beats a learned guess) and the rhythms above take a second job — **auditing the count for drift**. The engine never edits a count and the backtest never sees one. A count is about **how many**, never about **whether they're still good** or **what someone just told us** — so it never silences an expiration label or a newer signal.

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
Spec baseline was three pages — Dashboard (`/`), Upload (`/receipt`), Products (`/products`); the build added Grocery List (`/list`), Trends (`/trends`), Product Detail (`/product/{id}`), Accuracy (`/accuracy`), Recipes (`/recipes`), and Count from a photo (`/pantry-photo`, §13.8) (CLAUDE.md). Dashboard = "Running Low" (Overdue + DueSoon, signal-pinned first), each row name / status chip / `Basis` / [Bought today][Restocked], plus the chat box and a collapsed "everything else" table. Upload = image → spinner → editable review table (name, qty, category, tags editor, product-match dropdown w/ "create new", low-confidence highlight) → [Confirm all]. Products grid = filters + an always-available **[Out]** button + a clickable **tag cloud** that filters the grid (deep-linkable `?tag=`). Grocery List = by aisle + copy/print + a manual **Extras** section. Recipes = won't-eat list, NL "what can I make?" suggestions grounded in on-hand products, saved recipes with "Ready to make"/"Missing items" badges, "Ate it", "Pick for me", and "Add missing to list". Visual polish deferred until after Phase 4.

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

## 13. Quantity on hand (v4.0 — built, §13.8's shelf-photo census included)

**Why it exists.** §6 models *flow* — purchases in, a learned rhythm out. A backed-up pantry/freezer is
a *stock* problem, and the household's actual goal is to answer **"do we have it?" without walking to the
garage freezer.** A rhythm can never answer that; a count can. Counts normally fail because nobody logs
the decrements — but ShelfAware already automates the hard half: a confirmed receipt is an exact, dated
`+N` with dedupe detection and an undo behind it.

**Opt-in per product** (`Product.TrackQuantity`, default **false**). The hoard is ~30 items — freezer
meat, canned goods, bulk buys. Every other product keeps running on §6 exactly as it does today. A
feature that demands you count the salt is dead inside a week: §13.7 is how you pick the 30, and the
count panel says so out loud for fast movers (`CountingAdvice`, ≤10-day rhythms — steering, never a
gate, because someone WILL try to count the milk and then blame the feature for the drift questions).

### 13.1 What a quantity is
- **Packages, not volume.** `QuantityOnHand` is a `decimal`, and it counts *containers*. Four milk means
  four jugs, two of which may be gallons — consistent with the standing no-unit-arithmetic rule (§4, and
  the dominant-size model in CLAUDE.md). **The UI must say "packages" plainly**; an unqualified "4" reads
  as a claim about volume, which would be a lie.
- **Decimal because weight items are already fractional.** `PurchaseEvent.Quantity` carries 2.34 for
  2.34 lb of ground beef (extraction prompt rule 6: a weight-priced line's quantity IS the printed
  weight). For those the count is fractional *in the item's own unit*, which is meaningful — not
  "2.34 packages of beef". No normalization between the two, ever. **That fractionality is also how the
  app tells the two kinds apart** — see §13.3, and note it is a stronger signal than any unit field,
  because it is written by the same path that writes the number.
  *Display* follows `Product.DefaultUnit` where a human has set one ("2.34 lb") and prints a bare number
  otherwise. Extraction never writes it (a weight-priced line's unit goes into the per-purchase `Size`),
  so it is human-entered: the manual add-a-product form at creation, and the count panel's unit box on
  Product Detail afterwards (`IPantryStore.SetDefaultUnitAsync` — added when "no editor exists" turned
  out to mean every receipt-imported weight item showed a bare number forever). A bare "2.34" is
  incomplete rather than wrong, which is why display can live with it and the decrement in §13.3 cannot.
- **`QuantityCountedAt` is the date of a LOOK, not of a change — and not every human act is a look.**
  A stated total ("we have 6") re-anchors it: the person saw the shelf. A **relative** move ("used
  two", the lists' one-tap "Used one") moves the number and leaves the clock alone: the person saw what
  they *took*, not the rows behind it, and letting deltas refresh the clock would renew a count's
  credibility forever for exactly the households that tap "Used one" most — §13.5's drift check would
  never fire for them. The one exception is a relative move that lands at ZERO (clamped included):
  taking the last package *is* seeing the shelf empty, so it stamps and asserts the out (§13.4).
  Automated `+`/`-` never touch the date — that gap is precisely what §13.5 measures.

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
- ⚠️ **Removal defers to a LOOK the human took after the confirm.** Confirm a duplicate (+3), recount
  the shelf (attest 6 — ground truth, phantom excluded), then remove the duplicate: subtracting past
  that look would overrule newer, better evidence, so when `QuantityCountedAt` postdates the receipt's
  `ConfirmedAt` (§13.6) the subtract is skipped for that product. This guard is only sound because
  §13.1 makes the clock mean exactly "a look": a **relative** move ("Used one") carries a duplicate's
  phantom stock *forward* rather than re-baselining, deliberately does not advance the clock, and so
  deliberately does not shield the count — its case needs the subtract, or the phantom survives and
  the buy list goes quiet about stock that never existed. Both directions are pinned. A receipt
  confirmed before `ConfirmedAt` existed carries NULL and subtracts exactly as it always did — erring
  toward one early rebuy, the app's safe side.

### 13.3 Decrements
- **"Ate it" auto-decrements each MAIN ingredient's matched product by one package.** Recipe quantities
  are free-form strings by deliberate design ("2 lbs", "3 cloves"), so the app cannot know you used half
  a package and **must not start parsing them** — that would re-open a locked decision.
- **For a COUNTED item, one package is exactly 1 — never a median.** A receipt line reading
  "Beef Chuck Roast × 6" is one purchase *of six*, not one purchase of a six-pack, so a median over
  per-purchase quantities returns 6 for a household that habitually buys six at a time and cooking one
  dinner would empty the freezer — which lifts suppression and puts the item straight back on the
  grocery list, the exact opposite of the feature's purpose.
- ⚠️ **The QUANTITIES are the discriminator, not `Product.DefaultUnit`.** A whole-number median means
  the numbers are counts, so one of them is 1; a fractional median means they are a continuous measure,
  so one package is that median. This is the same fact §13.1 gives as its reason for the decimal type —
  *"weight items are already fractional"* — used as the signal it is.
  **`DefaultUnit` was tried first and was wrong twice over.** At the time nothing populated it — its
  only writer was the manual add-a-product form with no editor afterwards, and extraction puts a
  weight-priced line's unit in the per-purchase **`Size`** (prompt rule 6) — so every receipt-imported
  product had it null (measured: **0 of 190** on the real dev database), which made the weight branch
  unreachable. And where it *is* set it can mislead: a product declaring `"each"` or `"ct"` with
  quantities `[6, 6, 6]` would take the median path and charge six for cooking one — the very bug the
  counted rule exists to prevent, arriving through the field meant to prevent it. The count panel now
  has a unit box (§13.1), but that changes nothing here: it stays a display label for
  `QuantityFormat.Describe` and nothing more, whoever writes it.
  *Residual limit, accepted:* a weight item whose median lands on a whole number (beef at exactly 2.00 lb
  every time) reads as counted and deducts 1. Continuous weights essentially never do that, and the
  alternative is trusting a field nothing writes. Pinned by
  `A_weight_item_whose_median_lands_whole_reads_as_counted`.
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
- ⚠️ **Which product a main ingredient means is `IngredientMatcher`'s question — the SAME one the ✓/🛒 mark
  on the row above asks.** Matching on `RecipeIngredient.MatchedProduct` alone (as the decrement first did)
  let a row show "you have this" — satisfied by an on-hand product of the same specific food, or by a
  curated "also works as" — while the tap beneath it decremented **nothing**, because the grounded link was
  null or named something else. Two rules for one question is the same screen-disagrees-with-engine fault
  §6's "one prediction, one story" directive exists for.
  It is also the **only** way a count on a product the recipe was saved BEFORE can ever be maintained:
  nothing back-fills `MatchedProduct` when a product appears, so census stock (§13.8) would otherwise have
  no automated decrement at all.
  Two consequences the looser matcher brings, both deliberate:
  - **The grounded link still wins outright** when `MatchedProduct` names a counted product. A human
    confirmed that pairing; it beats an inference.
  - **Ambiguity is ASKED, not guessed.** An ingredient can be covered by several counted products
    ("ground beef" by two cuts). Cooking one meal must not take a package off each, and picking one
    silently would be arbitrary — so such a main decrements none of them by itself and the tap opens a
    **tiny picker** (the swap-cloud styling): each candidate as a bubble with its current count, "pick
    what came off the shelf". Click-away answers "none of these" — no count moves, and the notice says
    which ingredients were left uncounted rather than letting the question vanish. One tap to answer,
    zero to decline; the meal itself is already recorded either way (tell-don't-ask holds).
  - ⚠️ **A grounded link to a product that exists UNCOUNTED also goes to the picker, even with one
    counted candidate.** The household pinned the recipe to the store pack; token-matching past it to
    the counted freezer stock is the app guessing *which* ground beef got cooked — the exact guess a
    tell-don't-ask decrement must not make silently. A grounded link naming a product that no longer
    exists at all is different: that link is STALE, and the automatic fall-through is the only way
    census stock (§13.8) is ever maintained, so it stays automatic. Both directions pinned.
    ⚠️ **Judged against the COMPLETE set of chosen products, which needs a second pass.** If another main
    is pinned to one of the candidates, that package is already being taken and this ingredient is covered
    by it — reporting it anyway put a product in the panel's "not touching these" list while the take list
    above was touching it. Grouped by ingredient name too, since a recipe may list one main twice.
  - **The grounded-link precedence lives in `IngredientMatcher.Covering`, not in the caller.** It returns
    the pinned product ALONE when it is on hand, so a pinned ingredient can never read as ambiguous and no
    caller re-implements the rule. `IsSatisfied` is defined as `Covering(...).Count > 0`, which is what
    makes the tick on a recipe row and the action taken on its behalf the same question asked once.
- **This is approximate, and it fails SAFE.** Using half a package still costs a whole one, so the count
  reaches zero early and you rebuy early — the same direction as the app's existing safe-side rounding
  (intervals floor, buy quantities ceil). What it must never be is silent — and "not silent" means
  **tell, don't ask**: the tap commits in one go, and the notice then says exactly what was taken
  (actual amounts, where each count landed, and any question the picker was left holding) with
  **↩ Undo** as the one-tap way back — picks included, since they land in the same take list. ✅ *built: `MealStock.Apply` reports the ACTUAL takes (clamp-aware, so the undo
  can never invent stock) and `MealStock.Restore` reverses precisely them; a recipe touching nothing
  counted gets a one-line notice.* A confirmation step was tried first and rejected by its own logic:
  asked on every cook of the same stew, it gets blown through unread, which protects nothing and
  costs every tap. `MealStock` (Web/Data) owns the resolution, the write and the restore — logic
  private to a page is logic no test can reach, the §13.7 lesson applied to the one path that changes
  a hand-maintained number unasked.
- `set_quantity(product_name, quantity, relative?)` — the chat/voice tool. Absolute by default; with
  `relative: true` the number is a delta ("used two" → `-2`). Only the absolute form re-anchors
  `QuantityCountedAt` — a delta is not a look (§13.1), except when it lands at zero.
- One-tap decrement **where the count is claimed**: the grocery list's suppressed row and the product
  page's count panel (both "Used one"). Deliberately **not** on the dashboard — it lists running-low
  items only, so a counted item appears there only once its count has *stopped* doing work (stale,
  pinned, or due by label), and at that point the useful act is re-attesting or asserting zero, not a
  decrement. Both of those live on the product page's panel one tap away from the card, beside the
  staleness sentence that makes them safe; a bare count control on the card would duplicate the
  surface without its explanation.
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
- **A count answers "how many", so it may only silence a recommendation that rests on "how many".**
  Four things stand it down, each for its own reason:
  - an explicit `OutNow` — a count is a memory of a past look, an outage is a statement about now;
  - an **expiration label** (§6/v3.6's cap, and an expired pin) — how many you have says nothing about
    whether they're still good, and v3.6's cap is escalate-only. Silencing it would delete exactly the
    warning that arrives *before* the food dies, and the household would first hear about the milk the
    day after. Consequence worth stating: a counted item with a live label is explained by its **label**
    rather than its count, which is the honest order — the label is the binding constraint;
  - a **`RunningLow` tapped since the count** — newer human evidence beats older human evidence, the
    same argument the outage makes. (One tapped *before* the count loses to it, or a single old tap
    would disable counting forever.);
  - a **stale count**, below.
- **The rhythms audit the count.** This is what keeps a count from rotting unnoticed:
  **expected exhaustion = `QuantityCountedAt` + (days-per-package × `QuantityOnHand`)**, where
  **days-per-package = driving median ÷ the typical trip quantity**. Past that date with the count still
  positive, the app asks once — "you counted 3 in March and one usually lasts ~9 days; still have them?"
  The engine **never silently corrects a count**; it only ever raises a question.
  ⚠️ **The driving median is days-per-TRIP, not days-per-package**, and conflating them is a silent
  failure: a household buying six at a time on a 60-day rhythm gets a 360-day horizon instead of 60, so
  the drift check never fires for exactly the bulk buyers §13 exists for. That divisor is not a new
  assumption — it is the *same* proportionality `StockUpFactor` already asserts when it stretches a due
  date by (this buy ÷ typical trip). One reading of what the median measures, or two rules in one file
  contradict each other. Pinned by `TheDriftHorizon_ReadsTheMedianAsOneTripsWorth_NotOnePackage`.
  This is the answer to "an inventory decays": the drift is detected instead of assumed away, and the
  cost of being wrong is one tap, not a re-census.
- **`CountConfidence` governs how a count is STATED, not what it is** — `Counted` / `Aging` / `Spent`.
  There is one stored truth (the number, and the date a human vouched for it); confidence decides whether a
  surface may **assert** it ("4 on hand") or must **attribute** it ("you counted 9 on Mar 12"), which is
  still true when the first form has become a lie. One enum, because "why did we stop trusting it" and "how
  much do we trust it" are the same fact seen twice; `CountLooksStale` is derived from it so the two cannot
  drift.
  ⚠️ **A low-confidence count is NOT banded by depth.** "Plenty" vs "nearly out" needs a consumption rate,
  and the `Aging` case is *defined* by not having one — elapsed time says nothing about how much got eaten.
  Guessing a smaller number would be the confident lie the whole feature exists to avoid. Only `Spent`,
  which by definition has a rhythm, may add a depth claim ("by its rhythm they'd be gone").
  This is also what lets §13.9's rejection of coarse depth levels stand: a band here is not a second truth
  about the pantry, it is an honest rendering of the first one's reliability.
- ⚠️ **A count with NO rhythm is asked about on AGE alone — 90 days.** An item with 0 or 1 purchases has
  nothing to project exhaustion from, which is exactly the shape of stock bought before the app, bought
  elsewhere, gifted, or in one bulk run (§13.8). Without this the drift check simply wouldn't apply, so
  the count would be trusted **forever** on the one population no receipt will ever correct — the longest
  trust given to the weakest evidence. The threshold is a judgement and has to be: long enough not to nag
  a freezer hoard that genuinely lasts a season, short enough that a count can't outlive the food.
  `CountStaleReason` (`PastItsProjection` / `Unattested`) tells a surface which finding it has, because
  the two need different sentences — only one of them has a rhythm to have outlived. No date is invented
  for the age case: `CountRunsOutOn` stays null rather than implying a projection the engine can't make.
- **Staleness is a question about the NUMBER'S AGE, so it applies to a count of zero too.** Gating it on
  a positive count left a stale zero deciding outright while a stale positive deferred — one fact treated
  two ways. Suppression separately still needs a positive count: a zero has nothing to hold back.
- **A fresh count decides recipe stock outright, in both directions** (`PantryOnHand`). Same principle —
  real evidence beats a learned guess — and it is the ONLY way a count reaches makeability for stock with
  no purchase history, since such an item never leaves `Unknown` and reading status alone left its number
  as decoration: a counted 12 added nothing and a counted 0 removed nothing. A stale count defers back to
  the rhythm. ⚠️ A zero withholding an item here is a DISPLAY inference and leaves §13.4 untouched — the
  cost of being wrong is a red recipe row with a hint, not a false `OutNow` taught to the cadence engine.
- ⚠️ **…but a PINNED item is out, whatever the count says.** An expiration label and an explicit `OutNow`
  both beat the count for recipe stock exactly as they beat it for suppression, and for the same one-line
  reason: a count answers "how many", never "are they still good" or "what did someone just tell us".
  Letting the count decide unconditionally made recipes offer to cook with food the app knew was expired,
  and with food the household had just reported running out of — so the rule reads the ENGINE's pin rather
  than re-deriving the precedence. Pinned by `An_expired_counted_item_is_NOT_on_hand` and
  `A_counted_item_the_household_reported_OUT_is_NOT_on_hand`.
- **The backtest stays count-blind**, exactly as it stays expiration-blind — it grades the learned
  rhythm, and a human-entered fact overwriting `DueDate` would be grading itself.
- **Untracked products are untouched.** No count, no suppression, no drift check — §6 verbatim.

### 13.6 Data
Additive, default-valued columns on `Product` → `AdditiveSchema` migrates live DBs on boot; **no fresh
DB** (unlike v3).
```
Product   + TrackQuantity (bool, default false)      # opt-in; false = today's behaviour exactly
          + QuantityOnHand (decimal?)                 # packages, or the item's own unit for weight items
          + QuantityCountedAt (DateTimeOffset?)       # last LOOK at the shelf (§13.1), not last change
Receipt   + ConfirmedAt (DateTimeOffset?)             # when the confirm RAN (v4.1) — orders it against a
                                                      # later look (§13.2); NULL = pre-v4.1, subtract as ever
```

**Stop-counting is dormant, not destructive** — `TrackQuantity` goes false and the number + date STAY
(the v3.6 toggle semantics). Every reader gates on the flag, so the dormant pair renders nowhere and
influences nothing; the product page attributes it ("you counted 14 on Mar 12 — enter a number to start
again") instead of amnesia. Resuming still starts from a fresh count: the old number is stale by
definition.

**`QuantityFormat.Describe` (Core/Shopping) already exists — use it, don't write a second one.** §13.1's
display rule is built: it labels a quantity with `Product.DefaultUnit` when the product declares one
("2.34 lb") and prints a bare number when it doesn't ("4"). **Null means UNKNOWN, never "packages"** —
`PurchaseEvent.Quantity` is a package count for a counted item and a *weight* for a weight item, so
"2.34 packages" of beef would be a confident lie where "2.34" is merely incomplete, and in practice
**no** product has a unit set (0 of 190 on the real dev database — §13.1). This is the one place
`DefaultUnit` is still consulted, and it is display only; §13.3's decrement reads the quantities
instead. The backlog check's Qty column runs through it now; the count's own display must too,
or the two surfaces drift the way the due dates did. Format is `0.##`, matching the recommended-quantity
displays — `0.#` silently rounds 2.34 lb to 2.3, which a test pins.
No change-log table in v4.0: purchases and `MealEvent`s are already dated, confirms carry their own
moment since v4.1 (`ConfirmedAt`, above), and only manual edits are unrecorded. Add the log if the
household ever needs to ask "why does it say 3?" and can't answer it.

**Editing a purchase's quantity after the fact** ✅ *built: tap a quantity in Product Detail's "Recent
purchases"; `IPantryStore.SetPurchaseQuantityAsync` is the one write path.* There was no way to correct one.
`Quantity` is typed once in the Upload review grid (or set by `add_purchase`), and after confirm
nothing edits a `PurchaseEvent` — Product Detail's "Recent purchases" table is read-only, so the only
recourse is removing the whole receipt (v3.9) and re-uploading it, or adding a second purchase to
average out the mistake. Neither is "fix that number".

That was survivable while `StockUpFactor` capped at 3×. It stopped being, the moment the ceiling came
off (CLAUDE.md item 19): a quantity misread on a receipt you already confirmed now stretches the due
date in proportion — bounded at `MaxProjectionDays`, but with no granular way to undo it. And the
counting feature makes it worse before it makes it better, because a wrong purchase quantity will now
also land in `QuantityOnHand` via §13.2's increment.

**One write path**, the way `SetExpirationAsync` is, and it moves the count by the DIFFERENCE — a misread
12 corrected to 2 takes ten off the shelf as well as the history, because fixing one and not the other
just relocates the error. Same lesson as §13.2's confirm/remove symmetry.

Three calls the code can't say for itself:
- **Not an attestation.** The person is correcting what the RECEIPT said, not reporting what they can
  see, so `QuantityCountedAt` stays put and the staleness check keeps measuring from their last real look.
- **A non-positive quantity is refused, not clamped** (unlike the confirm path, which clamps ≤0 → 1 on
  machine-read lines). This number was typed on purpose; silently changing it is how the app starts
  disagreeing with the person who typed it. Removing a purchase entirely is the receipt's job.
- **The receipt's own line is left alone** — it's the audit copy of what was read, and a `PurchaseEvent`
  points at a receipt rather than a line, so a receipt with two lines for one product couldn't be updated
  unambiguously anyway. `/receipts` stays the record of the receipt; Product Detail is the record of the
  pantry.

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
⚠️ That hand-off was a **no-op** until §13.5 grew the two rules a rhythm-less count needs (recipe stock
decided by the count; staleness asked on age): a count on a single-purchase item previously did nothing
whatsoever, so the sentence above is load-bearing rather than rhetorical.

**Where the preset loads live, and why.** `ReportDataService.LoadBacklogAsync` / `LoadGapRowsAsync` /
`LoadLabelOutcomesAsync` — not the page. All three used to open their own `DbContext` inside
`Reports.razor`, which made the docstring on that service ("the one place reporting touches the
database") false and put the joins somewhere no test could reach. **The due-date bug lived in exactly
that layer and shipped past 714 green tests**; nothing but noticing two screens disagreeing would have
caught it. Moved down, they're covered by `ReportDataServiceTests` on the same real-EF-on-SQLite
harness as everything else in `ShelfAware.Web.Tests` — no new packages, no auth harness, no browser.
The page keeps only what is genuinely UI: which preset is open, and whether expiration tracking is on.

### 13.8 Shelf-photo census ✅ *built: "Count from a photo" (`/pantry-photo`)*
The intake answer for stock that receipts can never know about — bought pre-app, bought elsewhere,
gifted, bulk. Reuses the extraction **line shape** — photo → candidate items with confidence → the
review-grid pattern → confirm. Three photos of a freezer beats reading thirty items aloud.
- ⚠️ **Every item carries an EVIDENCE grade, and that is what keeps a photo honest.** A receipt is text:
  `raw_text` is either there or it isn't. A photo has no such floor — a freezer looks like a freezer, and a
  model asked "what's in here?" can produce a plausible pantry out of priors alone, every word invented. So
  `CensusItem.Evidence` says HOW the item was known: **`Label`** (printed text it could read, kept verbatim
  in `LabelText` — the census's `raw_text`, checkable against the photo in a second), **`Appearance`** (no
  legible label, recognised by sight — a bunch of bananas needs no barcode), or **`Unidentified`** (a package
  is there and it could not say what; the NAME then describes the package, not the food).
  Three of the contract's rules are **enforced in the parse rather than trusted to the prompt**, because a
  shelf photo's output cannot be checked against anything: a `Label` claim with no readable text is
  downgraded to `Appearance`; an `Unidentified` item's confidence is capped below the review grid's tick
  threshold and it may never carry a product match; and `visible_count` is floored at 1 — reporting an item
  means something was seen, and a zero surviving to the grid could be confirmed into an **attested** zero,
  which mints a real `OutNow` (§13.4). A machine's arithmetic must never mint one.
- **The grid ticks what it read and leaves what it guessed.** At or above **0.6** — the same threshold the
  receipt review grid highlights a low-confidence line at, deliberately not a second number — a row arrives
  ticked; below it the row is shown, styled low-confidence, with its reason visible and its box empty. A
  guess has to be opted into; a legible label or an unmistakable banana is not punished for having no
  barcode. `Tick all` / `Untick all` exist because thirty rows otherwise cost thirty taps — the guard is the
  default, not a lock.
  ⚠️ **Confidence is necessary and not sufficient, because a tick authorizes a WRITE.** Two more conditions
  hold, each because the number alone is about the wrong question:
  - **Never an `Unidentified` row**, enforced on the page and not merely implied by the reader's 0.3 cap.
    Leaving it implied put the rule in one constant in another assembly: raise that cap, or swap the
    reader, and rows literally named "foil-wrapped parcel" arrive ticked and become real products named
    after packaging.
  - **Never a row matched by name SIMILARITY.** Confidence states certainty in the ITEM — how sure the
    reader is this is peanut butter — and says nothing about *which* product a `ProductMatcher` pass then
    picked. Its substring rule lands "Peanut Butter" on a catalog's "Butter", and `Attest` REPLACES that
    product's count with no undo, so a flawless read must not pre-authorize an unscored guess at the
    target. The row says it was matched by similarity and waits to be looked at.
- ★ **It must never create `PurchaseEvent`s.** You did not buy those today, and invented purchases would
  poison every rhythm in the app. A census writes products (if new) and an attested count — nothing else.
- ⚠️ **What that output can and cannot do, measured before building any of it.** Census stock has 0 or 1
  purchases by construction, so it has no learned rhythm — and every use §13 makes of a count except one
  is gated on having one. What it does: **a fresh count decides recipe makeability** (§13.5), which is the
  census's real payoff and required teaching `PantryOnHand` to read the count instead of inferring stock
  from status. What it is asked about: **age alone, at 90 days** (§13.5's `Unattested` reason) — without
  which a census count would never be questioned at all.
  What it deliberately does NOT do:
  - **No buy-recommendation suppression, and that is correct.** A rhythm-less item is `Unknown` — "still
    learning" — so the app was never asking you to buy it and there is nothing to hold back. Suppression
    is for silencing a request, not for announcing stock.
  - **No exhaustion date**, hence no "you counted 3 in March and one lasts ~9 days" question. There is no
    rate to reason from and inventing one would be a projection the engine cannot make.
  - **"One package" is 1** for the `Ate it` decrement, since there is no purchase history to take a median
    from. Fine for cans; meaningless for a quarter cow — so the census review grid is the natural place to
    capture *how many packages*, because that IS the count.
  - ✅ **It CAN be decremented by cooking, since §13.3's matcher fix.** This was the missing prerequisite:
    the decrement used to match on `MatchedProduct`, which nothing back-fills when a product appears, so a
    census product was named by no saved recipe and no tap could ever touch it. Now it resolves through
    `IngredientMatcher` like the ✓ mark does, so a census item is maintained by cooking from the moment it
    exists. ⚠️ **How well that works is a function of how much cooking goes through a saved recipe** —
    measured on the real household at ~3–4 "Ate it" taps a week against ~537 purchases, so call it a third
    of meals. The count will therefore be *directionally* right and *precisely* wrong, which is exactly what
    `CountConfidence`'s attribute-don't-assert rendering is for.
  - ⚠️ **A second write path.** ✅ *built: `CensusConfirmationService`.* `ReceiptConfirmationService` is THE
    confirm path and it creates `PurchaseEvent`s, which the ★ rule above forbids. The census reuses the
    review-grid *UI* and needs its own persistence: products (if new) + `StockLedger.Attest`, nothing else.
    "Reuses the extraction shape end to end" was true of the line contract and false of the writing.
    Three calls inside it the code can't say for itself:
    - ⚠️ **Rows are SUMMED per product before a single `Attest`.** An attestation states a TOTAL, so
      attesting row by row would let a second row resolving to the same product silently overwrite the
      first — a household with five left believing they had two, and nothing on screen saying a number had
      been dropped. Two photos of one food, or two varieties of it, are exactly that case.
    - **A negative count is REFUSED, not clamped**, and reported back. `Attest` floors at zero, so a
      floored "-3" would land on an asserted out and write an `OutNow` off a typo — the same rule, for the
      same reason, as `SetQuantityAsync`'s refusal.
    - **An UNMATCHED row whose name already exists resolves to that product.** A census is the app's
      biggest bulk product creator and a twin splits purchase history, so this is the standing duplicate
      guard applied where it matters most — and it is also what makes a RETRY safe, which the failure
      message then promises: a confirm that commits and fails on the way back invites a second press, and
      without this that press would file a duplicate of every product the first one created.
      ⚠️ **"Unmatched" and "the human chose create-new" are different facts and the row must carry which**
      (`CensusRow.CreateNew`). Both arrive as `ProductId` 0, and collapsing them was a real bug: the
      fallback resolved an explicit create-new onto the same-named product and REPLACED its count — twelve
      packs silently becoming four, no new product, and a summary that said nothing unusual, while the grid
      said "new product". An explicit create-new whose name is taken is **refused and named** instead:
      merging overrules the human, and creating the twin is what the duplicate guard exists to stop, so the
      honest move is to decline and let them say which they meant. The grid says so *before* the confirm.
    - ⚠️ **A count of ZERO is refused for any product with NO PURCHASE HISTORY** — not merely for one the
      row would create. An attested zero writes a real `OutNow` (§13.4), and with no purchases behind it
      nothing can ever re-anchor or clear that signal: it pins the item **Overdue** at the top of the
      dashboard and the grocery list indefinitely, a later census counting it at three does not lift it,
      and it teaches nothing either (`BurnCycles` needs purchases to form a cycle). Drawing the line at
      *newness* looked equivalent and was not — **a census's own output has no purchases by construction**,
      §13.8 being precisely for stock no receipt knows about, so the second census of the same shelf walked
      into exactly the state the first one was refused for. A zero on a product the household actually buys
      is untouched: that one is §13.4's real evidence, and its rhythm is what can later contradict it.
    - ⚠️ **An EMPTY count box is not a zero.** The row carries a nullable count and a null is refused, never
      coerced: `@bind` on a non-nullable decimal turns a box cleared to retype into `0`, and a ticked `0` is
      an *asserted* out — a real `OutNow` against a shelf full of the stuff, from a field nobody typed in,
      with the blur beating the click so the zero is never even seen. Same rule, same reason, as the product
      page's count panel (§13.4's "a machine's arithmetic must never mint one", applied to a widget's).
    - **A row naming a product that has since VANISHED is refused, not redirected.** A merge or a delete in
      another tab while the grid sits open, and resolving by name instead would create a twin of the product
      that just went away, or land the count on a different product than the dropdown showed. The grid said
      where the row was going; a row that can no longer go there is the household's business.
    - **A NEAR-miss on a typed name is named on the grid, never resolved in the write.** The reader's match
      runs once, at read time, so it never sees a name the human enters afterwards — and "Name it if you
      know what it is" on an unidentified package invites exactly that. Fuzzy matching false-positives, so
      the service still resolves only on an exact name and the *grid* raises the near-miss for the household
      to settle. Same shape as the standing duplicate guard elsewhere: exact blocked, fuzzy asked.
    - **Resuming a DORMANT count is reported.** Stopping counting keeps the number and its date as history
      (§13.1); a census overwrites both and starts believing them again — right for someone who just counted
      the shelf, but it is the one switch they deliberately turned off, so the row shows the stored number
      beforehand and the summary names the resumption afterwards. `Retracked` is a different property
      (`IsTracked`) and would have stayed silent.
  Seeded now as `Quarter Cow Ground Beef` (a count, no purchases) so all of the above is demonstrable and
  tested before the photo half exists.
  Census input and purchase input are different doors.
- Honest limits, designed for rather than papered over: occlusion (the back row), stacking (one visible
  can may be five), and unlabeled freezer parcels. The photo **proposes** a front-row count; the human
  corrects it. Still an audit, not an authoring session. The page states all three out loud rather than
  letting the household discover them: photograph *different* areas or things get counted twice, only the
  front row can be seen, and counts are **packages** (§13.1 — an unqualified "4" would read as a claim
  about volume).
- **Nothing is persisted but the counts.** The photo goes to the model and stops there — no audit copy, no
  census table, nothing new for the export or "delete my data" to reach. It costs the receipt path's Retry
  (there is no saved copy to retry FROM) and loses an abandoned review, and both are cheap when you are
  standing at the shelf you just photographed. What it buys is that a photograph of the inside of someone's
  home never lands on disk, and that the feature adds no new tenant table to get wrong.

### 13.9 Considered and rejected
- **Barcode scanning stays out of scope (§12).** Fast per item but still linear in item count, patchy
  US store-brand UPC coverage, useless for produce, meat in freezer bags, and anything repackaged — and
  the deeper objection is that a UPC *is* a brand+size identity, the exact axis `Product` is
  deliberately not keyed on (§4).
- **Coarse "depth" levels** (none/low/have/deep) — subsumed by a real count, and shipping both would be
  two truths about the same question. Uncounted products simply keep §6's derived in-stock/run-out state.
  ⚠️ **Re-examined once maintenance turned out to be asymmetric** (§13.8: census stock gets no automated
  `+` and only partial `−`), because this rejection assumed a count stays maintainable and for that
  population it doesn't. It still stands, and the reason is sharper than before: a depth band would need a
  consumption rate, which is precisely what a rhythm-less item lacks — so it could only ever be a guess
  dressed as a reading. `CountConfidence` takes the honest half of the idea instead: the count's
  **reliability** is banded, never its depth, and one truth is rendered two ways rather than two truths
  shipped side by side.
- **Unit arithmetic / normalized volumes** — re-opens a locked decision, and packages answer the
  household's question already.
