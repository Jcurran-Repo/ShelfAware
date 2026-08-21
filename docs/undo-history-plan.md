# Undo history & the `/history` page — implementation plan

Handoff spec for a fresh context. **Scope: the whole feature in one branch** — an activity log,
per-action undo, an inline Undo affordance, and a `/history` page. Redo is a documented *next step*,
not built here.

Read `CLAUDE.md` §0 rules first. This feature lives squarely on top of two of its hardest-won
patterns — **precondition-checked reversal** (`ReceiptRemovalService`, `MealStock.Matches`) and the
**tenancy drill** for a new household table — so follow those, don't reinvent them.

---

## 1. Goal & shape

Every undoable action a household takes is recorded, and can be reversed two ways:
- **Inline Undo** — a brief "↩ Undo" right after the action (reuse the **Ate-it** notice + the
  **receipt-upload Undo** patterns that already exist).
- **`/history` page** — the household's actions, newest first, each with an Undo button where it's
  still reversible; the rest greyed. A **"Show all history"** expander reveals the full retained log
  with the extra entries greyed/de-emphasized (same shape as the `/admin` ErrorLog resolved drawer,
  item 49).

One backbone, two surfaces: **`ActivityLog`** is the single source of "what happened + how to reverse
it", and **both surfaces call the same `ActivityLogService.UndoAsync(entryId)`** — never fork the undo
logic per surface.

---

## 2. The load-bearing rule: undo is precondition-checked, NEVER blind

This repo's signature failure is two places disagreeing about one fact. An undo that fires blind is
exactly that, across time — it reverses to a past state the world has since moved past. So:

> **Every undo re-reads current state and reverses ONLY if that state still matches what the entry
> recorded. Otherwise it refuses, with a specific reason.**

This is the same discipline as `ReceiptRemovalService` (reverse by stored provenance ids, keep a
product that gained other history) and `MealStock` (re-plan in the commit's own context, refuse if the
numbers moved — items 21, 27, 28). Copy that posture.

Each entry is therefore always in exactly one **state**, shown on the page:

| State | Meaning | On the page |
|---|---|---|
| **Undoable** | reversible kind · not undone · current state still matches the record | Undo button |
| **Undone** | already reversed | greyed, "undone <date>" |
| **Superseded** | a later action changed the same thing since | greyed, "can't undo — changed since" |
| **Not reversible** | the kind has no clean inverse (e.g. merge) | greyed, "can't be undone" |

---

## 3. Schema — `ActivityEntry`

A household-owned entity (Core domain; EF config in `ShelfAwareDbContext`). Columns:

- `Id` (int, PK)
- `HouseholdId` — implements `IHouseholdOwned` (global query filter + SaveChanges stamping)
- `OccurredAt` (DateTimeOffset)
- `Kind` (enum `ActivityKind`)
- `Summary` (string) — the human-readable line, **computed by the Kind's handler AT RECORD TIME** so
  the page renders no per-kind logic and the wording lives in one place (the "one definition" rule).
- `Reversibility` (enum: `Reversible` / `NotReversible`) — from the handler; drives page greying.
- `UndoneAt` (DateTimeOffset?) — null until undone.
- `PayloadJson` (string) — the kind-specific data the handler needs both to **reverse** and to **detect
  supersession** (e.g. created row id; or `target id + oldValue + newValue`).
- `Source` (enum/string, optional) — "dashboard" / "chat" / "grocery list", for the summary.

⚠️ `DateTimeOffset` cannot be used in a SQLite SQL `ORDER BY` (item 47) — order by `Id` (insert order
IS chronological) or materialize then sort client-side.

New table → **`AdditiveSchema.EnsureTable`** (the post-v3 new-table pattern; DDL lifted from EF's
`GenerateCreateScript()`), plus the schema-parity test that compares migrated vs fresh
`sqlite_master`. On `shelfaware.db` (household data), not `auth.db`.

---

## 4. The handler pattern — `IUndoHandler`

One handler per `ActivityKind`. Each owns its payload shape, produces the `Summary`, declares its
`Reversibility`, and reverses with precondition checks. Registered by Kind (DI keyed set or a
dictionary). **Add an action → add a handler; nothing else changes.**

