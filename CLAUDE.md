# CLAUDE.md — process notes for AI-assisted sessions

Working notes for Claude Code sessions on this repo. The authoritative spec is
[DESIGN.md](DESIGN.md) — read §0 (rules) and §10 (phases) before doing anything.
This file records build state, decisions, and environment quirks the spec and
code don't capture. **As-built deviations from the spec live here, not in DESIGN.md.**

**Audience & quality bar:** a portfolio piece with real users (Jordan + his wife)
and professional viewers (current + prospective employers). Demonstrate
production-ready work — robustness, clean atomic git history, tests, accessibility,
and visual polish are in-scope and expected, not gold-plating. Don't dismiss polish
as overkill "because it's single-user."

## Design directives

- **Co-creation — stop and discuss before diverging.** Jordan and Claude are
  co-creators. Always stop and talk it through if you disagree about a direction,
  or see a better/riskier/materially harder path than what was asked. Don't silently
  build what you think is best, and don't silently implement something you believe is
  wrong — surface the trade-off, reason it out together, decide jointly, then code.

- **Craftsmanship — take pride in every change; no shortcuts.** Always do the polished,
  professional thing, not the quickest thing that happens to pass. Concretely: **no empty
  or catch-all `catch` blocks that swallow errors** — catch specific exceptions, log via
  `ILogger`, and let cancellation (`OperationCanceledException`) propagate; don't duplicate
  logic that should live in one shared place; don't ship behavior without tests; leave no
  dead code, orphaned state, or TODO-shaped gaps. If you spot a corner being cut — yours or
  the existing code's — fix it or flag it, never leave it. Assume every line will be read by
  a prospective employer, because it will.

## Build state (updated 2026-07-07)

| Phase (DESIGN.md §10) | Status |
|---|---|
| 1 — Skeleton + data | ✅ Done, acceptance verified |
| 2 — Extraction pipeline | ✅ Done, 3 acceptance criteria verified with live calls |
| 3 — Prediction engine + dashboard | ✅ Done, engine tests green + dashboard verified |
| 4 — Chat tools (IPantryChat) | ✅ Done, acceptance verified with a live tool-call |
| 5 — Azure deploy + README | ◑ README ✅ done + pushed (`4757839`); **Azure still deferred** (pending Jordan's account) |

Everything below is built, verified live, committed, and **pushed** (master, through the 2026-07-05
v2.3 full-site-audit + BYOK arc — see item 8 below and timeline.md).
Beyond the spec's 3 pages, the app now has Dashboard (`/`), Upload (`/receipt`),
Products (`/products`), Grocery List (`/list`, by aisle + copy/print + a manual **Extras**
section), Trends (`/trends`, price tickers + spend forecast — page component is
`SpendInsight.razor`), Product Detail (`/product/{id}`, rhythm + price-history chart),
Accuracy (`/accuracy`, renders `eval-results.json`), **Recipes (`/recipes`)**, and
Receipts (`/receipts`, added 7/12 — per-receipt line-item totals via `ReceiptTotals`, Core).
Extensive polish stretch done: design-system + dark mode (CSS vars) + site-wide a11y
pass; LLM-assisted product matching in extraction; GitHub Actions CI (restore + build
+ unit tests; Evals excluded — needs a live key). **200 green xUnit tests across three
projects** (pure engine · faked-IChatClient AI layer · persistence on in-memory SQLite).

**Post-Phase-4 feature arc (all ✅ committed + pushed):**
1. **Size loop closed in the buying UI** (`cc21250`) — recommended size + usual brand now show
   on the Grocery List and dashboard cards (not just Product Detail / Products grid).
   `ProductEstimate` carries `RecommendedSize` + `UsualBrand` (shared `ShoppingEstimator.
   UsualBrandOf`); sizes are display-normalized via `SizeFormat.Normalize` (cosmetic only);
   est. cost prices the recommended (dominant) size, so **`Size` was added to `ReceiptLine`**
   (mirrors `Brand` on both `ReceiptLine` + `PurchaseEvent`; `ConfirmAll` writes it).
2. **Real accuracy numbers** (`af19103`, then `b250103`) — 3 real Walmart receipts hand-labelled
   (PDFs gitignored; only `*.expected.json` + `eval-results.json` committed). **99% recall /
   99% precision / 100% field accuracy** on `/accuracy`. First run read 58% — the flaw was the
   symmetric-Jaccard name matcher, switched to the **token containment coefficient (≥ 0.6)**;
   the honest 58%→100% metric-fix story is in the README.
3. **Two-layer categories** (`9670d39`, `b250103`, `628fecf`, `994ead7`, `8da2114`) — see the
   Tags section below.
4. **Recipes** (`ff1fd83` P1, `612fcbd` P2) — see the Recipes section below.
5. **README capstone** (`4757839`), **rewritten 2026-07-04** per Jordan's "more casual /
   usage-focused" feedback — now covers the v2 arc (voice, graduated auto-import, two-stream
   cadence) and the both-halves accuracy story (extraction eval + prediction backtest).
   **Placeholders Jordan must still fill:** live-demo URL (`<!-- LIVE_DEMO_URL -->`),
   `docs/demo.gif`, `docs/accuracy.png`.
