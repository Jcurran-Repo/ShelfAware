# demo.gif — storyboard & capture plan

The hero capture at the top of the README. One GIF, under 30 seconds, that shows the thesis loop:
**a receipt goes in → the app knows what's running low → you tell it something in plain English →
the grocery list is ready.** Delete this file once `docs/demo.gif` is recorded and committed.

---

## Hard rules (before anything is recorded)

- [ ] **No real data on screen.** Record from a **throwaway account** (`demo@…` — auth.db is
      dev-local and gitignored) whose household was filled by the **"Load sample data"** button on
      the dashboard's welcome banner. Never record the real household — receipts, prices, and
      habits in the GIF live on the public internet forever.
- [ ] **No keys on screen.** Don't open Settings during the recording. If a scene accidentally
      shows it, the key fields are masked — but just don't.
- [ ] **The upload scene uses a synthetic receipt** — one of the committed PNGs in
      `test-fixtures/`, not a Walmart PDF from `Documents\Walmart Receipts`.
- [ ] Browser chrome hidden as much as possible: a clean profile, no bookmarks bar, no extensions,
      100% zoom. Record the **viewport only** — no tabs, no URL bar (localhost:5179 in the address
      bar looks like a dev demo, because it is one).

## Format

| Setting | Value | Why |
|---|---|---|
| Capture size | ~1280 × 800 viewport | The layout's comfortable desktop width |
| Export width | 960 px (height follows) | Crisp in the README column (~830 px), tolerable file size |
| Frame rate | 10–12 fps | UI motion doesn't need more; file size does |
| Length | 24–30 s | Long enough for the loop, short enough to rewatch |
| File size | ≤ 10 MB target, 15 MB ceiling | README loads on hotel wifi too |
| Theme | **Light** | Reads crisper scaled down; pick one and stick with it |
| Loop | Infinite, seamless | First and last frame are both the dashboard |

**Tool:** [ScreenToGif](https://www.screentogif.com/) (free, Windows) — records a region, has a
frame editor (delete dead frames, stretch hold frames via per-frame delay, which costs ~nothing in
a GIF), and a good palette-optimizing encoder. Record at natural speed; fix pacing in the editor.

## Scenes

Total ≈ 28 s. Times are the *exported* timeline — cut waiting-for-the-model dead frames in the
editor, don't try to rush the mouse live.

### 1 — The promise (0:00 – 0:04) · Dashboard `/`
Open on the dashboard: a handful of **Running low / Overdue cards** with due dates and the
plain-English basis line. No interaction — let it breathe for ~3 s (a long-delay hold frame).
This is the app's one question, answered, before anything moves.

### 2 — A receipt goes in (0:04 – 0:13) · Upload `/receipt`
Navigate to Upload. Drop a `test-fixtures/` receipt PNG onto the picker → extraction progress →
the **review table** appears: lines matched to existing products, tags filled in, prices read.
Hover one matched row for a beat (the graduated-trust story in one frame), then **Confirm all**.
Trim the model wait to ~1.5 s of progress indicator in the editor.

### 3 — Just tell it (0:13 – 0:20) · Dashboard `/`
Back on the dashboard, click into the chat box and type (real keystrokes, natural speed):
> **we're out of dog food, almost out of coffee**

Send. The reply appears and the affected cards **jump to the top, pinned/overdue**. Use items the
sample pantry actually has — check before recording and adjust the sentence to match.

### 4 — Ready to shop (0:20 – 0:26) · Grocery List `/list`
Navigate to the list: **aisle-sorted sections**, the usual size + brand under each item, estimated
cost at the bottom. One slow scroll if it doesn't fit; otherwise hold.

### 5 — Loop close (0:26 – 0:28) · Dashboard `/`
Back to the dashboard, hold ~1.5 s. Last frame ≈ first frame, so the loop reads as a cycle — which
is what the app is.

**Cut from the hero GIF (deliberately):** recipes/adapt (a strong *second* GIF later), voice and
cook-along (pointless without audio — a future MP4, not a GIF), Settings/BYOK, Trends, Accuracy.
One GIF, one story.

## Wiring it into the README

1. Save as `docs/demo.gif`, commit it (it's synthetic data — committable).
2. In `README.md`, delete the TODO comment and un-comment the image line (alt text is already
   written there).
3. Delete this file in the same commit.

## Appendix — the other placeholder: `docs/accuracy.png`

A **static PNG** of the `/accuracy` page, captured from the **real household** (it's just metric
tables — no receipts or personal data on that page, and the numbers must match the ones quoted in
the README: 99/99/100 extraction, the honest backtest numbers). ~1200 px wide, light theme, both
halves of the page (extraction eval + prediction backtest) in frame. Replaces the
`<!-- TODO: screenshot of the /accuracy page -->` comment in the eval section.
