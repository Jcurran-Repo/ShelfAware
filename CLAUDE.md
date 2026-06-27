# CLAUDE.md — process notes for AI-assisted sessions

Working notes for Claude Code sessions on this repo. The authoritative spec is
[DESIGN.md](DESIGN.md) — read §0 (rules) and §10 (phases) before doing anything.
This file records build state, decisions, and environment quirks that the spec
and code don't capture.

**Audience & quality bar:** this is a portfolio piece with real users (Jordan +
his wife) and professional viewers (current + prospective employers). The goal is
to demonstrate production-ready work, so robustness, clean atomic git history,
tests, accessibility, and visual polish are in-scope and expected — not
gold-plating. Don't dismiss polish as overkill "because it's single-user."

## Build state (updated 2026-06-26)

| Phase (DESIGN.md §10) | Status |
|---|---|
| 1 — Skeleton + data (solution, entities, EF/SQLite, Products CRUD) | ✅ Done, acceptance verified |
| 2 — Extraction pipeline (IReceiptExtractor, upload/review/confirm, aliases) | ✅ Done, all 3 acceptance criteria verified with live API calls |
| 3 — Prediction engine + dashboard | ✅ Done, 15 engine tests green + dashboard verified in browser |
| 4 — Chat tools (IPantryChat) | ✅ Done, acceptance verified with a live tool-calling call |
| 5 — Azure deploy + README | ⬜ Next up |

Phase 2 verification details: synthetic Walmart receipt round-tripped to 6
confirmed PurchaseEvents; re-upload pre-matched all lines via aliases; a
non-receipt image returned zero lines and was discardable; API errors surface
as a friendly message with the receipt kept as PendingReview.

Phase 4 verification details: typing "we're out of dog food and almost out of
coffee" into the dashboard quick-update box produced two tool calls →
record_signal(OutNow) for Pedigree Dog Food and record_signal(RunningLow) for
Folgers Classic Coffee → dog food went Overdue + pinned, coffee went DueSoon,
with the one-line reply "Marked dog food as out and coffee as running low." Fuzzy
matching resolved both loose references; no console errors.

Phase 3 verification details: `ReplenishmentPredictor` (pure C# in
`ShelfAware.Core/Prediction/`) with 15 xUnit tests covering the §6-required
cases (2-event minimum, median + 3× outlier trim, every status boundary ±1 day,
each signal override, same-day collapse) — all green. Dashboard (`Home.razor`)
verified live: Running Low list bucketed Overdue/DueSoon correctly, "everything
else" held the Unknown ("still learning") items, and the **Bought today** quick
button round-tripped (wrote a purchase → engine recomputed median → row moved
to Stocked) with no console errors.

Post-Phase-4 polish (pre-deploy, browser-verified): dashboard rebuilt as
status-accented cards that surface plain-language urgency ("12 days overdue") and
header count chips; the user's signal note is split from the statistical basis
(`PredictionResult.SignalNote`); Products page median-interval column is populated
with status chips; thin-data items show honest "N more purchases to start
predicting" hints; a design-system stylesheet + responsive pass spans all pages.
The receipt review also gained per-line confidence chips, a primary/secondary
button hierarchy, and LLM-assisted product matching — verified live: an eggs line
printed as "EGGS LRG 12CT" (no brand) pre-filled the existing "Great Value Large
Eggs", while a genuinely new item correctly offered "create new".

Accessibility + Products overhaul (browser-verified): site-wide a11y pass —
`NavLink` for `aria-current="page"`, `:focus-visible` outlines, `aria-label`s on
all controls, icon glyphs marked `aria-hidden`, disambiguated repeated buttons
("Delete Bananas"), an `aria-live` status region for the chat reply, and
table `scope`/`caption`. The Products grid gained integrated in-header filters
(search/category/status/tracking), a "Next buy" estimate column with a detail
tooltip (last bought, typical quantity, expected cost), and a small trash-icon
delete; the temporary Phase 1 manual-purchase form was removed (purchases now
come from receipts, chat, and the dashboard quick buttons). A Grocery List report
page is the next piece.

## Decisions & deviations from the spec

- **`SignalKind.Restocked`** — the spec's enum value "ShelfAwareed" is a
  find/replace artifact (Restock→ShelfAware); implemented as `Restocked`.
  The same artifact appears in §6 and §7 — read those as "Restocked".
- **`ShelfAware.slnx`** not `.sln` — the .NET 10 CLI's default solution format.
- **Local data dir is `app-data/`** not `data/` — on case-insensitive
  filesystems `data/` collides with the `Data/` source folder in Web (the
  SQLite file once landed next to ShelfAwareDbContext.cs). Azure still uses
  `/home/data` via the `DataDir` config key.
- **Official Anthropic C# SDK (`Anthropic` NuGet) used directly** behind
  `IReceiptExtractor`, rather than wrapping in Microsoft.Extensions.AI
  `IChatClient` as §2 suggests. The interface seam satisfies the spec's real
  goals (swappable provider, testable without API calls); revisit only if a
  second provider actually appears.