6. **Small UI adds:** always-available **"Out" button** on the Products grid (`9c78a14`) — the
   dashboard only lists running-low items, so the grid is the home for marking any product out;
   grocery-list item names link to `/product/{id}` (`b6afb35`).
7. **v2.2 review-hardening pass (2026-07-04, from the 7/3 code review — see timeline.md):**
   - **`ReceiptConfirmationService` (Web/Data) is THE confirm path** — Upload's ConfirmAll and the
     auto-importer both go through it. Idempotent (already-Confirmed = no-op), clamps qty ≤ 0 → 1
     and future dates → today, canonicalizes tags against the GLOBAL vocabulary, and takes a
     `writeAliases` flag: **only human-confirmed receipts write merchant aliases** (machine matches
     must not become sticky). Don't add a second confirm path.
   - **`ReceiptLine` gained `TagsJson` + `SuggestedProduct`** (additive EnsureColumn migrations in
     Program.cs) so queued receipts keep tags + the LLM match through review.
   - **ImportMode setting** (Review/Smart/Auto; Smart default; legacy `AutoConfirmImports` still
     honored) — Smart auto-confirms only when every line resolves via alias or ≥ 0.8-confidence
     match to an existing product. Importer holds a static scan lock; failed imports are listed on
     Upload ("couldn't be read") with Retry (re-extracts from the saved audit copy) and Discard.
   - **Engine:** `IntervalSpreadDays` (IQR of the driving samples) widens the DueSoon window;
     `StockUpFactor` (extend-only, ≤ 3×) stretches the due date after a bigger-than-usual buy;
     same-day signal ties deliberately lose to the purchase (documented + pinned by a test).
   - **`PredictionBacktest` (Core)** — walk-forward self-scoring of the engine, rendered live on
     `/accuracy` next to the extraction eval.
   - **`tests/ShelfAware.Web.Tests`** — real EF on in-memory SQLite (FKs + unique indexes enforced);
     covers the confirmation service, importer routing, and the product-delete FK regression.
   - **Chat can navigate the UI:** `ChatResult.NavigateTo` (a relative URL) is set by the `open_page`
     and `read_recipe` tools; the voice/chat surfaces apply it via NavigationManager after showing/speaking
     the reply. `open_page` also handles `page="recipes"` + `product_name` → `/recipes?uses={id}` (recipes
     that use a product). Recipe names resolve exact → substring → token containment ≥ 0.6 (unique winner).
     **"Stop listening"** (`VoiceCommands`, Core, plain code — whole-utterance match, filler tolerated)
     ends the conversation BEFORE the LLM is called; cookalong.js also force-closes the session on the phrase.
   - **Hands-free navigation (v2.2):** the conversational agent moved out of the dashboard into
     `Components/Layout/VoiceAgent.razor`, hosted in `MainLayout` so it **survives navigation and keeps
     listening** — enabling a chain like "go to the chicken → recipes that use it → read me the second
     one". This required going **global interactive** (see Decisions). It keeps listening after an
     `open_page` nav but stands down on a hand-off (`ChatResult.HandsOff`, set by `read_recipe`) where the
     reader makes its own audio. `read_recipe` navigation prefers the **listening cook-along agent** when
     the ElevenLabs agent is configured (fully voice-controllable: next/back/stop + "go to the assistant"),
     and **falls back to the button-controlled read-aloud** if cook-along can't connect. Both readers expose
     a "🎤 Back to assistant" hand-back (a button; cook-along also takes the spoken phrase) that resumes the
     agent via `VoiceCoordinator`. **Screen-aware references** ("the second one") work because the page on
     screen publishes its list to `VoiceCoordinator.ScreenContext`, which the agent passes into
     `IPantryChat.HandleAsync(screenContext)` for injection into the system prompt.

