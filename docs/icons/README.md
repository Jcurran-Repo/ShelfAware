# App icon — the "dapper blob"

`shelfaware-icon.svg` is the vector source for the app / home-screen icon: a gold-suited
gentleman blob in a top hat and Shelf Aware–blue bow tie, tucking a light-brown stock
clipboard under one side, on the app's dark-mode background.

Palette (all from the app design system in `wwwroot/app.css`):

| Role | Colour |
|---|---|
| Background | `#131619` (dark-mode `--bg`) |
| Suit + hat | `#e3b341` (Shelf Aware gold, the `--duesoon-line` accent) |
| Bow tie, hat ribbon, buttons | `#2563eb` (Shelf Aware blue, `--accent`) |
| Blob / shirt | `#ffffff` |
| Clipboard | `#d2b48c` light brown |

## Served PNGs (generated from the SVG)

- `wwwroot/icons/icon-512.png` — 512×512, manifest `any` + `maskable`
- `wwwroot/icons/icon-192.png` — 192×192, manifest `any`, favicon
- `wwwroot/icons/apple-touch-icon.png` — 180×180, iOS home screen

Regenerate by rasterizing `shelfaware-icon.svg` at those sizes with any SVG→PNG rasterizer
(the art sits inside the maskable safe zone and is full-bleed, so no per-size cropping is needed).
The manifest is served from `Program.cs` (`GET /manifest.webmanifest`); the head wiring lives in
`Components/App.razor`.

## The suit buttons — one placement, three copies

The two blue suit buttons sit on the body's centre line, straight down the shirt under the bow tie:
`cx=256, cy=352` and `cx=256, cy=372` (r=4.5). Until 2026-10-07 they were off-centre and staggered
(`cx=272,cy=356` / `cx=278,cy=376`, to the right of the `x=256` centre line); realigned on every copy
that day. The coordinates are deliberately duplicated — the mascot component carries the icon's
artwork inline so only its face varies — so a change to them is a change to **all** of:

- `docs/icons/shelfaware-icon.svg` and `shelfaware-icon-gold-plain.svg` — the vector sources
- `src/ShelfAware.Web/Components/EggsMascot.razor` — the inline mood-mascot (the same two `<circle>`s)
- `wwwroot/icons/icon-512.png` · `icon-192.png` · `apple-touch-icon.png` — re-rasterize after any SVG edit

The served PNGs were last regenerated 2026-10-07 from `shelfaware-icon.svg` with CairoSVG
(`cairosvg.svg2png(output_width=N, output_height=N)` at 512/192/180, saved as RGBA).

## Earlier explorations (kept for reference)

- `shelfaware-icon-gold-plain.svg` — the same blob without the clipboard
- `shelfaware-icon-smile-blue.svg` — first look: black formalwear + red accents on the brand-blue background