- **Structured outputs** (`OutputConfig`/`JsonOutputFormat`) enforce the §5
  schema server-side, *plus* the spec's own validate-and-retry-once in C#.
  The schema omits `minimum`/`maximum` on confidence (unsupported in strict
  mode) — confidence is clamped in code instead.
- Extraction model pinned: `claude-haiku-4-5-20251001` (never aliases, per §2).
- **Chat = manual tool-call loop over the SDK (§7 Option B)**, not Semantic
  Kernel. Consistent with the existing direct-SDK choice for extraction; SK would
  add a dependency for no gain here. `AnthropicPantryChat` loops on
  `Messages.Create` with the 4 tools, executes each tool against `IPantryStore`,
  and feeds tool_result blocks back until the model returns its one-line reply.
- **`IPantryStore` (Core/Chat) is the chat data port** — Core defines it, Web
  implements it (`EfPantryStore`) so the chat layer touches no EF (§3). Chat
  purchases are recorded with `PurchaseSource.Chat`. Fuzzy name→product
  resolution lives in `ProductMatcher` (Core, unit-tested): exact → substring →
  token-overlap ≥ 0.5.
- **`PredictionResult.Pinned`** — added beyond the §6.7 field list to serve §8's
  "signal-pinned first" ordering. Set true when an active OutNow signal forces
  Overdue; the dashboard sorts pinned rows to the top. A Restocked signal is
  treated as a purchase-equivalent date (feeds the interval median and clears an
  earlier OutNow), which is how §6.6's "→ Stocked" falls out naturally rather
  than being forced.
- **`PredictionResult.SignalNote`** — the active user signal ("Marked out of
  stock" / "Marked running low") is surfaced separately from `Basis` so the UI
  presents the statistical prediction and the user's own statement as distinct
  cues, rather than concatenating them.
- **Cost estimates live in Web, not Core** — `ShelfAware.Web/Services/ShoppingEstimator.cs`
  combines the price-free Core prediction with quantity (median purchase qty) and
  unit price (average of confirmed `ReceiptLine.UnitPrice` for the product) into a
  `ProductEstimate`. The prediction engine stays pure timing statistics (§1/§6);
  money never enters Core. Used by the Products grid and the Grocery List page.
- **LLM-assisted product matching (extends §4)** — the extraction call now also
  receives the existing product list and returns a per-line `existing_product`
  (exact name or null) → `ExtractedLine.SuggestedProductName`. `ExtractAsync`
  gained an optional `knownProductNames` param (null → no matching, fully
  backward-compatible; the Evals harness is unchanged). Upload review pre-fills by
  trust order: learned alias → model suggestion → `ProductMatcher` (deterministic)
  → create new. §4 specified deterministic aliases only; this adds semantic
  matching where name understanding genuinely helps, which fits the README thesis
  ("LLMs where language understanding is required, plain code where it suffices").

## Environment & workflow gotchas

- **Stop the dev server before `dotnet build`** — a running server locks the
  DLLs and the build fails with MSB3027 after 10 retries.
- Dev server runs via the preview tooling: config name `shelfaware-web` in
  `.claude/launch.json` (repo root and one in the parent ClaudeCodeSessions
  folder), port 5179.
- **API key** lives in dotnet user-secrets, id `3d6755e6-9881-43a6-813c-fe3ebd974cd9`,
  key `Llm:ApiKey`. Editing that file by hand repeatedly failed for the user
  (Windows 11 Notepad unsaved-tab confusion + hidden AppData folder). If the
  key must change: have the user save the bare key to an easy location
  (Desktop key.txt), move it into secrets.json programmatically, delete the
  temp file. Never echo the key into chat or commit it.
- **Blazor InputFile**: the `<InputFile>` element must stay mounted while
  `IBrowserFile` streams are being read — unmounting (e.g. switching to a
  spinner view) breaks reads with `_blazorFilesById` null errors. Upload.razor
  hides the input with `hidden` instead of unmounting. Don't "simplify" this.
- **Browser-testing uploads without real files**: draw a receipt on a JS
  canvas in `preview_eval`, wrap in `File`/`DataTransfer`, assign to the input,
  and dispatch a `change` event. `test-fixtures/` also has committed PNGs
  (a synthetic Walmart receipt + a non-receipt image) intended to seed the
  Phase 5 eval harness.
- `gh` CLI installed at `C:\Program Files\GitHub CLI\gh.exe` (may need the
  full path in non-refreshed shells), authenticated as `Jcurran-Repo`.
  Remote: https://github.com/Jcurran-Repo/ShelfAware (public).
- Shell is Windows PowerShell 5.1 — no `&&`, no ternary; commands that probe
  state (`Get-NetTCPConnection` finding nothing) can exit 1 without being
  failures.

## Conventions

- Phases strictly in §10 order; do not start a phase until the previous
  phase's acceptance criteria pass. No scope beyond the spec (§0, §12).
- Prompts live in `src/ShelfAware.Llm/Prompts/` as embedded resources —
  iterate the prompt there, not in C# string literals.
- Core has no LLM and no EF references; the DbContext lives in Web.
- Commit style: phase-scoped commits, message body explains what was verified
  and any spec deviations.
