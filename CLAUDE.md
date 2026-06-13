# CLAUDE.md — process notes for AI-assisted sessions

Working notes for Claude Code sessions on this repo. The authoritative spec is
[DESIGN.md](DESIGN.md) — read §0 (rules) and §10 (phases) before doing anything.
This file records build state, decisions, and environment quirks that the spec
and code don't capture.

## Build state (updated 2026-06-12)

| Phase (DESIGN.md §10) | Status |
|---|---|
| 1 — Skeleton + data (solution, entities, EF/SQLite, Products CRUD) | ✅ Done, acceptance verified |
| 2 — Extraction pipeline (IReceiptExtractor, upload/review/confirm, aliases) | ✅ Done, all 3 acceptance criteria verified with live API calls |
| 3 — Prediction engine + dashboard | ⬜ Next up |
| 4 — Chat tools (IPantryChat) | ⬜ |
| 5 — Azure deploy + README | ⬜ |

Phase 2 verification details: synthetic Walmart receipt round-tripped to 6
confirmed PurchaseEvents; re-upload pre-matched all lines via aliases; a
non-receipt image returned zero lines and was discardable; API errors surface
as a friendly message with the receipt kept as PendingReview.

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
