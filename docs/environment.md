# Environment, toolchain and workflow gotchas

Extracted from `CLAUDE.md` on 2026-09-19. Read this **before your first build** in a new session —
most of it is a trap that costs twenty minutes and teaches nothing.

---

## The toolchain

⚠️ **The SDK is not pinned yet** (`docs/remediation-plan.md` §2 is the fix). CI asks for `10.0.x`, i.e.
whatever the latest patch is that day, and the patches are not interchangeable: on **10.0.112** the
Razor parser reads a relational pattern at the start of a switch-expression arm as an HTML tag start,
so `< 0 => ...` inside `@code` produces hundreds of errors on a commit CI reports green. If a build
fails on `.razor` files with tag-mismatch errors, check the SDK patch before suspecting the repo.
`_ when days < 0 =>` is the equivalent that parses everywhere.

⚠️ **"0 Warnings" from an incremental build is vacuous.** MSBuild does not re-emit warnings for
up-to-date targets, so a `dotnet build` straight after `dotnet test` reports zero no matter what. Check
with `dotnet build --no-incremental -c Release`. CI has caught analyzer warnings that a local check had
"verified" away.

⚠️ **CI runs on `ubuntu-latest`; you probably develop on Windows.** A green local suite is not a green
CI. Paths are where this bites: `"C:\Users\..."` is not an absolute path on Linux — it is a *relative*
filename containing a colon and backslashes, so `Path.GetFullPath` resolves it under the working
directory. Build test paths from `Path.GetTempPath()` + `Path.Combine`. Same class as
`Path.DirectorySeparatorChar` and case-sensitive path comparison. Let a failed CI teach you rather than
re-running locally and shrugging.

---


- **CI runs on `ubuntu-latest`; you develop on Windows — a green local suite is not a green CI.** Paths are
  where this bites: `"C:\Users\..."` is not an absolute path on Linux, it's a RELATIVE filename that happens
  to contain a colon and backslashes, so `Path.GetFullPath` resolves it under the working directory. A test
  hardcoding one either fails there (if it asserts on the resolved value) or — worse — passes for a reason
  it isn't about. Build test paths from `Path.GetTempPath()` + `Path.Combine`. Same class of trap as
  `Path.DirectorySeparatorChar` and case-sensitive path comparison (see `PathScope`): the Linux behaviour is
  only ever exercised by CI, so **let a failed CI teach you rather than re-running locally and shrugging**.
  (Caught 2026-07-15: `Unconfigured_allows_any_local_path`, green on Windows 609/609, red on CI.)

- ⚠️ **Open `ShelfAware/` as the workspace — NOT the parent `ClaudeCodeSessions/`.** Claude Code scans
  for `.claude/` and looks for a git repo at whatever folder the session opened, so rooting a session at
  the parent silently disables things with errors that don't name the cause: `/code-review ultra` refuses
  ("not inside a git repository" — it clones the session's folder), `.claude/commands/pre-push.md` and
  `.claude/launch.json` aren't discovered, and every path has to be spelled out in full. The commands
  file at the PARENT is a pointer to this one for exactly that reason — it exists to survive the mistake,
  not to make it fine. Cost ~20 minutes on 2026-07-28 before the workspace was switched mid-session.
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
- **Blazor `IBrowserFile` handles die when their `<InputFile>` unmounts OR re-activates** —
  `_blazorFilesById` is per-element and replaced per change event. ⚠️ Since v4.8 (item 48) neither
  photo page holds a handle past its own change event: every picked file is read into memory AT
  SELECTION, so the old "keep the input mounted while extracting" rule is RETIRED there. The fact
  itself still governs any new `InputFile` use: read the bytes inside the change event, or your
  handles are one re-render/re-pick away from dead.
- **Browser-testing uploads without real files:** draw a receipt on a JS canvas in `preview_eval`,
  wrap in `File`/`DataTransfer`, assign to the input, dispatch `change`. `test-fixtures/` also has
  committed synthetic PNGs.
- `gh` CLI at `C:\Program Files\GitHub CLI\gh.exe` (full path in non-refreshed shells), authed as
  `Jcurran-Repo`. Remote: https://github.com/Jcurran-Repo/ShelfAware (public).
- Shell is Windows PowerShell 5.1 — no `&&`, no ternary; state-probing commands
  (`Get-NetTCPConnection` finding nothing) can exit 1 without being failures.
- ⚠️ **Never round-trip a source file through PowerShell 5.1 `Get-Content` → `Set-Content`.** Every file
  in this repo is UTF-8 **without a BOM**, and BOM-less is exactly the case PS 5.1 guesses wrong: it
  reads as the ANSI codepage and writes back as UTF-8, double-encoding every non-ASCII byte. One
  bulk-edit of two test files turned every `—` into `â€"` and every `×` into `Ã—` (2026-07-28, caught
  immediately and reverted from HEAD). This codebase is full of em-dashes, `×`, `≥` and `→` in comments
  and UI copy, so the damage is wide and a compile won't flag any of it. Use the editing tools for text
  edits; if a scripted rewrite is genuinely needed, `git diff` it before staging and grep the diff for
  `â€` / `Ã` as a tripwire.
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