8. **v2.3 — full-site audit, BYOK, and fixes (2026-07-05; all ✅ committed + pushed):**
   - **Audit hardening** — `QuerySplittingBehavior.SplitQuery` + `AsNoTracking` on read loads (kills the EF
     cartesian-`Include` [20504] warning) (`c526648`); page catch-alls log via `ILogger`, rethrow
     `OperationCanceledException`, and stop leaking `ex.Message`, and `AnthropicPantryChat` wraps each
     tool-handler call so a thrown tool exception becomes an error result instead of blanking the dashboard
     box (`50b9e2b`); ProductDetail reloads on id change (`d1618ff`); NotFound/Error use the design system
     (`d927f56`); dashboard double-tap guard + SplitButton a11y + tidy EF writes + table captions (`5739c3a`).
   - **BYOK — bring your own key** — public/source-available posture: the deployed demo ships **no usable keys**;
     visitors bring their own with minimal effort; Jordan's keys are never used live. The `IChatClient` seam
     means service CODE didn't change — BYOK is a lifetime/wiring change (singleton→**scoped**), not per-call:
     - **Provider seam** (`10a8fcb`): `AiProvider` enum + `IChatClientFactory`/`ChatClientFactory` (Anthropic
       via the SDK adapter; OpenAI via `Microsoft.Extensions.AI.OpenAI`). Config-driven; keyless boot preserved.
     - **Per-circuit clients** (`5ffa466`): `CircuitAiSettings` (scoped, defaults to `LlmOptions`, overlaid by
       the browser) + `ByokChatClient` (scoped delegating `IChatClient` that builds the real client at CALL
       time, robust to the browser settings loading late) + `AiSettingsLoader.razor` + `wwwroot/js/ai-settings.js`
       (localStorage `shelfaware.ai`). AI services + importer are **scoped**; store/inbox/settings/confirmation
       stay singleton; the startup receipt scan runs in its own scope, owner-key-only (skipped on a keyless deploy).
     - **CSP + security headers** (`4a6cb0f`): `script-src 'self' https://esm.sh` (no unsafe-inline/eval),
       `connect-src` locked to self + ElevenLabs, object/base/frame-ancestors/form-action locked, +
       nosniff/Referrer-Policy/X-Frame-Options/Permissions-Policy(mic=self). Keys never persist/log; they transit
       server RAM only during a call. (Dev relaxes script/connect-src — see the CSP-vs-hot-reload gotcha below.)
     - **Settings UI** (`fba756f`): provider dropdown, masked key, editable per-module model datalists, optional
       EL key + agent id, **Forget-my-key** (clears both stores) + **session-only** toggle.
     - **Voice keyed per-circuit** (`b140b48`, `b959d4a`): `IVoiceCredentials`/`CircuitVoiceCredentials`; speech
       services attach `xi-api-key` PER REQUEST from the scoped creds (fail soft without one); the signed-url
       endpoint uses the visitor's key/agent, **rate-limited 12/min per IP**; cook-along sends the visitor's key
       headers; EL SDK pinned `@elevenlabs/client@1.14.0` (kept on `esm.sh` — a multi-module ESM SDK can't be
       vendored without a build step).
     - **README/BYOK docs DONE (2026-07-09):** "Whose keys?" section in the README (byok/managed/`Llm:KeyMode`,
       quota keys, the honest key-custody story). The remaining README placeholders are the two captures —
       capture plan in `docs/demo-gif-storyboard.md` (delete that file when `docs/demo.gif` lands).
   - **Fixes** — (a) short-cadence items never left Running Low after a restock: the flat 3-day DueSoon floor
     could span the whole cadence, so a fresh stock-back re-anchored straight back into the window; now capped
     at `interval - 1`, regression-tested (`6b2c32b`). (b) `/recipes?uses=` only matched top-level recipes, so an
     adapted variant that swapped in a product its original never used didn't show; variants now match on their
     own ingredients, with the non-matching original shown as a muted "for reference" row (`7c805e5`). (c) The
     strict CSP broke VS Browser Link + hot reload in dev — relaxed `script-src`/`connect-src` in Development
     only (`fd580bd`; see the gotcha in Environment notes).

