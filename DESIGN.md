# Shelf Aware — Design Document
*LLM-powered pantry replenishment tracker. Display name: **Shelf Aware**. Repo/solution/namespace: `ShelfAware`.*

**Author:** Jordan Curran · **Status:** Approved for build · **Target:** 2 weekends, hard cap

---

## 0. Instructions to the AI coding assistant

You are implementing this spec. Follow these rules:

1. **Do not expand scope.** No authentication, no multi-user, no mobile app, no notification infrastructure, no third-party retailer integration (no Walmart). If a feature is not in this document, ask before adding it.
2. **Prefer boring solutions.** Minimal packages, no speculative abstractions, no microservices, no CQRS/MediatR. One solution, three projects (see §3).
3. **Keep all LLM interaction behind interfaces** (`IReceiptExtractor`, `IPantryChat`) so the provider is swappable and the rest of the app is testable without API calls.
4. **The prediction engine must be pure, deterministic C#** with unit tests. No LLM calls in the prediction path.
5. Build in the phase order of §10. Each phase has acceptance criteria; do not start the next phase until they pass.

---

## 1. Product summary

A single-user web app that answers one question: **"What am I about to run out of?"**

- User photographs a grocery receipt → LLM extracts and normalizes line items into structured purchase events (after a human confirm step).
- A deterministic engine computes each product's typical repurchase interval and predicts run-out dates.
- Dashboard shows a "Running Low" list. A natural-language quick-update box ("we're out of dog food, almost out of coffee") adjusts state via LLM tool calls.

**Design principle (say this in the README):** LLMs are used where language understanding is genuinely required — parsing messy receipt text and interpreting natural-language updates. Replenishment prediction is plain statistics, because statistics suffice there.

## 2. Tech stack

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS) | Fall back to .NET 8 only if local tooling requires |
| Web/UI | Blazor (Interactive Server) | Single project, C# end-to-end, 3 pages total |
| Data | EF Core + SQLite | Single file DB; no migrations drama |
| LLM abstraction | `Microsoft.Extensions.AI` (`IChatClient`) | Provider-agnostic seam |
| LLM provider | Anthropic Messages API | Extraction model: `claude-haiku-4-5-20251001` (cheap, vision-capable). Config-switchable to `claude-sonnet-4-6` if extraction quality on hard receipts demands it. Pin versioned IDs; never aliases. |
| Orchestration | Semantic Kernel **(Option A, preferred)** for tool/function calling; manual tool-call loop over `IChatClient` **(Option B)** if SK friction exceeds 2 hours | Resume signal favors A; shipping beats purity |
| Secrets | `dotnet user-secrets` locally; App Service settings in Azure | Never commit keys |
| Hosting | Azure App Service (Linux, F1/B1) | SQLite file under `/home/data/` (persisted) |
| Tests | xUnit | Prediction engine + extraction eval harness |

## 3. Solution layout

```
ShelfAware.sln
  src/ShelfAware.Web/          # Blazor app: pages, components, DI wiring
  src/ShelfAware.Core/         # Domain: entities, prediction engine, interfaces (no LLM, no EF)
  src/ShelfAware.Llm/          # IReceiptExtractor + IPantryChat implementations, prompts, schemas
  tests/ShelfAware.Tests/      # xUnit: prediction engine unit tests
  tests/ShelfAware.Evals/      # Console eval harness for extraction accuracy (see §9)
  DESIGN.md                 # this file
```

## 4. Data model (EF Core entities)

```
Product
  Id (int PK)
  Name (string, canonical e.g. "Great Value Whole Milk")
  Category (enum: Dairy, Meat, Produce, Pantry, Frozen, Beverage,
            Household, PetCare, PersonalCare, Other)
  DefaultUnit (string?, e.g. "gal", "lb", "count")
  IsTracked (bool, default true)        # user can untrack one-off buys

PurchaseEvent
  Id (int PK)
  ProductId (FK)
  PurchasedAt (DateOnly)
  Quantity (decimal, default 1)
  Source (enum: Receipt, Manual, Chat)
  ReceiptId (FK?, nullable)

Receipt
  Id (int PK)
  Merchant (string?)
  PurchasedAt (DateOnly?)
  ImagePath (string)                    # stored under /home/data/receipts/
  RawModelJson (string)                 # full extraction output, kept for audit/debug
  Status (enum: PendingReview, Confirmed, Discarded)

ReceiptLine
  Id (int PK)
  ReceiptId (FK)
  RawText (string)                      # verbatim from receipt
  NormalizedName (string)
  Quantity (decimal)
  UnitPrice (decimal?)
  Category (enum, same as Product)
  Confidence (decimal 0–1)
  ProductId (FK?, set at confirm time)

ProductAlias                            # deterministic repeat-matching memory
  Id (int PK)
  Merchant (string)
  RawText (string)                      # unique with Merchant
  ProductId (FK)

InventorySignal                         # explicit user statements
  Id (int PK)
  ProductId (FK)
  SignaledAt (DateTimeOffset)
  Kind (enum: OutNow, RunningLow, ShelfAwareed)
```

