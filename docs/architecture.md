# Architecture — the data model, the tenancy boundary, and the seams

Extracted from `CLAUDE.md` on 2026-09-19. This is the shape of the system: what a Product is, how a
household is isolated, and which interfaces exist so a layer can be swapped or faked. Read it before
adding an entity, a write path, or a provider.

---

## The tenancy boundary (the one thing to get right)

Every pantry entity implements `IHouseholdOwned`. `ShelfAwareDbContext` carries a per-instance
`HouseholdId` that drives a **global query filter on every table** (reads) and a `SaveChanges`
**stamp-and-refuse** step, `EnforceHousehold` (writes) — added rows are stamped when empty and
**refused** when they name another household; modified and deleted rows are refused when the entity's
household isn't the context's. The filter alone is not enough: EF builds updates and deletes from the
change tracker keyed on the PK, so no filter is ever consulted for them.

**`IHouseholdDbFactory` is THE way to a pantry context.** It is scoped, and pre-sets `HouseholdId` from
the scoped `ICurrentHousehold` (a `UseFixed` pin → the HttpContext claim → the circuit's auth state).
The raw `IDbContextFactory` is **bootstrap-only**.

**Two databases, deliberately.** `shelfaware.db` is the pantry; `auth.db` is Identity, operator data
(the error log, wishlist entries) and money (the credit ledger, tiers, API tokens). auth.db has **no**
query filter, so every query there hand-scopes its own WHERE — the `ApiTokenService` pattern. The
split is load-bearing: a household cannot reach operator space, "delete all my data" cannot destroy
money because the ledger is not in the context it operates on, and `EnsureCreated` builds the identity
schema anywhere with no migrations. The one real cost — the usage write and the ledger write cannot
share a transaction — is documented in `docs/subscription-plan.md` §4 as a bounded, logged,
best-effort pair. See `docs/remediation-plan.md` §8 for why merging them was considered and rejected.

**Cross-household reads are countable and gated.** There are four production `IgnoreQueryFilters`
sites, each admin-gated as its first statement, `AsNoTracking`, and read-only:
`AdminReportReader` (bug reports), `AdminReportReader.ListRecentActivityAsync` (the activity panel),
`AdminAiSpendReader` (the operator spend aggregate — aggregate-only, never returns household ids), and
the census resolve's column-scoped write. There is exactly **one** cross-household WRITE,
`ReportResolutionService`, which uses `IgnoreQueryFilters().Where(Id).ExecuteUpdateAsync` so no tracked
entity exists and it is structurally unable to touch another column. Anything new wanting
cross-household data makes its own case at review — do not reuse these by analogy.

**Every new tenant table walks the full drill,** and there is no reflection test enforcing it:
query filter + stamping, an isolation test, `AdditiveSchema.EnsureTable` + a schema-parity test,
export `data.json`, delete-my-data, and `CountAll`.

## The schema seam

`EnsureCreated` does **not** migrate. Post-v3 changes go through `AdditiveSchema`:

- **`EnsureColumn`** — idempotent `ALTER TABLE … ADD COLUMN` on startup, additive DEFAULT-valued
  columns only. ⚠️ Pin it with a **drop-COLUMN** parity test, not only the drop-TABLE one: the
  drop-table test rebuilds via `EnsureTable` with the column already present, so the ALTER branch —
  the path every live deployment takes — never runs.
  - It returns **true only on the boot that actually added the column**, which is the one safe moment
    to **backfill** the new column from an existing one (`Receipts.UploadedAt` from `ConfirmedAt`,
    2026-09-22). Still additive: nothing existing changes, and only the new column is written. The
    guard is the whole point — a backfill that re-ran on every boot would rewrite values the app has
    since written, so pin BOTH halves (it backfills once; a later boot leaves a written value alone).
    Reach for it only when an empty column would make a learned-from-history feature dead on arrival
    for exactly the deployments that are already running, and only where the source column is an
    honest reading of the new one.
- **`EnsureTable`** — the DDL is lifted from EF's own `GenerateCreateScript()` at runtime, so there is
  no hand-written second copy of the schema. A parity test compares `sqlite_master` fingerprints of
  the migrated and fresh paths.
- **`NullableInviteCodeMigration`** is the documented exception: SQLite cannot ALTER a column to
  nullable, so relaxing NOT NULL needs a create/copy/drop/rename rebuild. It names its columns
  explicitly and asserts the set it knows, and it must run **strictly after** `AdditiveSchema.Apply`.

Anything structural is still a fresh-DB decision. Adopting EF Migrations is designed but sequenced —
`docs/remediation-plan.md` §8.

---

## Data model: brand-agnostic products, size as metadata (final, 2026-06-28)

A product is a brand-agnostic **item**; brand and size are tracked **per purchase**, so
the same item bought across brands/sizes rolls up into one product.

- `Product.Name` is the brand-stripped item ("Whole Milk", "Chicken Wrapped Cod Skin Dog
  Treats"). `Brand`, `Size`, and (since v3.5) `Variety` (all `string?`) live on `ReceiptLine`
  **and** `PurchaseEvent`; `ConfirmAll` copies the reviewed line's brand+size+variety onto both.
  Matching (ProductMatcher + aliases) keys on the item name only — so different brands/sizes/
  flavors merge, and the old store-brand collision is moot.
- Extraction prompt drives `normalized_name`=item, `brand`=brand, `size`=size. **Gotcha:
  keep the item's DISTINGUISHING words (variety/cut/flavor/form); strip ONLY brand + size.**
  An early prompt over-shortened "…Chicken Jerky Dog Treats" to bare "Dog Treats" and merged
  distinct products — the prompt now forbids bare-category names.
- **The DOMINANT size drives the prediction.** `Product.Size` was tried as identity then
  reversed (Jordan buys milk as half-gallon OR gallon at random; identity-by-size either made
  two products or split trivial sizes). `ReplenishmentPredictor` predicts cadence from the
  dominant size's purchases (most-bought; ties → most recent) and exposes `RecommendedSize` —
  one cadence, one recommended size, never "buy a gallon AND a half-gallon". **HYBRID:** use the
  dominant size's purchases only when that size has ≥2 buys, else fall back to ALL purchases (so a
  mixed-size item still predicts). NO unit arithmetic ("1 gal" = 2×"64 fl oz") — emergent approach
  chosen deliberately; trivial-vs-meaningful size (10.6 vs 11 oz) is undistinguished, revisit only
  if it bites. "bought N×" counts ALL purchases. UI: usual-brand + recommended-size hints across
  Products grid, Grocery List, dashboard cards, Product Detail.
- After the clean re-import: 56 products / 83 purchases / 73 branded; cross-brand rollups
  verified (bread = Nature's Own + Sara Lee, cod-skin dog treats = ASMPET + Pawmate); unbranded
  produce/meat (e.g. "93% Lean Ground Beef") stay null.
- **`Product.Tags`** (child `ProductTag` rows) is the descriptive second category layer added
  later — see the Tags & Recipes section above. The recipe feature adds `ExcludedFood`, `Recipe`,
  `RecipeIngredient`, and `GroceryExtra`. All are in the DbContext; `EnsureCreated` builds them on a
  fresh DB, but existing dev DBs were migrated in place via the dbfix ALTER-TABLE pattern below.

## Decisions & deviations from the spec

- **Spec enum "ShelfAweed"** is a find/replace artifact (Restock→ShelfAware) — implemented as
  `SignalKind.Restocked`. Read §6/§7's "ShelfAweed" as "Restocked".
- **`ShelfAware.slnx`** not `.sln` — the .NET 10 CLI default.
- **Data dir is `app-data/`** (not `data/` — collides with the `Data/` source folder on
  case-insensitive FS). Resolves to `src/ShelfAware.Web/app-data/` locally (ContentRootPath);
  a cloud box points the `DataDir` config key somewhere real (the droplet runbook uses
  `/var/lib/shelfaware`).
- **Global InteractiveServer render mode (v2.2).** `App.razor` sets `@rendermode="InteractiveServer"` on
  `<Routes>` and `<HeadOutlet>`; pages **must not** re-declare a render mode (a page can't set one an
  ancestor already set — it throws). This replaced per-page `@rendermode` directives so the layout, and
  the `VoiceAgent` it hosts, is interactive and **persists across navigation** (the persistent listening
  agent needs this; a static layout re-creates its interactive islands on every page change). No static-SSR
  benefit was lost — every page was already interactive. Cross-component coordination goes through a
  **scoped** `VoiceCoordinator` (Web/Services): `PantryChanged` (a voice data change refreshes the page on
  screen, replacing the old per-page `OnApplied`), `ResumeRequested` ("Back to assistant" resumes the
  agent), and `ScreenContext` (the page publishes what's on screen for positional references).
- **Official Anthropic C# SDK (`Anthropic` NuGet) used directly** behind `IReceiptExtractor` /
  `IPantryChat`, not wrapped in `Microsoft.Extensions.AI` `IChatClient` (§2) and not Semantic
  Kernel (§7) — the interface seam already gives swappability + testability; revisit if a second
  provider appears. Chat = manual tool-call loop over `Messages.Create` (§7 Option B).
- **Structured outputs** (`OutputConfig`/`JsonOutputFormat`) enforce the §5 schema server-side,
  plus the spec's validate-and-retry-once in C#. Schema omits `minimum`/`maximum` on confidence
  (unsupported in strict mode) — clamped in code. Extraction model pinned `claude-haiku-4-5-20251001`.
- **`IPantryStore` (Core/Chat) is the chat data port** — Core defines it, Web implements
  `EfPantryStore`, so the chat layer touches no EF. Fuzzy name→product resolution in
  `ProductMatcher` (Core, unit-tested): exact → substring → IDF-weighted token-overlap ≥ 0.5
  (IDF so brand/qualifier words like "great","value" carry ~0 weight and don't false-merge).
- **Chat has grown well beyond §7's tool set.** Live tools (matches `MakeTool` calls in
  `AnthropicPantryChat` — this list had drifted): `record_signal`, `add_purchase`, `query_status`,
  `set_tracking` (start/stop tracking → `IPantryStore.SetTrackingAsync`), `set_expiration`,
  `create_product`, `add_tags`, `suggest_substitutes`, `adapt_recipe`, `add_recipe_to_list`,
  `open_page`, `read_recipe`, and `go_to_step`. (`import_receipts` was removed with the
  folder-import feature, item 17 — "import my receipts" now opens the Upload page via `open_page`.)
  The last three don't touch data — they write into a mutable
  `NavigationTarget` slot that rides out on `ChatResult` (`NavigateTo` / `HandsOff` / `StepTarget`) for
  the UI to carry out. **`go_to_step` is the safety net under the cook-along grammar** (see Voice below):
  it moves the hands-free reader, which is what lets that grammar stay conservative.
- **Two new AI interfaces beyond §2/§7:** `ITagAdvisor` (Core/Tagging → `AnthropicTagAdvisor`) and
  `IRecipeAdvisor` (Core/Recipes → `AnthropicRecipeAdvisor`), both registered in DI. Same interface-
  seam pattern (Core defines, Llm implements). Tag advisor uses `ExtractionModel`, recipe advisor
  uses `ChatModel` (both Haiku).
- **Prediction extras beyond §6.7:** `PredictionResult.Pinned` (OutNow forces Overdue + sorts to
  top); `SignalNote` (user's statement, surfaced separately from `Basis`); `RecommendedSize`;
  `RebuyIntervalDays` + `BurnRateDays` (the two-stream rhythms). A Restocked signal is **status-only**
  — it clears an earlier OutNow and re-anchors the due date (a "last stock-back"), but does NOT feed
  either cadence rhythm; only real purchases do (§6 two-stream model).
- **`ShoppingEstimator` (Core/Shopping) is pure + unit-tested** — combines the price-free Core
  prediction with median quantity and a unit price passed IN by Web (avg of confirmed
  `ReceiptLine.UnitPrice` for the recommended size), so Core stays EF-free and the engine stays
  pure timing stats. Exposes `ProductEstimate` (incl. `RecommendedSize`, `UsualBrand`).
- **LLM-assisted product matching (extends §4):** extraction also receives the existing product
  list and returns a per-line `existing_product` → `ExtractedLine.SuggestedProductName`. Upload
  review pre-fills by trust order: learned alias → model suggestion → `ProductMatcher` → create new.
- **Purchase date from the receipt, not upload date** — review screen has an editable "Purchase
  date" (defaults to extracted date, or today with a warning), written to every PurchaseEvent so a
  batch of old receipts keeps accurate intervals.