9. **v3 — Accounts & households (2026-07-07, branch `feature/auth-households`):**
   - **Every page requires a signed-in user.** ASP.NET Core Identity, cookie auth, local email+password.
     **Identity lives in its OWN SQLite file (`app-data/auth.db`,** `AuthDbContext : IdentityDbContext<AppUser>`)
     so `EnsureCreated` builds the auth schema everywhere with no migrations and the pantry context stays
     free of Identity noise. `Auth/` holds the domain (`AppUser`, `Household`, `HouseholdService`,
     `HouseholdClaimsPrincipalFactory`, `AuthOptions`).
   - **Households are the tenancy unit** — accounts belong to exactly one (created at registration, or
     joined via a CSPRNG **invite code**); ALL pantry data is household-scoped. Every pantry entity
     implements `IHouseholdOwned`; `ShelfAwareDbContext` has a per-instance `HouseholdId` driving a global
     query filter on every table + SaveChanges stamping on inserts. `AppSettings` = composite PK
     `(HouseholdId, Key)`; alias uniqueness = `(HouseholdId, Merchant, RawText)`.
   - **`IHouseholdDbFactory` is THE way to a pantry context** (scoped; pre-sets `HouseholdId` from the
     scoped `ICurrentHousehold`: `UseFixed` pin → HttpContext claim → circuit auth state). The raw
     `IDbContextFactory` is bootstrap-only. Formerly-singleton data services (store, settings, inbox,
     confirmation, rename, seeder) are **scoped** now. The startup receipt scan runs once per household
     that configured a folder (`IgnoreQueryFilters` enumeration + `UseFixed` per scope).
   - **Account pages are Blazor components on static SSR** (`Components/Account/`): auth cookies can't be
     set over a circuit, so they carry `[ExcludeFromInteractiveRouting]` and `App.razor` picks the render
     mode per page (`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null` — BOTH outlets).
     They use `AccountLayout`, NOT MainLayout (whose VoiceAgent/AiSettingsLoader islands must not spin up
     circuits pre-auth). Zero scripts beyond `js/account.js` (progressive enhancement) — strict CSP holds.
   - **Security posture:** registration gate is server-side (`Auth:AllowRegistration`; first-user bootstrap
     + invite-join always open); logout bumps the security stamp (all circuits/devices die within the
     5-minute revalidation) then clears the cookie; per-IP rate limit on `/Account` POSTs atop Identity
     lockout; `/api/data/export` + `/api/cookalong/signed-url` require auth (401/403 for API callers);
     DataProtection keys persist to `app-data/keys` (DPAPI-encrypted on Windows) so republish doesn't log
     everyone out. **Backup set is now `shelfaware.db` + `auth.db` + `keys/`.**
   - **BREAKING SCHEMA — v3 needs a fresh pantry DB.** No in-place upgrade (Jordan's call: wipe + re-import
     receipts). The old EnsureColumn/CREATE-IF-NOT-EXISTS additive block is REMOVED (it only served pre-v3
     DBs); `PantryDbGuard` fails fast on an old file with delete-and-restart instructions.
   - Managed (non-BYOK) keys stay **server-wide** — exactly as before; BYOK stays per-circuit/browser.
   - **Metering (managed mode only):** `AiUsage` (one row per household/day) + `AiUsageMeter` +
     `MeteredChatClient` atop `ByokChatClient` — every IChatClient call quota-checked/recorded; the
     cook-along endpoint gets a per-household mint quota. Config: `Llm:DailyCallLimit`,
     `Llm:DailyTokenLimit`, `ElevenLabs:DailySignedUrlLimit` (all null = unlimited, the self-host
     default). BYOK circuits are NEVER metered. Billing/pricing = Jordan's separate workstream.
   - **OAuth (config-gated):** Google login registers only when `Authentication:Google:ClientId` (+
     `:ClientSecret`) is configured — put them in user-secrets, never committed. Unconfigured = zero
     OAuth surface. First external sign-in runs the SAME registration gate + household chooser
     (`Components/Account/Pages/ExternalLogin.razor`).

