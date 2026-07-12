# Shelf Aware — Feature Timeline

The master list of every feature, shipped and planned, by release phase/version.
Terse on purpose (no descriptions) — **git log** has the descriptions, **DESIGN.md** and
**CLAUDE.md** have the detail. This file exists so the full roadmap — including what *isn't*
done yet — survives even if everything else is lost.

**Terminology:** Phases 1–5 are the original v1 build milestones. v2 / v3 are later versions.
`[x]` + date = shipped · `[ ]` = not done yet.

_Last updated: 7/7/2026_

---

## v1 — Pantry tracker

### Phase 1 — Skeleton + data
- [x] Solution, entities, EF/SQLite, Products CRUD — 6/12/2026

### Phase 2 — Extraction pipeline
- [x] Receipt extractor (structured output + validate/retry) — 6/12/2026
- [x] Upload → review/confirm → alias write-back — 6/12/2026

### Phase 3 — Prediction engine + dashboard
- [x] Replenishment predictor (median intervals, signal overrides) — 6/26/2026
- [x] Dashboard "Running Low" — 6/26/2026

### Phase 4 — Chat tools
- [x] NL dashboard box + tool-calling loop (record_signal, add_purchase, query_status, create_product) — 6/26/2026
- [x] Chat can untrack a product (set_tracking) — 6/30/2026

### Phase 5 — Deploy + README
- [x] Capstone README — 6/30/2026
- [ ] Azure App Service deploy + live demo URL — Not complete
- [ ] README assets: demo.gif + accuracy screenshot — Not complete (capture plan: docs/demo-gif-storyboard.md, 7/9/2026)

### v1 enhancements (added over later weekends)
- [x] UI design-system + dashboard polish — 6/26/2026
- [x] Receipt review: confidence chips + LLM-assisted product matching — 6/26/2026
- [x] Products grid overhaul + accessibility pass — 6/27/2026
- [x] Grocery List page (by aisle, copy/print) — 6/27/2026
- [x] ShoppingEstimator moved to Core + unit-tested — 6/27/2026
- [x] Purchase date from the receipt (not upload date) — 6/27/2026
- [x] Dark mode — 6/27/2026
- [x] Product Detail page (rhythm + price history) — 6/27/2026
- [x] GitHub Actions CI (build + unit tests) — 6/27/2026
- [x] Trends page + price charts — 6/27/2026
- [x] Extraction eval harness — 6/27/2026
- [x] Brand-agnostic products + brand per purchase — 6/28/2026
- [x] Usual-brand hint (Products grid) — 6/28/2026
- [x] Size as metadata + dominant-size cadence (hybrid) — 6/28/2026
- [x] Recommended size + brand across the buying UI — 6/28/2026
- [x] Safe-side rounding (interval down / buy-qty up) — 6/30/2026
- [x] Marked-out items show "due today" — 6/30/2026
- [x] Real eval accuracy numbers (99 / 99 / 100) — 6/30/2026
- [x] Two-layer categories: tags + tag cloud + live vocab — 6/30/2026
- [x] "Out" button (Products grid) — 6/30/2026
- [x] Grocery-list names link to Product Detail — 6/30/2026
- [x] Recipes: excluded foods + AI suggestions + save — 6/30/2026
- [x] Recipes: grounded matching, makeability, Pick-for-me, add-missing-to-list — 6/30/2026

---

## v2 — Voice + production hardening

### Production hardening (do before voice)
- [x] Structured logging / observability (ILogger) — 7/2/2026
- [x] Provider-agnostic AI seam (Microsoft.Extensions.AI IChatClient) — 7/2/2026
- [x] Separate AI/LLM test project (keep the plain-code test project pure) — 7/2/2026
- [x] CI tests for the LLM tool-loop via a faked client — 7/2/2026

### Voice
- [x] v2.0 — Push-to-talk (ElevenLabs STT + TTS, existing chat brain) — 7/3/2026
- [x] v2.1 — Conversational multi-turn (owned IPantryChat + ElevenLabs STT/TTS) — 7/3/2026
- [x] Recipe read-aloud — TTS plays a saved recipe — 7/2/2026
- [x] Voice control of read-aloud: stop / next / repeat (barge-in via an ElevenLabs agent) — 7/3/2026
- [x] Recipe cooking steps (data model + advisor) — enables step-by-step read-aloud — 7/2/2026

### Prediction
- [x] Two-stream cadence model (rebuy rhythm + burn rate, hybrid) — 7/2/2026

