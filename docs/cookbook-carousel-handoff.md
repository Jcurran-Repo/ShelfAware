# Cookbook carousel → subtle-peek drag/swipe (handoff)

**Goal:** replace the cookbook's Prev/Next button carousel with a **drag/swipe** carousel whose
**neighbouring recipes peek subtly in the background**, while keeping the accessibility the current
design has. No new front end — this is the existing Blazor Server page (`Cookbook.razor` + `app.css` +
one small `/js/` module). Jordan's words: "subtle peek is a good idea" (not full cover-flow).

## Status / where this sits

- The whole cookbook feature is on branch **`feature/recipe-book`** (6 commits, reviewed via the
  `/pre-push` gate, **NOT yet pushed** — Jordan's call). This carousel change is a refinement of that
  same unmerged feature. **Confirm with Jordan first:** continue on `feature/recipe-book`, or a new
  branch off it. Recommended: continue on `feature/recipe-book` (the cookbook isn't merged, so polishing
  its carousel before merge is clean and keeps one arc).
- The page is `src/ShelfAware.Web/Components/Pages/Cookbook.razor`. Read it first. Today it renders ONE
  recipe (`Current = filtered[index]`), with a `.cookbook-nav` (Prev button · `.cookbook-position`
  status · Next button), the current recipe as `<article class="recipe-card cookbook-page">`, then the
  read-aloud panel and two hidden `.cookbook-print` blocks. `index` is driven by `Prev`/`Next`/`OnKey`
  (arrow keys) and reset to 0 in `OnParametersSet`. Filters (`?uses=`, `?tag=`) and everything else stay.

## Recommended design — a preview shelf + a stable detail panel (READ THIS, it avoids a trap)

Do **NOT** put N full recipe cards in the drag track. The current card carries interactive controls with
**fixed ids** — the photo `<InputFile id="cookbook-photo">` and the `<datalist id="recipe-tag-vocab">` —
and rendering many full cards would duplicate those ids (invalid HTML) and give you three tag boxes and
three "Read it to me" buttons fighting for focus. Instead, split the two jobs:

1. **The shelf** = a horizontal, scroll-snap track of **preview cards**, one per recipe in `filtered`.
   A preview shows only read-only content: the photo (or a tasteful placeholder), the name, the
   Ready-to-make / Missing chip, the adapted chip, and the tag chips. The **centered** preview is at full
   scale/opacity; its neighbours are **subtly** scaled down (~0.92) and faded (~0.55 opacity) so they
   "peek" from the sides. You change the centre by dragging/swiping, clicking a neighbour, or the arrow
   keys; it snaps to centre.

2. **The detail panel** = a single, stable panel BELOW the shelf that renders the **current** (centered)
   recipe's full content and ALL the interactive controls — ingredients with ✓/🛒 + amounts, the steps,
   read-aloud, Print recipe, Print products, add/replace/remove photo, and the tag add/remove/✨-suggest
   editor. This is essentially the body of today's `.cookbook-page` article, moved below the shelf and
   still targeting `Current`. One photo input, one datalist, one reader, one pair of print blocks — no
   duplicate ids, no layout shift as you drag, and **the interactive logic is unchanged** (it already
   targets `Current`).

"Other recipes visible in the background" = the peeking preview cards. "Subtle peek" = the small
scale/opacity on neighbours, not big cover-flow rotation.

_(Fallback only if the preview shelf looks wrong: keep full cards in the track, cap them to a uniform
max-height with internal scroll, mark every non-centered card `inert` + `aria-hidden`, and give the
fixed-id elements a per-recipe suffix. This is messier; prefer the preview-shelf design above.)_

## Accessibility — hard requirements (this repo's bar is high; a drag-only carousel is not acceptable)

- **Keyboard still pages it.** Keep an `OnKey` handler: ArrowLeft/ArrowRight move the centre by one
  (Home/End to the ends), and it programmatically scrolls the new centre into view. The shelf container
  is focusable (`tabindex="0"`).
- **Announce the current recipe.** Keep a visually-present-or-`sr-only` live region ("`<name>` — N of M")
  with `role="status"` / `aria-live="polite"` that updates as the centre changes.
- **The shelf is an accessible picker.** Model the previews as a list of options (e.g. each a `<button>`
  with `aria-current="true"` on the centered one, inside a labelled `role="group"`/list). Clicking a
  preview centers it. Because the single recipe CONTENT lives in the detail panel, you do NOT need to
  `aria-hidden` the neighbours — they are legitimately pickable options; the panel is the "one recipe".
- **`prefers-reduced-motion`:** no smooth-scroll animation / no scale transition when the user prefers
  reduced motion (snap instantly). The existing `@media (prefers-reduced-motion: no-preference)` guard on
  `.cookbook-page` is the pattern.
- The visible Prev/Next buttons go (Jordan's ask) — keyboard + click-a-preview + drag replace them.

## Technical approach

- **Swipe/drag comes from two layers:**
  - **Touch + trackpad = CSS `scroll-snap`, free, no JS.** The shelf is `display:flex; overflow-x:auto;
    scroll-snap-type: x mandatory; scroll-behavior:smooth` and each preview is `scroll-snap-align:center`.
    Horizontal padding (or a spacer) equal to ~half the viewport minus half a card lets the first/last
    card reach centre and makes neighbours peek. This alone gives native phone swipe + two-finger laptop
    swipe + momentum + snapping.
  - **Mouse click-and-drag = a thin JS module** `wwwroot/js/cookbook-carousel.js` (pointerdown → track
    pointermove → `el.scrollLeft -= dx` → pointerup; suppress text selection + click during a real drag).
    CSP is `script-src 'self'` (see CLAUDE.md), so a same-origin `/js/*.js` ES module imported via
    `JS.InvokeAsync<IJSObjectReference>("import", "/js/cookbook-carousel.js")` is fine — no inline, no CDN.
- **The JS module also owns:**
  - An `IntersectionObserver` (root = the shelf) that reports the **most-centered card's index** back to
    .NET via a `DotNetObjectReference` callback, on scroll/drag settle (debounced).
  - `scrollToIndex(i, smooth)` — center the i-th card (`card.scrollIntoView({inline:'center', ...})`), for
    keyboard nav, click-a-preview, and filter changes.
  - Cleanup (remove listeners, `observer.disconnect()`) on dispose.
- **Blazor side:** `index` stays the source of truth (drives `Current` → the detail panel, the print
  blocks, the reader). Bidirectional sync, with **loop guards** (the tricky part): JS reports a centered
  index → Blazor updates `index` only if it changed → re-render; Blazor changes `index` (keyboard / click
  / filter) → calls `scrollToIndex`. Debounce the JS callback and short-circuit no-op updates so a
  Blazor-initiated scroll doesn't ping-pong. Import the module + create the `DotNetObjectReference` in
  `OnAfterRenderAsync(firstRender)`; dispose both (make the page `IAsyncDisposable` or extend the existing
  `IDisposable`), guarding `JSDisconnectedException`.
- Render ALL `filtered` recipes as previews (a household has dozens; fine). Windowing (render centre ± a
  few) is a later optimization only if a cookbook gets huge — note it, don't build it first.

## Files

- `src/ShelfAware.Web/Components/Pages/Cookbook.razor` — replace the `.cookbook-carousel`/`.cookbook-nav`
  block with the shelf + detail panel; add the JS interop + index sync + dispose. Keep `filtered`,
  `ApplyFilters`, the filters, `ResetTransient`, the reader, the print blocks, and every handler.
- `src/ShelfAware.Web/wwwroot/js/cookbook-carousel.js` — NEW (drag + IntersectionObserver + scrollToIndex).
- `src/ShelfAware.Web/wwwroot/app.css` — new `.cookbook-shelf` / preview / peek / detail styles; remove
  the now-unused `.cookbook-carousel` / `.cookbook-nav` / `.cookbook-position` rules. Reuse existing
  `.recipe-card`, `.chip*`, `.tag-*`, `.ingredient-list`, `.recipe-steps`, etc. Use the `:root` design
  tokens (`--surface`, `--ink`, `--accent`, `--radius`, `--shadow`, …) — no hardcoded colours; both
  themes come free.

## Gotchas (from CLAUDE.md — don't relearn these)

- **CSS/JS are fingerprinted static assets → a change needs a server RESTART to show** (a browser reload
  serves the old file). Item 37.
- **Dev server:** port 5179 is Jordan's tailnet publish — do NOT kill it. Use the `shelfaware-web-alt`
  launch config (port 5180). Sign in with `/dev/login` (Development-only quick login into the sample
  household).
- **bUnit runs no real JS**, so the drag/scroll/observer behaviour is **live-verified only** (like the
  app's other JS-driven features). The page tests must assert the **Blazor-side** logic with JS interop
  in loose mode: that `filtered` renders N previews, that the detail panel shows `Current`, that `OnKey`
  and click-a-preview change `index`/`Current`, that filters still scope + reset. Don't try to test the
  drag itself in bUnit.
- **Don't round-trip source files through PowerShell** (BOM/encoding damage) — use the editor tools. The
  repo is full of `—`, `×`, `≥`, `→`.

## Tests to update

- `tests/ShelfAware.Web.UI.Tests/CookbookPageTests.cs` currently asserts `.cookbook-carousel`,
  `.cookbook-position`, `button[aria-label='Previous recipe']` / `'Next recipe'`, and `.cookbook-page h2`.
  Rework these for the shelf: the current recipe's name/detail now lives in the detail panel; "paging"
  is `OnKey` (arrow keys) or clicking a preview; assert `index`/`Current` moves and the detail panel +
  live region update. Every existing behavioural test (filters, tags, photos, print, read-button gating)
  should still pass with at most selector updates — the underlying logic is unchanged.
- Keep the repo bar: **mutation-check** any new/changed test (green is what the defect produces), 0
  warnings on `dotnet build ShelfAware.slnx -c Release --no-incremental`, full suite green.

## Acceptance criteria

- Touch/trackpad **swipe** and mouse **click-drag** both move the shelf and snap a recipe to centre;
  neighbours **peek subtly** (small scale + fade), current at full emphasis.
- The **detail panel** below shows the centered recipe with all controls working (read-aloud, print
  recipe, print products, photo add/replace/remove, tag add/remove/suggest) — unchanged behaviour.
- **Arrow keys page it**, "recipe N of M" is announced, the centered preview has `aria-current`, clicking
  a neighbour centers it; **`prefers-reduced-motion`** disables the animation.
- Tag cloud + product filters still scope the deck and reset to the first card on change.
- **Live-verified in a real browser** (port 5180, `/dev/login`): desktop mouse-drag, and touch via the
  browser's mobile emulation; no console/CSP errors (the strict CSP must hold — same-origin module only).
- Page tests updated + green; 0 warnings; then **run `/pre-push`** (code + security review) before any
  merge — the security review has little to chew on here (no new endpoint/table/settings key/DB write),
  but the gate is the rule. Do NOT push; that's Jordan's call.

## Nice-to-haves (only if quick; skip otherwise)

- A soft gradient mask on the shelf's left/right edges so cards fade out rather than hard-clip.
- Snap the reader/print to the recipe you're actually centered on if you drag while a reader is open
  (or just close the reader on centre-change, mirroring today's `ResetTransient` which already nulls
  `readingRecipe` on paging).