10. **v3.2 — verified-receipt self-eval + usage transparency (2026-07-12):**
   - **`Receipt.VerifiedForEval`** — the user's explicit "I checked every line" assertion (Upload review
     checkbox, or retro-verify on `/receipts`). THE trust boundary for accuracy ground truth: machine
     confirms can never set it (same principle as `writeAliases`), and it's a parameter on the ONE
     confirm path. Ships via **`AdditiveSchema` (Web/Data) — the post-v3 additive-migration seam**:
     idempotent `ALTER TABLE … ADD COLUMN` on startup after EnsureCreated. Additive DEFAULT-valued
     columns only; anything structural stays a fresh-DB change.
   - **`ExtractionScorer` (Core/Evaluation)** — the scoring math (containment matcher, plural folding,
     aggregates) moved out of the Evals console so the offline harness and the in-app check share one
     definition of "accurate". Unit-tested now (it wasn't testable as console-local functions).
   - **`/accuracy` "Your receipts"** — `ReceiptSelfEval` (Web, scoped) re-reads each verified receipt
     from its stored audit copy (`app-data/receipts/<folder>/page-*`, the Retry path's files) and
     scores against the confirmed lines. On-demand button only (a vision call per receipt — token
     disclaimer shown, with today's usage); last run persists per household in AppSettings
     (`SelfEvalResults`). Runs on the circuit's key: BYOK grades on the visitor's wallet, managed is
     metered/quota'd like any call. "Export fixture labels" downloads the harness's expected.json shape.
   - **Usage recorded in EVERY key mode** — `MeteredChatClient` now always records calls+tokens to the
     household's `AiUsage` row; quotas remain managed-only (BYOK: recorded, never limited). Settings
     gains an "AI usage" panel (today + 14-day daily table via `AiUsageMeter.GetRecentAsync`).

Mid-session polish (committed): **safe-side rounding** — predicted run-out interval
floors (due a touch early), buy-quantity ceils for whole-unit items (no more "1.5"
on the list; weight items stay fractional); **out-now shows "due today"** — an active
OutNow sets the effective due date to the outage date so the card no longer says
"Overdue" next to "due in 21 days".

Deferred / backlog: **Azure App Service deploy** (Phase 5 — then swap the README live-demo
URL + add `docs/demo.gif` + `docs/accuracy.png`). **Deploy gotcha — timezone:** every "today"
in the app (purchases, signals, predictions) is server-local `DateTime.Today`/`DateTimeOffset.Now`,
deliberately consistent; on Azure (UTC) an evening "Bought today" would land on tomorrow's date, so
set the App Service `WEBSITE_TIME_ZONE` (Linux: `TZ`) app setting to Jordan's timezone at deploy.
Also backlog: **CSV history importer — PARKED** (Walmart won't export to Jordan's state; needs another
itemized source); a tiny "dapper blob" mascot for the header; a per-size Trends price chart.
(Shipped since this note: the double-scroll fix; the **two-stream cadence model** — rebuy rhythm +
burn rate, hybrid, restock is status-only (§6); and the whole **production-hardening pass** —
logging, the SQLite CVE patch, the `IChatClient` migration, and faked-client tests.)

## Tags & Recipes (feature arc beyond the original spec)

**Two-layer categories** — one primary store-aisle `Category` (enum, unchanged, drives
grocery-list order) PLUS free-form `ProductTag`s (a many-per-product child table; `Product.Tags`).
The `category` was re-framed in the extraction prompt to STORE AISLE (first-aid→PersonalCare,
canned/condiment/shelf-stable→Pantry, cleaners/paper→Household); brand-defined items keep their
brand (the Velveeta over-strip fix). **Two-stage tag dedup:** plain-code
`TagVocabulary.FindNearDuplicate` (near-dup guard, Core, unit-tested) → then, only if that finds
nothing, `ITagAdvisor.FindSynonymAsync` (`AnthropicTagAdvisor`, Haiku synonym check, **fails open**).
Extraction is fed the **live tag vocabulary** (seed ∪ stored) via `ExtractAsync(…, knownTags)` so
the model reuses tags instead of coining near-dupes (dedup-at-source). UI: per-line tag editor on
Upload review (chips + datalist), tag chips on Product Detail linking to `/products?tag=`, and a
clickable **tag cloud** on Products that filters the grid (deep-linkable `[SupplyParameterFromQuery]
?tag=`) + per-row mini chips.

**Recipes (`/recipes`)** — an inventory-aware recipe helper (P1 `ff1fd83`, P2 `612fcbd`).
`IRecipeAdvisor`/`AnthropicRecipeAdvisor` (structured output, ChatModel/Haiku) takes an NL request
("what can I make?"), reasons over on-hand products (tracked & not-Overdue) and hard-excludes a
persistent **won't-eat** list (`ExcludedFood`). Suggestions list main ingredients vs. seasonings
separately and are saveable. **Key learning:** the LLM can't self-report availability, so the advisor
returns a grounded `matched_product` per ingredient (exact on-hand product name or null), captured
**once at save time** and persisted on `RecipeIngredient.MatchedProduct`. Makeability = **plain-code**
check that all MAIN ingredients' matched product is currently on-hand ("Ready to make"/"Missing items"
badges). Also: **"Ate it"** (`Recipe.TimesEaten`), **"Pick for me"** (random from saved + eaten +
makeable), and **"Add missing to list"** → the new `GroceryExtra` **Extras** section on `/list` (which
also filled a real gap — the list had no manual-add before). A label-check disclaimer is shown (not
allergy-safe medical advice). Entities: `ExcludedFood`, `Recipe`, `RecipeIngredient(IsMain,
MatchedProduct)`, `GroceryExtra`.

**Makeability by food family (v2.2).** Recipes stay SPECIFIC ("chicken breast", real cook times); the
flexibility lives on products. Each product has an **"Also works as"** list (`ProductSubstitute` child
rows, `Product.Substitutes`) — the recipe ingredients it can stand in for ("Chicken Breast Tenderloins"
also works as "chicken breast", "chicken cutlet"). `IngredientMatcher` (Core, unit-tested, replaces the
old exact-`MatchedProduct` check) covers a main ingredient when its core words (only trivial modifiers —
fresh/frozen/boneless/size/unit — stripped; cut/form words KEPT) appear in an on-hand product's **name OR
a substitute phrase** — so tenderloins cover "chicken breast" but "Whole Chicken" and "Chicken Broth" do
NOT. Recipe on-hand is **edible only** (excludes Household/PetCare/PersonalCare, so "Chicken Jerky Dog
Treats" can't count as chicken). Substitutes are **AI-seeded** (`IProductSubstituteAdvisor` →
`AnthropicProductSubstituteAdvisor`, Haiku, fails soft) + user-curated: an ✨ Suggest button on Product
Detail, and the **`suggest_substitutes` chat/voice tool** (`IPantryStore.AddSubstitutesAsync`) so the
assistant fills them in from anywhere. `ProductSubstitutes` is an additive table (CREATE TABLE IF NOT
EXISTS in Program.cs for existing DBs).

**Adapt to what you have (v2.2).** A saved recipe can be rewritten to use on-hand ingredients: the AI swaps
missing main(s) for ones you have and **rewrites the steps + cook times** (thighs cook longer than breast),
saved as a **variant** (`Recipe.ParentRecipeId`, additive column) grouped under the original on the Recipes
page. On-demand only (no AI calls on load). One orchestration path — `IRecipeAdapter` (Core) →
`RecipeAdapter` (Web, scoped; loads the recipe + on-hand + excluded, calls `IRecipeAdvisor.AdaptAsync`,
saves the variant) — drives the "🔀 Adapt to what I have" button, the **`adapt_recipe` chat/voice tool**,
AND the per-ingredient bubble cloud. Adapt prompt is `recipe-adapt-system.txt`. On-hand = the shared
`PantryOnHand.EdibleInStock` (Core; `CategoryExtensions.IsEdible` + not-overdue). **Robustness:** re-adapting
**dedupes by main-ingredient content signature** (not the AI's title) so it updates in place; variants are
saved only when valid, and the adapter logs + re-throws cancellation (no swallowed errors).

**Bubble-cloud ingredient picker (v2.2).** Each main ingredient on an original recipe has a **⇄ swap**
that opens a cloud of interchangeable forms (`IIngredientAlternativesAdvisor`, Haiku; generated once and
**cached** on `RecipeIngredient.AlternativesJson`), colored green/red via `IngredientMatcher`. Clicking a
bubble runs a **targeted adapt** — a typed `IngredientSwap(IngredientName, ChosenForm)` the adapter turns
into the prompt preference AND **guards**: if the model ignores the pick, `IngredientMatcher.IsMentionedIn`
catches it and the adapt is rejected (retry) rather than saving a mislabeled variant.

## Data model: brand-agnostic products, size as metadata (final, 2026-06-28)

A product is a brand-agnostic **item**; brand and size are tracked **per purchase**, so
the same item bought across brands/sizes rolls up into one product.

- `Product.Name` is the brand-stripped item ("Whole Milk", "Chicken Wrapped Cod Skin Dog
  Treats"). `Brand` and `Size` (both `string?`) live on `ReceiptLine` **and**
  `PurchaseEvent`; `ConfirmAll` copies the reviewed line's brand+size onto both. Matching
  (ProductMatcher + aliases) keys on the item name only — so different brands/sizes merge,
  and the old store-brand collision is moot.
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
  Azure uses `/home/data` via the `DataDir` config key.
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
- **Chat has a 5th tool beyond §7:** `set_tracking(product_name, tracked)` (start/stop tracking,
  i.e. untrack) → `IPantryStore.SetTrackingAsync`. So the live tool set is `record_signal`,
  `add_purchase`, `query_status`, `create_product`, `set_tracking`.
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

## Environment & workflow gotchas

- **Stop the dev server before `dotnet build`** — a running server locks the DLLs (MSB3027 after
  10 retries). Started outside the preview tooling it won't show in `preview_list`; find/kill the
  `ShelfAware.Web` process (it names itself in the lock error).
- Dev server runs via the preview tooling: config `shelfaware-web` in `.claude/launch.json`
  (repo root + parent folder), port 5179. **When Jordan's tailnet publish occupies 5179** (it's the
  same exe name — match on path, not name), use the `shelfaware-web-alt` config (port 5180) instead
  of killing his live app.
- **v3 auth gotchas:** don't re-declare a render mode on a page (`App.razor` decides per page now —
  static for `/Account/*`, InteractiveServer otherwise). Live-testing login flows: register a
  throwaway account (e.g. `jordan@test.local`) — `auth.db` is dev-local and gitignored. A pre-v3
  pantry DB makes startup fail fast by design (delete `app-data/shelfaware.db*` and re-import).
- **API key** is in dotnet user-secrets, id `3d6755e6-9881-43a6-813c-fe3ebd974cd9`, key `Llm:ApiKey`.
  Editing that file by hand repeatedly failed for Jordan. To change it: have him save the bare key
  to a gitignored repo file (see the sandbox gotcha below), move it into secrets.json programmatically,
  delete the temp file. Never echo or commit the key.
- **Claude's tool sandbox reads a FROZEN snapshot of the user's `%APPDATA%` / user-secrets, separate
  from the real machine.** The repo dir is live-shared (edits + commits are real), but the user profile
  is NOT: a key the user adds via `dotnet user-secrets` in their own terminal is INVISIBLE to the dev
  server Claude launches (which reads the stale sandbox copy — e.g. it was seen frozen at 2026-06-12
  with only `Llm:ApiKey`). Tell-tale symptom: `dotnet user-secrets list` shows different keys in
  Claude's shell vs. the user's terminal. Consequences: (a) Claude's launched app only has whatever
  secrets existed when the sandbox was created; (b) to test a feature needing a NEWLY-added secret,
  either the USER runs the app themselves, OR drop the key into a **gitignored repo path** (e.g.
  `src/ShelfAware.Web/app-data/elkey.txt` — `app-data/` is ignored; NOT the Desktop, which the sandbox
  can't see) and have Claude read it and `dotnet user-secrets set` it into the sandbox store, then
  delete the file. Suppress the `set` command's stdout so the value isn't echoed.
- **Schema changes need a fresh DB** — `EnsureCreated()` does NOT migrate. Either delete
  `app-data/shelfaware.db*` (clean empty DB; re-import the 3 real receipts via Upload) OR, to keep
  the curated data without re-extraction, `ALTER TABLE … ADD COLUMN` + backfill against the SQLite
  file (a throwaway `dotnet run` console referencing `Microsoft.Data.Sqlite.Core` works; PowerShell
  5.1 can't load the .NET 10 assemblies). Real receipts: `C:\Users\Jorcu\Documents\Walmart Receipts`.
- **Blazor `<InputFile>` must stay mounted while `IBrowserFile` streams read** — unmounting (e.g.
  switching to a spinner) breaks reads with `_blazorFilesById` null. Upload.razor hides it with
  `hidden`. Don't "simplify" this.
- **Browser-testing uploads without real files:** draw a receipt on a JS canvas in `preview_eval`,
  wrap in `File`/`DataTransfer`, assign to the input, dispatch `change`. `test-fixtures/` also has
  committed synthetic PNGs.
- `gh` CLI at `C:\Program Files\GitHub CLI\gh.exe` (full path in non-refreshed shells), authed as
  `Jcurran-Repo`. Remote: https://github.com/Jcurran-Repo/ShelfAware (public).
- Shell is Windows PowerShell 5.1 — no `&&`, no ternary; state-probing commands
  (`Get-NetTCPConnection` finding nothing) can exit 1 without being failures.
- **Commit with a message file:** write the full message (incl. `Co-Authored-By` trailer) to a temp
  file and run `git commit -F <file>` from PowerShell. Multi-line `-m`/heredoc commits via the Bash
  tool silently no-op'd here (staging worked, commit never happened, no error). Commit per task/phase;
  the body explains what was verified + any deviations. **Don't push until asked.**
- **Dev CSP vs. hot reload (2026-07-05).** The production Content-Security-Policy is strict
  (`script-src 'self'`, locked `connect-src`) and blocks Visual Studio's Browser Link + browser-refresh
  (they inject an inline bootstrap script and use ephemeral localhost websockets), which **silently kills
  hot reload** in dev — edits stop applying to the running app with no error, and you debug a stale binary.
  `Program.cs` relaxes exactly `script-src`/`connect-src` **in Development only**; production stays locked
  down (a plain Kestrel run shows zero CSP violations). Don't re-tighten those for dev. Tell-tale: a
  `Refused to execute inline script … script-src` console error on the host page under `dotnet watch`/VS.

## Conventions

- Phases strictly in §10 order; don't start one until the previous phase's acceptance passes. No
  scope beyond the spec (§0, §12) without discussion.
- Prompts live in `src/ShelfAware.Llm/Prompts/` as embedded resources — iterate there, not in C#
  string literals.
- Core has no LLM and no EF references; the DbContext lives in Web.
