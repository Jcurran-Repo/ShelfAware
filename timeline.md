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
- [x] Extraction skips never-delivered "Unavailable" order lines — caught by a real phantom purchase (5/22 avocados, ordered-but-out-of-stock, never charged); prompt rules 4+9 now skip by fulfillment status, pinned by a new 77-line hand-labelled 5/22 fixture (the largest yet); eval tokenizer folds singular/plural wobble; suite now 4 receipts / 160 lines at 100% recall / 100% precision / 99% field — 7/12/2026
- [x] Verified receipts become YOUR accuracy fixtures — an explicit "I checked every line" opt-in (Upload review checkbox, or retro-verify on the Receipts page; machine confirms can never set it); /accuracy gains "Your receipts": on-demand re-read of each verified receipt from its stored audit copy, scored against the confirmed lines by the same ExtractionScorer (now in Core, shared with the offline harness); last run persists per household; token-cost disclaimer; "Export fixture labels" downloads the expected.json shape. Receipt.VerifiedForEval ships via the new post-v3 AdditiveSchema seam — 7/12/2026
- [x] AI token usage visible to users — usage recorded in EVERY key mode (BYOK included; quotas still enforced only on managed keys), Settings gains an "AI usage" panel (today's calls/tokens/voice sessions + 14-day daily table), and the accuracy check shows what today has spent — 7/12/2026
- [x] Grocery list "Coming up" walks the store — same aisle-then-urgency order as Buy now, so the whole page reads as one list (the date column still carries chronology) — 7/12/2026
- [x] Duplicate guard on manual product adds — the Products form and the chat's create_product both resolve through ProductMatcher before inserting; exact dupes are blocked with a link to the existing product, near-misses ("Dog Food" vs "Dry Dog Food") prompt use-existing / create-anyway (a twin product would split purchase history) — 7/12/2026
- [x] Substitutions respect the curated matrix everywhere — Adapt receives every on-hand product's "also works as" list (prompt rule 9: prefer curated stand-ins; matched_product = name only), and swap bubble-clouds show your own stand-in products first (SwapCloud, Core, tested; token-equal self-swaps excluded; AI generic forms dedupe behind them; clouds draw from every tracked edible product, out-of-stock renders "grab") — 7/12/2026
- [x] Adapted recipes can adapt and swap too — variants re-root: the variant's content is the base the advisor rewrites, but the result saves as a sibling under the ORIGINAL (flat family, no chains; the signature dedupe keys on the original; the reply names the family) — 7/12/2026
- [x] Red recipe rows explain themselves — born from Jordan's real "93% Lean Ground Beef won't match" confusion (the product was predicted-out, and nothing said why the row was red): suggestion cards now fall back to the plain-code matcher when the model's matched_product is null (pre-save can never disagree with post-save), and when a red row's covering product is merely predicted run-out the row says "you may still have X — it just looks run-out" with a one-tap Restocked (new PantryOnHand.EdibleOutOfStock, the exact complement of on-hand; same status-only signal as the dashboard) — 7/12/2026
- [x] Red rows also explain UNTRACKED coverage — the third red-row cause, found live in Jordan's real data (his only ground beef was untracked, so no pool saw it): "you have this as X, but it's untracked" + one-tap Track it (PantryOnHand.EdibleUntracked; run-out hint wins when both could apply; after re-track the row recomputes honestly — green if stocked, run-out hint if overdue) — 7/12/2026
- [x] "Get ideas" batches survive navigation and restarts — an AI call's results no longer evaporate as page state: the latest batch persists per household (SettingKeys.LastRecipeSuggestions JSON, the SelfEvalResults pattern) with an "Ideas for '…' — date" header and a Clear-ideas button; replaced only on a SUCCESSFUL new batch (a failed call keeps the old cards), and ✓/🛒 marks recompute live against the current pantry on every render (Have/ToGrab are [JsonIgnore], pinned by round-trip tests) so a stale batch stays truthful — 7/12/2026

---

## v3.3 — Own the voice loop
- [x] The reader speaks the model's language — nothing normalized the text, so "Simmer 6-7 min/side at 350°F" went to TTS verbatim. ElevenLabs' own docs: Flash v2.5 mis-reads numbers ("$1,000,000" → "one thousand thousand dollars"), normalization is off by default there for latency, and forcing it on is Enterprise-only — so `SpeechText` (Core, pure, tested) does it: fractions ("1/2 tsp" → "half a teaspoon"), mixed numbers, units with number agreement, temperatures, dimensions, ranges, "/" as "per". Refuses to guess where a guess would be wrong ("2 C flour" stays cups, not Celsius) — 7/14/2026
- [x] Narration starts at once — the reader synthesized every segment before playing any, so a ten-step recipe sat silent behind "Preparing narration…" for eleven round-trips; that, not the voice, was most of why the button reader felt worse than the realtime agent. It now plays the intro and appends steps as they land, parking (not finishing) when playback outruns synthesis. Plus `previous_text`/`next_text` for intonation across the cut, and configurable `voice_settings` (speed 0.90, set by ear; 0.85 is the floor) — 7/14/2026
- [x] A recipe costs one synthesis, however many times you read it — content-addressed disk cache keyed on text + neighbours + `ITextToSpeech.OutputFingerprint` (the provider declares what about its config changes the audio; excludes the API key, includes how we spell text out, so improving the spelling retires the old clips). A hit needs no key at all — which is what lets seeded/demo recipes talk for a visitor who brought none. Bounded by `Speech:CacheMegabytes`, swept at startup — 7/14/2026
- [x] **Cook-along is ours** — the built-in hands-free reader replaces the ElevenLabs agent as the primary action; the agent moves into the caret as "Live agent" (BYOK only, billed per minute, kept because interrupting mid-sentence is the one thing our loop can't do). Half-duplex by design: it listens BETWEEN steps, where a cook actually talks, which sidesteps needing echo cancellation good enough to hear "stop" under the voice saying "stop". `read_recipe` lands here now, so the hands-free chain no longer needs a configured agent — 7/14/2026
- [x] "next" costs nothing — `CookAlongCommands` (Core, pure, tested) resolves next/back/repeat/step N/start over/hold/stop with a string comparison and a cached clip: no model call, no round-trip, no per-minute meter. That's why the built-in loop can be FASTER than the realtime agent, which round-trips an LLM to work out that "next" means next. Whole-utterance matching keeps "what's next" a command while "what's next after the salt" stays a question — 7/14/2026
- [x] Anything the grammar doesn't own goes to the same brain — questions reach `IPantryChat` with the recipe as screen context (the mechanism that already resolves "the second one"), so no new brain API was needed. That fall-through is the difference between a voice remote and a cook-along — 7/14/2026
- [x] The grammar stopped having to be perfect — a miss used to be WRONG (the brain answered "up next" instead of doing it), so the phrase list was load-bearing and had to enumerate every way a human says "next" through a cough or a stutter. The new `go_to_step` tool lets the model move the reader, so a miss is merely SLOW. The grammar is an optimisation now, not a gate — 7/14/2026
- [x] Speech is not tidy, and the grammar stopped pretending — Scribe tags audio events INTO the transcript by default ("Next (coughing)"), so we ask it not to and strip annotations anyway; a command repeated before the pause elapses ("next next") is still that command; and `language_code` is named rather than detected, because a clean one-word "Next." came back only 33% sure it was English — 7/14/2026
- [x] Calibrated listening, not guessed — every threshold was a made-up number, and the reason 900ms was a guess is that it's a fact about a PERSON. Settings gains a wizard (stay quiet → say "next" → ask a question) that measures your room, your microphone, and your longest mid-sentence pause. The browser measures; the policy (`ListeningSettings`, Core, tested) decides — including refusing to conclude anything from a run that never heard you. Per device, own storage key — 7/14/2026
- [ ] The intermittent step-jump — jumping to a step occasionally left "next" advancing from the old index, then wouldn't reproduce. Every static path says it can't happen, so it's timing. The reader now logs what it resolved (and, at Debug, what it heard) — Open

---

## v3.4 — An invite code is an act, not a fixture
- [x] A household starts with NO invite code — the 7/15 hardening pass made codes expirable, limitable, and revocable, but every household still *had* one from birth: a bearer credential to a whole pantry, printed on a settings page forever, whether or not anyone had ever wanted to invite a soul. The lifetime was the fixable part; the shape was the wrong part. `CreateForAsync` stops minting, `GenerateInviteCodeAsync` mints on request (single-use by default — inviting one person shouldn't hand out a key that admits a crowd), and `ClearInviteCodeAsync` revokes in one click instead of "mint a replacement", which used to mean leaving a live credential lying around as the price of killing one — 7/15/2026
- [x] Spending the last use retires the code — a used-up code is refused either way, so this changes no access decision; what it changes is that a household can no longer be *holding* a dead credential that reads as a live one, and "nobody has been invited" stops being indistinguishable from "somebody already came". Done in the same `ExecuteUpdate` that claims the use — a follow-up write would reopen the exact race the conditional claim exists to close — 7/15/2026
- [x] "No code" is NULL, not "" — and the unique index is why: SQLite counts NULLs as distinct, so every code-less household coexists, while `""` would let exactly ONE household have no code and fail the second registration on the deployment. SQLite can't ALTER a column to nullable, so this needed `NullableInviteCodeMigration` — the documented exception to `AdditiveSchema` (which stays additive-only, and stays honest, by not being the thing that does this). Guarded, transactional, idempotent, and it asserts the column set it knows rather than silently dropping a column added later — 7/15/2026
- [x] The migration wipes existing codes — every one was minted permanent and unlimited under rules that no longer exist, so carrying one across would import precisely the credential this change stops issuing. It evicts nobody: membership isn't the code. Verified by dry-running the migration against a copy of the live auth.db before merge — 7/15/2026

---

## v3.5 — Variety (flavor as per-purchase metadata)
- [x] `Variety` on ReceiptLine + PurchaseEvent + extraction — flavor/varietal tracked like Brand and Size: extraction strips it from the item name into its own field (Kool-Aid Strawberry Drink Mix → "Drink Mix" / Kool-Aid / Strawberry), so every flavor rolls into ONE product and the cadence is the item's collectively; cut/form/lean% stay in the name (Whole Milk keeps Whole). Live-verified end-to-end via auto-import of a synthetic receipt — 7/17/2026
- [x] Product Detail "Varieties bought" split (count · last bought · avg price, pooled across brands — both brands' strawberry are one row) + Variety column in Recent purchases + editable Variety column on Upload review — 7/17/2026
- [x] Product merge (⇆ on Product Detail) — folds a split product into its item: moves purchases/lines/aliases/signals before the delete (one transaction), unions tags + substitutes, re-points name-keyed recipe links, and labels moved purchases' variety with a name-diff pre-fill ("Strawberry Drink Mix" → "Drink Mix" suggests "Strawberry"). The repair path for pre-variety history, and for dup-guard "Add anyway" twins generally — 7/17/2026
- [x] Demo seeder varieties (Drink Mix hero across two brands + four flavors, apple varietals, yogurt flavors) — 7/17/2026
- [x] Variety shown everywhere brand is (grocery list, products grid, dashboard cards, copy/print text) — usual variety with "+N", and a tap-to-expand breakdown of every brand and variety bought (native details, phone-friendly) — 7/17/2026
- [x] Buy-quantity is a TRIP's worth: same-day lines sum before the median, so 3 Gala + 3 Honeycrisp on one receipt recommends 6 apples (est. cost follows); demo data buys realistic multiples — 7/17/2026
- [x] Merge panel narrows candidates by tag — the same tag CLOUD as the Products page (counts, busiest first, tap to toggle, Clear ✕), pre-filtered to the product's own first tag (kin share a tag); a target hidden by a filter change resets rather than staying silently selected — 7/17/2026

---

## v3.6 — Expiration dates (opt-in)
- [x] `ExpirationDate` on ReceiptLine + PurchaseEvent — the label's date as per-purchase metadata (like Brand/Size/Variety), human-entered only: receipts don't print it, so extraction never touches it. Only the LATEST purchase's date governs (rebuying supersedes the old jug; same-day ties take the longest date), and nothing feeds either cadence rhythm — a label is a fact about the food, not about buying behavior — 7/18/2026
- [x] Derived expired-state in the engine, not a fired event — past the label (the "best by" day itself is still good) the item pins Overdue with the label as its due date; a state can't double-fire, miss a day the server slept through, or re-flag after an override. Requested by a demoee; built because perishables are the strongest replenishment category and a dated label is signal the cadence can fundamentally never infer — 7/18/2026
- [x] Restocked-after-the-label OVERRIDES it, visibly — "I froze it" beats the sticker, and the expiration panel says "overridden" instead of silently not firing (the human must never wonder why a date they set stopped counting) — 7/18/2026
- [x] Surfaces: optional Expires column on Upload review (typo + already-past warnings), Expiration panel on Product Detail (state story + date editor through the ONE write path), dashboard cards say "Expired Jul 16" as their own note — the honest reason a card is red, distinct from the user's own "Marked out" — 7/18/2026
- [x] Per-household Settings toggle, default OFF — the most ritual-heavy field in the app is opt-in, and off is dormant, not destructive (dates kept, nothing fires or renders; engine default fails inert on purpose). Expired items also leave recipe on-hand (PantryOnHand threads the flag); the backtest stays expiration-blind so it grades predictions, not labels — 7/18/2026
- [x] `set_expiration` chat/voice tool — "the milk expires Friday" is a future-looking label fact, never an OutNow; the system prompt now carries today's date (with weekday) so the model resolves relative dates itself; unparseable dates error rather than silently clearing. Live-verified through the quick-update box — 7/18/2026
- [x] The label HARD-CAPS the due date (min(rhythm, label), escalate-only) — the cadence estimates how long stock usually lasts, the label bounds how long it CAN, so an expiring item flows into Due Soon → the lists BEFORE it dies through the existing machinery (no expiration columns on any grid, deliberately); a still-learning item gets a real due date from its label alone; a post-label Restocked stands down pin AND cap while a casual pre-label Restocked can't silently disarm the feature — 7/18/2026

---

## v3.7 — Reports tab (printable, configurable)
- [x] `MealEvent` — "Ate it" records WHEN, not just how often (the counter stays for Pick-for-me and carries pre-log history the event log honestly can't); demo seeder writes a dated meal log; first post-v3 NEW TABLE via AdditiveSchema.EnsureTable (DDL lifted from EF's own create script + schema-parity test, so migrated and fresh DBs cannot drift) — 7/18/2026
- [x] Report engine (Core, pure, zero LLM) — ReportSpec in, honest series out: continuous calendar buckets (Mon weeks/months/quarters), and the honesty rules as CODE shared by the builder UI and the engine (quantity never sums across products; unit price = dominant-size PAID only, gaps not zeros; tag series overlap by design so they never stack or total; partitioning splits POOL their remainder — dropping small categories from a stacked chart falsified its total, caught live; every exclusion disclosed in a note) — 7/18/2026
- [x] Hand-rolled SVG charts (no vendor) — TimeSeriesChart + BarChart (grouped/stacked) + legend + always-rendered data table (the a11y/print relief); 8 validated categorical color slots vs the app's real surfaces in both modes, fixed order = the colorblind mechanism, 2px surface gaps between fills, zero-based axes always — 7/18/2026
- [x] `/reports` presets, print-first — Monthly report card (tiles + stacked aisle spend + top items + movers), What's costing more? (PriceWatch: spend-weighted personal grocery inflation with honest "based on N of M items" disclosure + refusal below 3), What we actually eat (meals/week + cost-per-meal at today's receipt prices), Waist watch (kcal/week from the meal log, "ballpark by design"), Waste watch (ExpirationOutcomes judges dated purchases from EVIDENCE — Superseded/MarkedOut/Overridden/PassedQuietly; says "worth checking" with $ at stake, NEVER "wasted"; gated on the expiration opt-in), Gap report (burn vs rebuy across the household — "out ~N days before you rebuy") — 7/18/2026
- [x] Custom builder + saved reports + deep links — by-product AND by-tag reports, live rule objections that disable Run, spec ⇄ URL round-trip (ReportSpecUrl is THE one serializer; saved rows store the query form), SavedReport walks the full tenancy/export/delete drill — 7/18/2026
- [x] Chat/voice: `open_page` reports + named report ("show me the waste report" navigates; unknown names degrade to the report card) — 7/18/2026
- [x] Pre-push gate findings fixed pre-merge (empty-series chart crash; TopN chart-color cap — which itself 500'd the report card's top-10 TABLE until the cap learned tables have no colors; printed legend swatches stripped by the browser → print-color-adjust + hairline border; the assistant button never prints) — 7/18/2026
- [x] Charts answer clicks — aisle segments/legend chips → /products?category=, tag series → ?tag=, top-item rows → /product/{id}; the three "everything else"s open (Untagged cleanup chip + ?untagged=1, pooled small aisles → ?categories= multi-filter with a visible named-and-clearable note, Other-aisle chip); pooled/synthetic series with no honest destination stay plain; + app-wide aria-pressed sweep (literal true/false — a bound bool renders an empty attribute) — 7/18/2026

---

## v3.8 — Folder import retired; Smart confirm moves to uploads
- [x] `ReceiptAutoConfirmer` — the folder importer's graduated-trust brain, kept and re-aimed at uploads: single, combined, and batch uploads all route through the household's ImportMode (Review/Smart/Auto) after the pending receipt is persisted, so a trusted receipt records itself and review never appears. Same contract as before: alias or ≥0.8-confidence match to a known product, machine confirms never write aliases and are never eval ground truth — 7/22/2026
- [x] One deliberate tightening: Smart now queues a receipt with NO detected purchase date (the date drives every prediction; "assume today" is the silent guess review exists to catch). Auto keeps its all-or-nothing contract (undated = today) — 7/22/2026
- [x] Folder-import transport REMOVED (inbox, drop-folder setting, startup scan, Settings "Scan now", `import_receipts` chat tool, `Receipts:AllowedRoot` policy) — built for the bootstrap era's mass imports, superseded by multi-receipt upload, and on a box shared beyond the household it was the app's one arbitrary-path filesystem read. Deleting the surface beats confining it; "import my receipts" now lands on the Upload page via open_page — 7/22/2026
- [x] Upload page says what will happen (active-mode hint) and what happened (per-receipt "recorded automatically" vs "in the review queue"; auto-confirm summary matches the manual one) — 7/22/2026

---

## Backlog (unscheduled)
- [x] Double-scroll fix (Grocery List + Upload review) — 7/2/2026
- [x] Photo-upload fix (CSP `img-src blob:` + bounded resize) — 7/21/2026 (the first real photo upload hung forever: the strict CSP blocked Blazor's in-browser resize and its JS never settles the promise; PDFs skip the path, so it hid since 7/5)
- [ ] CSV history importer — Parked (blocked on an itemized data export)
- [ ] More eval fixtures (paper / Edwards receipts) — Not complete
- [x] Per-size Trends price chart — 7/8/2026 (dominant-size ticker/chart; see v3.1)
- [ ] "Dapper blob" mascot / branding — Not complete
