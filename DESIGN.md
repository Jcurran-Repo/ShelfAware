# Shelf Aware — Design Document
*LLM-powered pantry replenishment tracker. Display name: **Shelf Aware**. Repo/solution/namespace: `ShelfAware`.*

**Author:** Jordan Curran · **Status:** Approved for build · **Target:** 2 weekends, hard cap
*As-built deviations and environment notes live in [CLAUDE.md](CLAUDE.md).*

---

## 0. Instructions to the AI coding assistant
1. **Don't expand scope.** No auth, multi-user, mobile, notifications, or retailer integration. Not in this doc → ask first.
2. **Prefer boring solutions.** Minimal packages, no speculative abstractions, no microservices/CQRS/MediatR. One solution, three projects (§3).
3. **Keep all LLM interaction behind interfaces** (`IReceiptExtractor`, `IPantryChat`) — provider swappable, the rest of the app testable without API calls.
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
  src/ShelfAware.Llm/     # IReceiptExtractor + IPantryChat impls, prompts, schemas
  tests/ShelfAware.Tests/ # xUnit: prediction engine unit tests
  tests/ShelfAware.Evals/ # Console eval harness for extraction accuracy (§9)
  DESIGN.md               # this file
```

## 4. Data model (EF Core entities)
```
Product         Id · Name (canonical item, brand-stripped) · Category (enum below) ·
                DefaultUnit (string?) · IsTracked (bool, default true)
PurchaseEvent   Id · ProductId · PurchasedAt (DateOnly) · Quantity (decimal=1) ·
                Brand (string?) · Size (string?) · Source (Receipt|Manual|Chat) · ReceiptId (FK?)
Receipt         Id · Merchant (string?) · PurchasedAt (DateOnly?) · ImagePath ·
                RawModelJson (full extraction output, audit) · Status (PendingReview|Confirmed|Discarded)
ReceiptLine     Id · ReceiptId · RawText (verbatim) · NormalizedName · Brand (string?) · Size (string?) ·
                Quantity · UnitPrice (decimal?) · Category · Confidence (0–1) · ProductId (FK?, set at confirm)
ProductAlias    Id · Merchant · RawText (unique with Merchant) · ProductId   # deterministic repeat-match memory
InventorySignal Id · ProductId · SignaledAt (DateTimeOffset) · Kind (OutNow|RunningLow|Restocked)