**Alias flow:** before sending lines to the LLM for normalization, match `(Merchant, RawText)` against `ProductAlias`. Pre-matched lines skip LLM normalization (cheaper, deterministic). On user confirm, write/refresh aliases.

## 5. Receipt extraction (the AI centerpiece)

**Flow:** Upload page → client-side resize (longest edge ≤ 1568px, JPEG q≈80) → `IReceiptExtractor.ExtractAsync(images)` → render editable review table → user corrects/confirms → persist `PurchaseEvent`s + aliases.

**A receipt = one or more images.** Photographed paper receipts are usually one image; long digital order pages (e.g. a Walmart online order) span multiple screenshots — send all images for one receipt in a single extraction call and merge into one line list. **Print-to-PDF order pages are also accepted:** pass the PDF to the model directly as a document content block (the Anthropic API ingests PDFs natively — no rasterizing, no resize step); trailing pages with barcodes/payment blocks are noise the prompt rules already discard.

**Output contract (JSON Schema the model must satisfy):**

```json
{
  "type": "object",
  "required": ["merchant", "purchase_date", "lines"],
  "properties": {
    "merchant": { "type": ["string", "null"] },
    "purchase_date": { "type": ["string", "null"], "description": "ISO 8601 date" },
    "lines": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["raw_text", "normalized_name", "quantity", "category", "confidence"],
        "properties": {
          "raw_text":        { "type": "string" },
          "normalized_name": { "type": "string" },
          "brand":           { "type": ["string", "null"] },
          "quantity":        { "type": "number" },
          "size":            { "type": ["string", "null"], "description": "e.g. '1 gal', '12 ct'" },
          "unit_price":      { "type": ["number", "null"] },
          "category":        { "type": "string", "enum": ["Dairy","Meat","Produce","Pantry","Frozen","Beverage","Household","PetCare","PersonalCare","Other"] },
          "confidence":      { "type": "number", "minimum": 0, "maximum": 1 }
        }
      }
    }
  }
}
```

**Extraction system prompt (v1 — iterate in `src/ShelfAware.Llm/Prompts/`):**

> You extract structured data from grocery receipt images. Rules:
> 1. Output ONLY JSON matching the provided schema. No prose, no markdown fences.
> 2. `raw_text` must be the line VERBATIM as printed, including abbreviations.
> 3. `normalized_name` is the short canonical pantry name. For paper-receipt abbreviations, EXPAND (e.g. "GV WHL MLK 1GAL" → "Great Value Whole Milk"). For verbose digital product titles, COMPRESS (e.g. "Great Value Whole Vitamin D Milk, Gallon, 128 fl oz Plastic Jug" → "Great Value Whole Milk"). Put package size in `size`, not in the name.
> 4. Do NOT invent items. Skip non-product lines (subtotal, tax, coupons, loyalty discounts, fuel points).
> 5. A quantity prefix like "2 @ 3.99" means quantity 2, unit_price 3.99. On digital invoices where a line shows "Qty N" and a single price, that price is the LINE TOTAL — set unit_price = price ÷ quantity.
> 6. Weight-priced items (e.g. "2.31 lb @ 1.99/lb"): quantity = weight in the printed unit; record the unit in `size`.
> 7. `confidence` reflects YOUR certainty in the normalization, not OCR legibility alone. Use < 0.6 when guessing.
> 8. If the image is not a receipt, return `{"merchant": null, "purchase_date": null, "lines": []}`.
> 9. Input may be a photographed paper receipt OR screenshot(s) of a digital order page — handle both. On digital orders, ignore UI chrome (buttons, "buy again", recommendations). If an item shows a substitution, record the item actually received.

**Robustness:** validate model output against the schema with `System.Text.Json`; on failure, retry once with the validation error appended. Two failures → surface a friendly error, keep the image, mark receipt `PendingReview`.

## 6. Prediction engine (pure C#, `ShelfAware.Core`)

For each tracked `Product`:

1. Collect distinct `PurchaseEvent.PurchasedAt` dates, sorted. Collapse same-day events.
2. `< 2` events → status **Unknown** (shown as "still learning").
3. Intervals = day gaps between consecutive purchases. Use the **median** interval (robust to one vacation/stock-up outlier). `≥ 4` events → discard intervals > 3× median before re-computing (trim, then median).
4. `DueDate = LastPurchase + MedianInterval`.
5. Status:
   - **Overdue** — today > DueDate
   - **DueSoon** — today ≥ DueDate − max(3 days, 20% of MedianInterval)
   - **Stocked** — otherwise
6. `InventorySignal` overrides: `OutNow` → **Overdue** (pinned to top) until next purchase or `ShelfAwareed`; `RunningLow` → at least **DueSoon**; `ShelfAwareed` → **Stocked**, and treat as a purchase-equivalent date for the next interval calc.
7. Expose `PredictionResult { ProductId, Status, DueDate?, MedianIntervalDays?, Basis }` where `Basis` is a short human string ("bought 5×, ~every 12 days") for UI transparency.