### Ingestion
- [x] Receipt auto-import: settings page + swappable inbox seam + agent tool (auto-confirm) — 7/3/2026
- [x] Recipe calorie estimate (advisor + display + cook-along context) — 7/3/2026
- [ ] Cloud receipt inbox (Azure Blob / drive) — non-local import at deploy — Not complete

### v2.2 — Review hardening + self-measuring accuracy (from the 7/3 code review)
- [x] Product delete no longer crashes on receipt-sourced products (+ confirm dialog) — 7/4/2026
- [x] One shared, idempotent receipt-confirmation service (Upload + importer; double-click safe) — 7/4/2026
- [x] Queued receipts keep tags + the model's product suggestion (persisted on ReceiptLine) — 7/4/2026
- [x] Smart graduated import mode (Review / Smart / Auto; Smart = new default) — 7/4/2026
- [x] Machine-confirmed imports write no merchant aliases (human-only trust) — 7/4/2026
- [x] Import scan lock (no concurrent double-imports) — 7/4/2026
- [x] Failed imports visible + retryable on Upload — 7/4/2026
- [x] Persistence test project on in-memory SQLite + CI step — 7/4/2026
- [x] Voice loop leak fix + surfaced conversation errors — 7/4/2026
- [x] Cadence spread (IQR) widens the DueSoon window — 7/4/2026
- [x] Stock-up quantity stretches the due date — 7/4/2026
- [x] Prediction backtest — the engine scores itself, live on /accuracy — 7/4/2026
- [x] README v2 refresh (voice, auto-import, both-halves accuracy) — 7/4/2026
- [x] "Stop listening" ends any voice surface (plain-code phrase detection) — 7/4/2026
- [x] One-command recipe handoff: chat navigates pages + auto-starts read-aloud (open_page / read_recipe) — 7/4/2026

### v2.2 — Hands-free navigation (persistent voice agent)
- [x] Persistent voice agent in the layout (global interactive render mode) — keeps listening across navigation — 7/4/2026
- [x] Hands-free chain: product → recipes that use it → read a recipe, without touching the screen — 7/4/2026
- [x] open_page: recipes scoped to a product (`/recipes?uses={id}`) — 7/4/2026
- [x] Screen-aware references — "read me the second one" resolves against the on-screen list — 7/4/2026
- [x] "Back to assistant" hand-back from the recipe reader (button on read-aloud + spoken on cook-along) — 7/4/2026
- [x] Voice "read me the recipe" opens the listening cook-along agent, with graceful fallback to the plain reader — 7/4/2026