⚠️ **Placement (settled 2026-08-17):** `IUndoHandler` takes `ShelfAwareDbContext`, so it — plus
`UndoResult`, every handler, and `ActivityLogService` — lives in **Web**, NOT Core (CLAUDE.md: "Core
has no EF references"). Only the `ActivityEntry` entity and the `ActivityKind` / `Reversibility` enums
live in Core (they're entity columns). The handlers sit in Web/Data beside the reversal services they
reuse (`ReceiptRemovalService`, `MealStock`, `ReportResolutionService`, `ProductRenameService`).

```csharp
enum UndoResult { Done, Superseded, Gone, NotReversible }

interface IUndoHandler
{
    ActivityKind Kind { get; }
    Reversibility Reversibility { get; }
    // Reverse the entry using its payload, within the caller's household-scoped context.
    // MUST re-read current state and refuse (Superseded/Gone) rather than clobber.
    Task<UndoResult> UndoAsync(ShelfAwareDbContext db, string payloadJson, CancellationToken ct);
}
```

Recording happens in the SAME context/transaction as the action wherever possible, so a failed action
logs nothing (and a logged action really happened).

---

## 5. `ActivityLogService` (Web/Data, scoped)

- `RecordAsync(kind, summaryOrPayload)` — serialize payload, stamp household, insert. Prefer calling
  it from inside the action's own write so they commit together.
- `UndoAsync(entryId)` — load the entry **through `IHouseholdDbFactory`** (household-filtered), dispatch
  to the Kind's handler, stamp `UndoneAt` on success; return a **typed outcome** for the UI
  (Done / already-undone / superseded / not-reversible / gone).
- `GetHistoryAsync(take, skip)` — for the page (recent window + expandable full log).
- `TrimAsync()` — bound the table (row cap like `ErrorLogStore.MaxRows`); trim oldest.

⚠️ **This is the app's SECOND cross-row write-orchestration** (after `ReceiptRemovalService`). It must
**never** use `IgnoreQueryFilters`; every row an undo deletes or edits is reached through the
household-filtered context, and `EnforceHousehold` already refuses a cross-household write. The
filtered entry load is what guarantees an undo can't touch another household's data — state that
plainly in a comment and pin it with an isolation test (household B cannot undo household A's entry).

---

## 6. Recording layer — at the data layer, not the button

Record inside `IPantryStore` implementations and the confirm/edit services, **not** in each page
handler. Why: one write-path per action (the "one definition" rule), and **chat/voice actions get
logged for free** because they already go through the store.

**Required consolidation:** the dashboard's `BoughtToday` (`Home.razor`) writes
`db.PurchaseEvents.Add(...)` inline. Move it onto **`IPantryStore.AddPurchaseAsync`** (which the chat
`add_purchase` tool already uses) so the purchase and its log entry are one recorded path shared by the
dashboard and chat. This is the same "logic private to a page is logic no test can reach" cleanup as
`MealStock`/`SpendForecast` (items 20/21).

---

## 7. Action inventory

Real write sites, from `IPantryStore` (`src/ShelfAware.Core/Chat/IPantryStore.cs`) and
`src/ShelfAware.Web/Data/*Service.cs`. **Verify each site + exact signature before coding** — this table
is the map, not the territory.

| # | Action | Write site | Reverse | Class | Notes |
|---|---|---|---|---|---|
| 1 | Bought today | `Home.razor` → move to `AddPurchaseAsync` | delete the created `PurchaseEvent` by id | Reversible | consolidate the inline write first |
| 2 | Add purchase (chat) | `IPantryStore.AddPurchaseAsync` | same as #1 | Reversible | covered once #1 records in the store |
| 3 | Restocked / Out / Running low | `IPantryStore.RecordSignalAsync` | delete the created `InventorySignal` by id | Reversible | signal rows are never pruned elsewhere — safe to delete the exact one |
| 4 | Edit purchase quantity | `SetPurchaseQuantityAsync` | restore old qty **iff** current == the qty this set | Reversible-precond | payload `(purchaseId, oldQty, newQty)` |
| 5 | Edit purchase brand | `SetPurchaseBrandAsync` | restore old brand, same precond | Reversible-precond | |
| 6 | Set count | `SetQuantityAsync` | restore old count **and** `QuantityCountedAt`, precond | Reversible-precond | restore both; refuse if re-counted since |
| 7 | Set expiration | `SetExpirationAsync` | restore old date, precond | Reversible-precond | |
| 8 | Set default unit | `SetDefaultUnitAsync` | restore old unit, precond | Reversible-precond | |
| 9 | Start/stop tracking | `SetTrackingAsync` | flip back, precond | Reversible-precond | |
| 10 | Create product | `CreateProductAsync` | delete the product **iff** it gained no other history since | Reversible-precond | mirror `ReceiptRemovalService`'s "keep if it has other history" |
| 11 | Add tags | `AddTagsAsync` | remove the tags this actually added | Reversible | payload = the *newly-added* subset (not ones already present) |
| 12 | Add substitutes | `AddSubstitutesAsync` | remove the ones this added | Reversible | as #11 |
| 13 | Add grocery extras | `AddGroceryExtrasAsync` | remove the extras this added | Reversible | |
| 14 | Ate it | `MealStock` (Apply) | **reuse `MealStock.Restore`** (exists) | Reversible-precond | Restore already commutes with intervening changes (item 28) |
| 15 | Confirm receipt | `ReceiptConfirmationService` | **reuse `ReceiptRemovalService.RemoveAsync`** | Reversible-precond | already the receipt-upload Undo; provenance-checked |
| 16 | Rename product | `ProductRenameService` | restore old name + re-point recipe links, precond | Reversible-precond | uses `ProductMatcher.IdentityKey` today — keep it |
| 17 | Resolve / reopen report | `ReportResolutionService` | flip back (reopen already exists) | Reversible | |
| 18 | Remove receipt | `ReceiptRemovalService` | ⚠️ no inverse (re-adding a removed receipt isn't built) | **Not reversible v1** | log history-only, greyed |
| 19 | Census confirm | `CensusConfirmationService` | ⚠️ needs a NEW inverse (revert the summed per-product attestations) — hard | **Not reversible v1** | log history-only, greyed (see §10) |
| 20 | Merge products | `ProductMergeService` | ⚠️ merge is **lossy** (moves purchases/aliases/signals, unions tags, deletes source) | **Not reversible** | log history-only, greyed |

**Soft actions — SETTLED with Jordan (2026-08-17):** log **exclude-food add/remove** and **recipe
save / adapt** (both reversible — remove the excluded-food row; delete the saved/variant recipe).
**Skip settings/config** for v1 (pure-config isn't household-data history). `add-to-list` is already
covered by #13 (`AddGroceryExtrasAsync`).

---

## 8. The `/history` page

- Route `/history`, `[Authorize]`, household-scoped, uses `MainLayout`.
- Newest-first, grouped by day. Each row: time · `Summary` · state.
- **Undoable** → **Undo** button → `ActivityLogService.UndoAsync` → typed outcome → refresh + a
  `role=status` message. Split the failure advice by point-of-failure (item 27): "Undone." vs
  "Couldn't — something changed since." vs "This kind can't be undone."
- **Non-undoable** (undone / superseded / not-reversible) → greyed, short reason, no button.
- **Default view = recent** (last ~30 days or ~25 entries). **"Show all history"** expander reveals the
  full retained log with the extra greyed/de-emphasized.
- A11y (item 49's residuals): `@key` per row, a persistent visually-hidden status announcer mounted
  before any action, year in date stamps; focus-after-action is a known residual, announcer covers it.
- A small footer/nav link to `/history` (like the bug-report footer link, item 47).

---

## 9. Inline Undo surfaces

- Dashboard **Bought today** / **Restocked**: after the write, show "Recorded X · ↩ Undo" (reuse the
  Ate-it notice component/pattern). Undo → `UndoAsync(thatEntryId)`.
- Grocery-list Restocked, product-detail actions, etc.: the same affordance where it fits.
- The inline Undo and the `/history` Undo are the **same service call** — the inline button is just
  "undo the entry I just made."

---

## 10. Non-reversible actions — the honest classification

"Whole scope" means the **log + page cover every action**; it does **not** mean every action gets a
working undo, because a few genuinely can't:
- **Merge** is lossy → history-only, greyed, permanently.
- **Receipt removal** and **census confirm** *could* get inverses, but each is real work (re-adding a
  removed receipt; reverting summed attestations). **Recommendation for v1: log all three as
  history-only (greyed, "can't be undone")**, and build the census/receipt-removal inverses as a
  fast-follow. This keeps "whole scope" true for the record while not overpromising undo on the hard
  ones. ⚠️ Confirm this call with Jordan — it's the one place the "whole scope in one go" instruction
  meets "not cleanly reversible."

---

## 11. Cross-cutting (all mandatory)

- **Tenancy drill for the new table** (the full checklist, per every prior new table): query filter +
  stamping, `AdditiveSchema.EnsureTable` + schema-parity test, **isolation test**, export in
  `data.json`, **delete-my-data** (the log is `UserContent` — it names products, dates, merchants —
  so it MUST be wiped with the household's data; the whole AppSettings-style table delete already
  exists as a precedent, item 33), `CountAll`.
- **Security (the `/pre-push` gate will check):** no new endpoint (the page is a Blazor component;
  undo is a scoped service call). No `IgnoreQueryFilters`. Every undo write reached through
  `IHouseholdDbFactory`. If retention needs a config key, classify it and default it to unbounded for
  the self-host (like the AI quotas).
- **Retention:** `ActivityLog:MaxRows` (~500/household, null = unbounded). Trim oldest at write or
  startup, like `ErrorLogStore`. ⚠️ trimming drops an entry's undo — acceptable for old actions.
- **Tests** (and the repo's test bar — items 31/34/38): pure handler tests per Kind (reverse +
  precondition + supersession, **both directions** — undo works when state matches, refuses when it
  moved); `ActivityLogService` tests on real SQLite (`tests/ShelfAware.Web.Tests` — record / undo /
  refuse / trim / **isolation**); `/history` bUnit page tests (render, undo button, greyed states,
  expander) in `tests/ShelfAware.Web.UI.Tests`. **Mutation-check every new test** — green is what the
  defect produces; a precondition test that never fails is the trap this repo keeps catching.
- **"One definition" watch:** `Summary` + `Reversibility` come from the handler (one place), never
  re-derived on the page; undo is one service path, not per-surface.

---

## 12. Redo — next step (NOT built here)

Redo = re-applying an undone action. It needs the *forward* op replayable from the entry. The schema
already leaves room: `UndoneAt` + `PayloadJson` mean a future redo re-runs the original action from
the payload and clears `UndoneAt`. Out of v1 scope; documented so the schema doesn't have to change to
add it later.

---

## 13. Suggested build order (within the one branch)

**Progress (branch `feature/undo-history`, unpushed):** steps 1–3 ✅ done; **step 4 ✅ done — 4a rename, 4b
Ate-it, 4c confirm-receipt.** Backbone + atomic recording + Peek (no-op undos grey out) + all IPantryStore-layer
actions + the `/history` page (day-grouped, per-row undo, greyed states, "Show all") + inline Undo on the
dashboard (Bought/Restocked, reusing the Ate-it notice). The branch now includes the merged
`fix/mobile-photo-upload` (merge `72ee2b7`).

The **static-on-db reversal pattern** the service-layer actions follow: extract the inverse as a
`static …OnAsync(db, …)` that stages on the caller's context and never saves — `ProductRenameService.RenameOnAsync`
(4a), `MealStock.ReverseMealOnAsync` (4b), `ReceiptRemovalService.RemoveOnAsync` (4c). Both a forward path's
inline "↩ Undo" and the /history undo run it, so "reverse this action" has one definition, the handler has no
service dependency, Peek stays safe, and the reversal + `UndoneAt` stamp commit in one transaction. 4b also
added `IActivityLog.Restate` (a staged-in-stages action — the "Ate it" picker resolves takes AFTER the first
commit — keeps its durable payload equal to what happened) and routed the inline Ate-it Undo through
`ActivityLogService.UndoAsync` (so it stamps the entry undone; /history then shows it reversed, never a live
undo of a meal already gone).

**4c (confirm-receipt) — Jordan's call: undoable via total removal** (the same `ReceiptRemovalService` the
Upload page's ↩ Undo and the Receipts page use). The receipt image is a filesystem side-effect that can't be
staged on the context AND must never run during Peek (Peek re-runs the reversal to grey the /history row), so
it needed a small, general framework addition: **`IUndoAfterCommit`** — a Peek-safe post-commit hook
`ActivityLogService.UndoAsync` runs only on a real, committed undo (never `PeekAsync`). The handler deletes the
image through a narrow **`IReceiptImageCleanup`** seam (over `ReceiptStorage`) so it stays cheap to construct
like every other handler and a test can prove the RIGHT folder is deleted — and that a Peek deletes none —
without a filesystem. The `ImagePath` rides in the payload (captured at confirm), so the post-commit delete
needs no DB read once the receipt row is gone. Recorded atomically in `ReceiptConfirmationService` (the ONE
confirm path — manual AND auto), only when the confirm recorded purchases. ⚠️ The Upload page's own ↩ Undo is
left as-is (RemoveAsync, its detailed messaging) rather than unified through the log; after it runs, its
/history entry Peeks as **Gone** (greyed) rather than "undone" — accepted (no data harm, no double-action; the
receipt really is gone), and unifying would only downgrade the panel's messaging.

**Step 5 (history-only, greyed `NotReversible`) — merge ✅, census ✅; receipt-removal deliberately NOT logged
(Jordan's call).** `HistoryOnlyHandler<T>` is the base for a recorded-but-never-reversed action (declares
`NotReversible`, so the service refuses the undo before dispatch; its `Reverse` is sealed as unreachable).
`ProductsMerged` records in `ProductMergeService` (source name read before the row goes), `CensusConfirmed` in
`CensusConfirmationService` (only when a row actually landed a count) — both staged on the action's own
transaction. **`ReceiptRemoved` was dropped** — 4c made a confirmed receipt's removal already visible in
/history (its `ReceiptConfirmed` entry greys as **Gone** on removal), so a separate entry would only
double-log the same narrative; the enum value is removed and the reason is noted in `ActivityEntry.cs` so it
isn't re-added.

**Soft actions ✅ — exclude-food reversible, recipes reversible-with-a-guard.** `ExcludedFoodChanged` is a
REVERSIBLE soft action (add↔remove; the payload carries the direction and the value, so the undo matches by
value with no generated id — recorded on AddExcluded/RemoveExcluded's own save; a reversal that's become a
no-op greys as Gone).

**`RecipeSaved` (Recipes.razor Save) and `RecipeAdapted` (RecipeAdapter) are now REVERSIBLE** (Jordan's call,
the branch AFTER the undo-history merge): undo DELETES the recipe/variant, but only while it is still exactly
as created — `RecipeReversal.HasBeenBuiltOnAsync` (the ONE guard both handlers share) refuses
(`Superseded`) the moment it's been cooked (`TimesEaten`/a `MealEvent`), adapted into a child variant
(deleting the parent would orphan it via the nullable self-FK), tagged, or given a photo. A pristine recipe
has only ingredients + steps, which cascade cleanly; the manual 🗑 stays the explicit destructive removal.
Recording gained the id via the two-save transaction (like a create). Adapting's undo removes only the
variant it made — it does NOT restore any stale variants that adapt replaced (bucket-2 territory).

**Removes made symmetric (same branch, bucket 1).** The add of a grocery-list extra or a substitute was
logged + undoable, but the REMOVE wasn't logged at all — so you could undo the add but not an accidental
remove. `GroceryExtraRemoved` + `SubstituteRemoved` (undo = re-add, unless it's already back → `Gone`) close
that, matching how the won't-eat list already handled both directions. New store methods
`RemoveGroceryExtraAsync`/`RemoveSubstituteAsync` own the remove-and-record (the pages route through them,
per #1's one-definition lesson). Tags are NOT in scope — product tags have no remove UI (no asymmetry), and
the Cookbook's recipe-tag remove belongs to a separate feature that isn't logged at all. The lossy/cascading
actions (merge, census, and the deletes) stay as they are — explicitly out of bucket 1 (Jordan's call).

The /history page is kind-AGNOSTIC (it switches on the Peek outcome, never on `ActivityKind`), so every new
kind renders — reversible ones with an Undo button, history-only greyed — with no page change.

Each precondition-checked, mutation-verified. Suite **1739 green**, Release 0 warnings. **The feature is
code-complete.** (#17 Report-resolve is DROPPED from the household log — admin cross-household, already has its
own /admin reopen undo.)

**`/pre-push` gate run (agent code + security reviews — the local `/code-review` is model-invocation-disabled,
so each ran as an independent `general-purpose` agent, then every finding re-verified in-code).** Security:
**tenancy boundary HOLDS** (probe-verified household B can't undo A's entry; `ActivityEntry` walks the full
drill; no new `IgnoreQueryFilters`/endpoint/settings-key/disk-write) — one LOW pre-existing note (the receipt-
image delete confines to the receipts ROOT, not the household subtree; not exploitable here — the `ImagePath`
is server-written per household with no injection path). Code review: **6 findings, no serious correctness
defect; Peek-safety, atomicity, the restate flow, the static-reversal split and preconditions all confirmed
clean.** Fixed 4:
  - ⚠️ **#2 `CountSetHandler`** silently reverted a count PAST a later re-attest to the same value (value
    comparison can't see a same-value re-count — only the clock moved). Added the `QuantityCountedAt >
    OccurredAt` guard its sibling `PurchaseAddedHandler` already has (attest-before-record means
    `QuantityCountedAt ≤ OccurredAt` for the own action, so no false-refuse). Mutation-checked.
  - ⚠️ **#3 `Recipes.razor` PickCandidate** stranded a decrement when the meal was undone on another surface
    mid-pick (the entry survives an undo — only stamped — so the restate found it; the gone `MealEvent` is the
    reliable signal). Guarded before `TakeOne`. Mutation-checked.
  - **#5** `/history` `AlreadyUndone` rendered a blank date on a concurrent-undo-during-load race — omit it.
  - **#6** no test asserted every `ActivityKind` has a handler (a new kind would fail only at record-time) —
    added, mutation-checked (it names the missing kind).
  - ⚠️ **#1 (MEDIUM) FIXED (Jordan's call).** Several PAGE buttons wrote directly to the DB instead of
    through the recording store methods, so those actions were logged via chat/voice but NOT from their own
    page. Routed every one through the store's ONE definition: `CreateAsync` (Products; `CreateProductAsync`
    gained a `defaultUnit` param so the page keeps its unit — the chat caller now names `ct` so it doesn't
    bind to it), `MarkOut` → `RecordSignalAsync`, `SetTracked` → `SetTrackingAsync`, `AddSubstitute`/
    `SuggestSubstitutes` → `AddSubstitutesAsync`, `AddExtra` + Recipes' `AddMissingToList` →
    `AddGroceryExtrasAsync` (which also DELETED `AddMissingToList`'s re-implemented "skip existing" — the
    store owns it, one definition). Products now injects `IPantryStore`. Each pinned by an
    entry-recorded page test (mutation-checked: a direct-write mutation fails the create assertion); the
    72 existing page tests stay green, so no page behaviour changed (the DefaultUnit test even proves the
    unit still rides through the store). Deliberately NOT logged (no such kind, consistent with chat):
    removing an extra/substitute, deleting a product.
  - **#4 (LOW/efficiency, left documented):** `/history` peeks each row in its own context (N+1; a
    ReceiptConfirmed peek stages a full removal-simulation). Bounded to that page, and the clean fix would
    force every handler to split "cheap precondition" from "stage reversal" — breaking the guarantee that
    Peek runs the EXACT same reversal as undo (so display and undo can't disagree). The cure is worse.

Suite **1742 green** (strengthened existing tests, no new methods), Release 0 warnings.

**Re-gate of the two fix commits (item 39 — a fix pass needs its own review): CLEAN, no regressions.** An
independent agent verified both commits statically AND by running 138 affected tests + 2 probes in an
ISOLATED worktree: the #2 timestamp guard is sound with no false-positive (the attest-before-record ordering
gives `QuantityCountedAt ≤ OccurredAt` for the own action; relative moves keep the old clock and aren't
refused — proved), #3 is state-safe, and all seven #1 routings preserved behaviour with no double-record and
tenancy intact. Its one non-blocking note — the **relative "used one" undo had no committed test** (a
pre-existing gap, exactly the path the #2 guard could have regressed) — is now closed by
`Undoing_a_relative_used_one_move_succeeds…`, mutation-checked (an inverted comparison false-refuses it).

Suite **1743 green**, Release 0 warnings. **The branch is fully gated and clean — pushing is Jordan's call,
not done.**

1. `ActivityEntry` schema + tenancy drill + `ActivityLogService` + `IUndoHandler` registry + retention.
2. The `IPantryStore`-layer actions (#1–#13): consolidate `BoughtToday`; record in `AddPurchaseAsync`,
   `RecordSignalAsync`, the `Set*` methods, `CreateProductAsync`, `AddTagsAsync`, `AddSubstitutesAsync`,
   `AddGroceryExtrasAsync`; write their handlers.
3. The `/history` page + the inline Undo affordances.
4. The service-layer reversible actions (#14 Ate-it via `MealStock.Restore`, #15 Confirm-receipt via
   `ReceiptRemovalService`, #16 Rename, #17 Report-resolve).
5. The non-reversible actions (#18–#20) logged history-only, greyed.
6. Full non-incremental Release build (0 warnings), full suite green, mutation-check pass, then
   `/pre-push` (both reviews) before any push.

## 14. Open questions to settle with Jordan first

- Census-confirm / receipt-removal: build inverses now, or history-only for v1? (§10 — recommend
  history-only.)
- Which "soft" actions to log (recipes, reports, settings)? (§7.)
- Retention cap value, and whether it's a config key.
- Page paging: "show all" expander vs infinite scroll.