**Unit tests required:** 2-event minimum, median vs outlier trim, each status boundary (±1 day), every signal override, same-day collapse.

## 7. Natural-language updates (tool calling)

Single-turn chat box on the dashboard. `IPantryChat.HandleAsync(userText)` runs a tool-calling loop (SK function calling, or manual loop) with these tools:

```
record_signal(product_name: string, kind: "OutNow"|"RunningLow"|"ShelfAwareed")
add_purchase(product_name: string, date?: ISO date, quantity?: number)
query_status(product_name?: string)        # null = return the Running Low list
create_product(name: string, category: Category)   # only when no fuzzy match exists
```

Rules for the chat system prompt: resolve product names against the existing product list (provided in context) with fuzzy matching; ask a clarifying question ONLY when two products are plausibly intended; multiple statements in one message → multiple tool calls ("out of dog food, low on coffee" → two `record_signal` calls). Reply to the user with a one-line confirmation of what changed. Model: same pinned Haiku ID.

## 8. UI — three pages, no more

1. **Dashboard (`/`)** — "Running Low" list (Overdue + DueSoon, signal-pinned first), each row: name, status chip, `Basis` string, [Bought today] and [ShelfAwareed] quick buttons. Below: chat quick-update box. Below: collapsed "everything else" table.
2. **Upload (`/receipt`)** — drop/upload image → spinner → editable review table (normalized name, qty, category, product match dropdown w/ "create new", confidence highlight when < 0.6) → [Confirm all].
3. **Products (`/products`)** — flat table: name, category, tracked toggle, purchase count, median interval. Inline rename.

Keep styling to a minimal clean stylesheet or default Bootstrap. **Zero effort on visual polish until Phase 4 is done.**

## 9. Eval harness (`tests/ShelfAware.Evals`)

Console app, run with `dotnet run --project tests/ShelfAware.Evals`.

- `fixtures/` holds 10 real receipts matching your actual input distribution — mostly digital-order screenshots, a few photographed paper receipts (a multi-screenshot order counts as one fixture) — + `expected/<name>.json` hand-labeled ground truth.
- For each fixture: run extraction, score **line recall** (found / expected), **line precision** (found that are real / found), **field accuracy** on quantity + category for matched lines (normalized-name match is fuzzy, ≥ 0.8 token similarity).
- Print a table + aggregate. **Targets: ≥ 90% recall, ≥ 90% precision, ≥ 85% field accuracy.** Below target → iterate the prompt, not the code.
- This folder is a README selling point: screenshot the output table.

## 10. Build phases & acceptance criteria

**Phase 1 — Skeleton + data (Sat W1, ~4h):** solution layout, entities, EF/SQLite, Products page CRUD. ✅ *Can create a product, add a manual purchase via temporary form, data survives restart.*

**Phase 2 — Extraction pipeline (Sun W1, ~6h):** `IReceiptExtractor` + Anthropic call, upload page, review/confirm flow, alias write-back. ✅ *A real photographed receipt round-trips to confirmed PurchaseEvents; re-uploading a similar receipt pre-matches via aliases; bad image fails gracefully.*

**Phase 3 — Prediction + dashboard (Sat W2, ~4h):** engine + unit tests green, dashboard with Running Low + quick buttons. ✅ *All engine tests pass; dashboard reflects seeded history correctly.*

**Phase 4 — Chat tools (Sat–Sun W2, ~3h):** `IPantryChat` + 4 tools wired to dashboard box. ✅ *"we're out of dog food and almost out of coffee" produces two correct signals and a one-line confirmation.*

**Phase 5 — Deploy + README (Sun W2, ~3h):** Azure App Service, SQLite under `/home/data/`, README with architecture diagram (Mermaid), demo GIF, eval table screenshot, "statistics where statistics suffice" paragraph, and the chip-clip mascot header gag ("It looks like you're running low on coffee. Would you like help with that?"). ✅ *Public URL works end-to-end; repo README presentable to a hiring manager.*

**Stretch (only after Phase 5):** GitHub Actions deploy; daily email digest via a scheduled job; Walmart *catalog search* deep links via their affiliate API. **Never:** checkout automation.

## 11. Cost & config

- Single `appsettings` section: `Llm: { Provider, ExtractionModel, ChatModel, MaxImageEdgePx }`.
- Expected spend: receipts are ~1–2k tokens each on Haiku — whole project lands in single-digit dollars. Set a monthly spend cap in the provider console anyway.

## 12. Explicitly out of scope (do not build)

Auth/accounts · multi-user · mobile apps · push/SMS/email infrastructure (digest is stretch-only) · barcode scanning · price tracking/budgeting · meal planning · any retailer checkout automation · background OCR queues · Docker/K8s.
