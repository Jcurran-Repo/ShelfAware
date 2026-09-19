# Backlog — what's open

Carried out of `CLAUDE.md` on 2026-09-19. Everything here is deliberate: either parked with a reason,
or small-and-not-yet-worth-a-branch. Shipped items are struck from the list rather than accumulating
as "(shipped since this note)" parentheticals, which is how the old version got to be wrong about
`docs/demo.gif` for two months.

## Open

- **`docs/accuracy.png`** — the README's last remaining TODO (line ~190). ⚠️ **Check before acting:**
  the file exists at `docs/accuracy.png` and the README renders it; the old note claiming it was
  outstanding was itself stale. Verify what's actually missing before building anything.
- **Re-record `docs/demo.gif`** — optional polish, not a gap. It has existed since 2026-07-12
  (`5f34b24`), but it pre-dates every feature from v3.5 on: variety, expiration, Reports, the whole
  counting arc, the census, the tour. A re-record needs a NEW capture plan first — the original
  storyboard was deleted when the gif landed, per that file's own lifecycle note.
- **A per-size Trends price chart** — the sibling of the price-trend fix in PR #37 (item 60).
- ⚠️ **Eggs' suit buttons are misaligned — realign on EVERY icon** (Jordan's call, 2026-09-05). The two
  blue suit buttons are off-centre and staggered (`cx=272,cy=356` / `cx=278,cy=376`, right of the
  `x=256` centre line), and the coordinates are duplicated across the SVG sources, `EggsMascot.razor`,
  and the rasterized PNGs. Fix the SVG(s) and the component together, then regenerate the PNGs. Full
  detail and the file list are in `docs/icons/README.md`.
- **The remediation arc** — seven phases out of the 2026-09-18 audit, designed in
  `docs/remediation-plan.md`. Phases 1 and 2 are done; 3–6 are independent and unblocked. Phase 7 (the
  Shelf Aware credit) has its anchor decided — 1 credit = $0.01 of cost — and is the one phase that
  touches money, so it gets two independent gate passes.

## Parked, with reasons

- **CSV history importer** — Walmart won't export to Jordan's state, so there is no itemized source to
  import from. Needs a different provider before it's worth building.
- **Barcode / SKU lookup for receipts** — rejected, not deferred. Receipts print internal store SKUs,
  not scannable UPCs, and no public API maps them; it would mean hand-maintaining per-store tables.
- **The lapsed-Free "keep-warm" problem** and **the dormant-subscriber conscience nudge** — both in
  `docs/subscription-plan.md` §8, each with its candidate shapes recorded.

## Deployment gotchas that are not backlog but keep being rediscovered

⚠️ **Timezone and locale, same root.** Every "today" in the app (purchases, signals, predictions) is
server-local `DateTime.Today`/`DateTimeOffset.Now`, deliberately consistent, and every price formats on
the server's culture. A box with no timezone or locale set files evening purchases on tomorrow's date
and renders `¤3.99` (invariant culture — a systemd service starts with **no** `LANG`, found on the
first live deploy). Set the droplet's timezone (`timedatectl set-timezone`, or `TZ` in the service
env) and keep `LANG` in the env file. Runbook step 2 covers both.

## Recently closed

- **Phase-5 cloud deploy** — LIVE on a DigitalOcean droplet since 2026-08-11 (not Azure), via
  `docs/deploy-droplet.md` + `deploy/`. The demo link points at https://demo.shelfaware.net.
- **Learning corrected names and brands from receipt review** — the corrected product NAME via the
  alias's product (PR #19, item 53), the corrected BRAND per (merchant, raw text) via PR #46 (item 61).
- **The ~768–1400px header overflow** — retired by the left sidebar nav rail (PR #21, 2026-08-22).
- **Downloading a receipt's saved image copy** — household-scoped `/api/receipt-image/{id}` plus a
  Download link on `/receipts` (PR #22, 2026-08-22).