### v2.2 — Smarter recipe makeability
- [x] Recipe makeability by food family via per-product "Also works as" substitutes (recipes stay specific — real cook times) — 7/4/2026
- [x] "Also works as" list editable on the product page + AI Suggest — 7/4/2026
- [x] Assistant generates substitutes by voice/chat (suggest_substitutes tool, dashboard + product pages) — 7/4/2026
- [x] Recipes only match EDIBLE products (dog treats / cleaners can't masquerade as food) — 7/4/2026
- [x] Cook-along greets with an intro line then waits, instead of silent (firstMessage override) — 7/4/2026
- [x] Adapt: rewrite a recipe (swap missing mains + adjust cook times) to use what you have, saved as a variant — button + adapt_recipe voice/chat tool — 7/4/2026
- [x] Bubble-cloud alternate picker per ingredient (AI-generated + cached forms, green/red, click to adapt to that choice, with an ignored-pick guard) — 7/4/2026

---

## v2.3 — Full-site audit, BYOK, and fixes (7/5/2026)

### Audit hardening pass
- [x] Query splitting + AsNoTracking on read loads (kills the cartesian-Include warning) — 7/5/2026
- [x] Page error handling: log via ILogger, rethrow cancellation, stop leaking exception text — 7/5/2026
- [x] Resilient chat tool loop: a thrown tool handler becomes an error result, not a blanked box — 7/5/2026
- [x] Product Detail reloads when the route id changes — 7/5/2026
- [x] NotFound / Error pages use the design system — 7/5/2026
- [x] Quick-buy double-tap guard + SplitButton a11y + tidy EF write + table captions — 7/5/2026

### BYOK — bring your own key (public / source-available posture: deployed demo ships no usable keys)
- [x] Provider seam: IChatClientFactory (Anthropic + OpenAI) — 7/5/2026
- [x] Per-circuit AI clients built from the visitor's browser settings (keyless boot preserved) — 7/5/2026
- [x] Content-Security-Policy + security headers (script-src 'self'; strict in production) — 7/5/2026
- [x] Settings UI: provider, masked keys, per-module models, forget-my-key, session-only — 7/5/2026
- [x] Voice keyed per-circuit (server-side, per-request xi-api-key, rate-limited signed-url, pinned EL SDK) — 7/5/2026
- [x] Source-available README / BYOK setup docs (Whose-keys section: byok/managed/KeyMode + the honest key-custody story) — 7/9/2026

### Fixes
- [x] Short-cadence items now clear Running Low after a restock (DueSoon window capped inside the cadence) — 7/5/2026
- [x] "Recipes that use X" filter now finds adapted variants (non-matching original shown for reference) — 7/5/2026
- [x] Dev CSP relaxed in Development only so VS Browser Link / hot reload work (production stays strict) — 7/5/2026

### Demo data & onboarding (release-readiness)
- [x] "You keep running out of these" dashboard insight — burn rate ≪ rebuy rhythm, promoted from Product Detail — 7/5/2026
- [x] Synthetic demo-data seeder — messy + realistic, dates relative to "now", guarded to seed an empty DB only — 7/5/2026
- [x] First-run onboarding banner — BYOK + voice-key guidance + one-click "Load sample data" — 7/5/2026

---

## v3 — Accounts & multi-user (prerequisite for selling)
- [x] Authentication + accounts (ASP.NET Identity, static-SSR account pages, invite-code households) — 7/7/2026
- [x] Multi-user data isolation (household query filters + insert stamping on every pantry table) — 7/7/2026
- [x] Registration gate: `Auth:AllowRegistration` flag (first-user bootstrap + invite-join always open) — 7/7/2026
- [x] Logout kills every tab/device (security-stamp bump + 5-min circuit revalidation) — 7/7/2026
- [x] Per-household settings, receipt auto-scan, demo seeding, export/delete-my-data — 7/7/2026
- [x] Household panel in Settings (rename, invite code copy/regenerate, members) — 7/7/2026
- [x] Managed-key usage metering + daily quotas per household (the public-Azure gate) — 7/7/2026
- [x] OAuth external login (Google; config-gated, ships dark without credentials) — 7/7/2026
- [ ] Email: confirmation + password reset (needs an email sender) — Deferred (no email server)
- [ ] Household switching — Deferred

---

## v3.1 — Voice fixes & usability batch
- [x] Cook-along opens without config overrides — dropped the fragile `first_message` override (the WS-1008 arc); the agent greets from its own config — 7/8/2026
- [x] read_recipe deep link auto-starts from any page — `?read` consumed in OnParametersSet (a query-only nav never re-ran OnInitializedAsync) and stripped one-shot; the voice agent releases the mic before the hand-off — 7/8/2026
- [x] "Read the second recipe" from ANY page — read_recipe takes a 1-based `position`; the store's recipe list now matches the Recipes-page display order (newest first, variants under their original) — 7/8/2026
- [x] Multi-receipt upload — each image is its OWN receipt (sequential extraction, live per-receipt progress, results land in the review queue); "these are all one receipt" checkbox restores the merge for one long receipt — 7/8/2026
- [x] Grocery list "Restocked" beside Untrack — "already have it" clears the reminder (status-only signal, same write as the dashboard via one shared IPantryStore path) — 7/8/2026
- [x] Per-size price trends (the 3,000%-limes fix) — Trends ticker + Product Detail chart plot only the dominant size bucket (PriceSeries, Core, tested); loose/"each" spellings collapse into one bucket so quantity never splits a series; mixed-size items get a size label — 7/8/2026
- [x] Mobile hamburger nav — below 768px the eight links collapse behind a ☰ toggle (opens as a full-width column, folds on link tap, aria-expanded); desktop untouched — 7/8/2026
- [x] Predictor folds loose/"each" size spellings (SizeBucket, Core) — the cadence side of the 7/8 limes fix: null/"Each"/"1 ct" purchases are one size for dominant-size selection, so alternating extraction spellings can't stretch the learned rhythm; one shared bucketing for prices AND cadence — 7/12/2026
- [x] Receipts page (`/receipts`) — every receipt with date, merchant, and line-item total; disclosure per receipt reveals the lines (product links, qty, size, unit price, line total); pending-review chip; voice `open_page` can navigate to it — 7/12/2026

---

## Backlog (unscheduled)
- [x] Double-scroll fix (Grocery List + Upload review) — 7/2/2026
- [ ] CSV history importer — Parked (blocked on an itemized data export)
- [ ] More eval fixtures (paper / Edwards receipts) — Not complete
- [x] Per-size Trends price chart — 7/8/2026 (dominant-size ticker/chart; see v3.1)
- [ ] "Dapper blob" mascot / branding — Not complete