Category enum: Dairy, Meat, Produce, Pantry, Frozen, Beverage, Household, PetCare, PersonalCare, Other
```
**Alias flow:** before LLM normalization, match `(Merchant, RawText)` against `ProductAlias`; pre-matched lines skip the LLM (cheaper, deterministic). On confirm, write/refresh aliases.

> Brand and Size are per-purchase metadata on both ReceiptLine and PurchaseEvent — the product is the brand/size-agnostic item, so the same item bought across brands/sizes rolls up. See CLAUDE.md for the matching + dominant-size prediction model.

## 5. Receipt extraction (the AI centerpiece)
**Flow:** Upload → client resize (longest edge ≤ 1568px, JPEG q≈80) → `IReceiptExtractor.ExtractAsync(images)` → editable review table → user confirms → persist `PurchaseEvent`s + aliases.

**A receipt = one or more images.** Paper receipts are one image; digital order pages can span several screenshots — send all images for one receipt in a single call and merge into one line list. **Print-to-PDF order pages too:** pass the PDF as a document content block (Anthropic ingests PDFs natively — no rasterizing/resize); barcode/payment pages are noise the prompt discards.

**Output contract** — strict JSON Schema: `merchant`, `purchase_date`, `lines[]`; each line `raw_text`, `normalized_name`, `brand?`, `quantity`, `size?`, `unit_price?`, `category` (enum), `confidence` (0–1). Validated server-side and in C#.

**System prompt** — the live prompt is an embedded resource in `src/ShelfAware.Llm/Prompts/`; iterate there, not in code. Key rules: output ONLY schema JSON (no prose/fences); `raw_text` verbatim; `normalized_name` = short canonical item — EXPAND paper abbreviations ("GV WHL MLK 1GAL" → "Whole Milk"), COMPRESS verbose digital titles, keep the item's distinguishing words, put size in `size` and brand in `brand`; don't invent items, skip non-product lines (subtotal/tax/coupons/loyalty/fuel); "2 @ 3.99" = qty 2, unit_price 3.99; digital "Qty N" + one price = line total → unit_price = price ÷ qty; weight-priced → quantity = weight, unit in `size`; `confidence` = certainty in the normalization (< 0.6 when guessing); non-receipt image → empty lines; handle paper OR digital, ignore UI chrome, record a substitution as the item actually received.

**Robustness:** validate output against the schema; on failure retry once with the error appended. Two failures → friendly error, keep the image, mark `PendingReview`.

## 6. Prediction engine (pure C#, `ShelfAware.Core`)
For each tracked `Product`:
1. Distinct `PurchasedAt` dates, sorted; collapse same-day events.
2. `< 2` events → **Unknown** ("still learning").
3. Intervals = gaps between consecutive purchases; use the **median** (robust to a stock-up outlier). `≥ 4` events → discard intervals > 3× median, then re-take the median.
4. `DueDate = LastPurchase + MedianInterval`.
5. Status: **Overdue** today > DueDate · **DueSoon** today ≥ DueDate − max(3 days, 20% of median) · **Stocked** otherwise.
6. `InventorySignal` overrides: `OutNow` → **Overdue** (pinned) until next purchase or `Restocked`; `RunningLow` → at least **DueSoon**; `Restocked` → **Stocked** + counts as a purchase-equivalent date for the next interval.
7. `PredictionResult { ProductId, Status, DueDate?, MedianIntervalDays?, Basis }` — `Basis` is a short human string ("bought 5×, ~every 12 days").

**Unit tests required:** 2-event minimum, median vs outlier trim, each status boundary (±1 day), every signal override, same-day collapse.

## 7. Natural-language updates (tool calling)
Single-turn dashboard box. `IPantryChat.HandleAsync(userText)` runs a tool-calling loop with:
```
record_signal(product_name, kind: OutNow|RunningLow|Restocked)
add_purchase(product_name, date?, quantity?)
query_status(product_name?)                 # null = return the Running Low list
create_product(name, category)              # only when no fuzzy match exists
```
Chat prompt: resolve names against the provided product list with fuzzy matching; clarify ONLY when two products are plausibly intended; multiple statements → multiple tool calls; reply with a one-line confirmation. Same pinned Haiku ID.

## 8. UI
Spec baseline was three pages — Dashboard (`/`), Upload (`/receipt`), Products (`/products`); the build added Grocery List, Trends, Product Detail, and Accuracy (CLAUDE.md). Dashboard = "Running Low" (Overdue + DueSoon, signal-pinned first), each row name / status chip / `Basis` / [Bought today][Restocked], plus the chat box and a collapsed "everything else" table. Upload = image → spinner → editable review table (name, qty, category, product-match dropdown w/ "create new", low-confidence highlight) → [Confirm all]. Visual polish deferred until after Phase 4.

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
5. **Deploy + README** — Azure App Service (SQLite under `/home/data/`), README with Mermaid diagram, demo GIF, eval screenshot, the "statistics where statistics suffice" thesis. ✅ *Public URL works end-to-end; README presentable to a hiring manager.* (Azure deferred — see CLAUDE.md.)

**Stretch (only after Phase 5):** GitHub Actions deploy; daily email digest; Walmart *catalog search* deep links. **Never:** checkout automation.

## 11. Cost & config
One `appsettings` section: `Llm: { Provider, ExtractionModel, ChatModel, MaxImageEdgePx }`. Receipts ~1–2k tokens on Haiku → single-digit dollars total. Set a monthly provider spend cap anyway.

## 12. Explicitly out of scope (do not build)
Auth/accounts · multi-user · mobile apps · push/SMS/email (digest is stretch-only) · barcode scanning · price tracking/budgeting · meal planning · retailer checkout automation · background OCR queues · Docker/K8s.
