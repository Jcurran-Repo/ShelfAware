# Shelf Aware — Feature Timeline

The master list of every feature, shipped and planned, by release phase/version.
Terse on purpose (no descriptions) — **git log** has the descriptions, **DESIGN.md** and
**CLAUDE.md** have the detail. This file exists so the full roadmap — including what *isn't*
done yet — survives even if everything else is lost.

**Terminology:** Phases 1–5 are the original v1 build milestones. v2 / v3 are later versions.
`[x]` + date = shipped · `[ ]` = not done yet.

_Last updated: 7/2/2026_

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
- [ ] README assets: demo.gif + accuracy screenshot — Not complete

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
- [ ] v2.0 — Push-to-talk (ElevenLabs STT + TTS, existing chat brain) — Not complete
- [ ] v2.1 — Conversational multi-turn — Not complete
- [ ] Recipe read-aloud — TTS plays a saved recipe — Not complete
- [ ] Voice control of read-aloud: stop / resume / next step (barge-in, pairs with v2.1) — Not complete
- [ ] Recipe cooking steps (data model + advisor) — enables step-by-step read-aloud — Not complete

### Prediction
- [ ] Two-stream cadence model (outage vs purchase dates tracked separately) — Not complete

---

## v3 — Accounts & multi-user (prerequisite for selling)
- [ ] Authentication + accounts — Not complete
- [ ] Multi-user data isolation — Not complete

---

## Backlog (unscheduled)
- [x] Double-scroll fix (Grocery List + Upload review) — 7/2/2026
- [ ] CSV history importer — Parked (blocked on an itemized data export)
- [ ] More eval fixtures (paper / Edwards receipts) — Not complete
- [ ] Per-size Trends price chart — Not complete
- [ ] "Dapper blob" mascot / branding — Not complete
