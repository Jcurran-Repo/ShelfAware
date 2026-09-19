# Backlog — what's open

Carried out of `CLAUDE.md` on 2026-09-19. Everything here is deliberate: either parked with a reason,
or small-and-not-yet-worth-a-branch. Shipped items are struck from the list rather than accumulating
as "(shipped since this note)" parentheticals, which is how the old version got to be wrong about
`docs/demo.gif` for two months.

## Open

- **The credit gate checks the balance but does not reserve it.** `EnsureManagedCallAllowedAsync` reads
  the balance before the provider call; `RecordCreditConsumptionAsync` writes the draw after it. So several
  acts started at once — two tabs, the roaming voice agent, a fast clicker — can all pass the same check
  before any of them draws, and `RecordConsumptionAsync` writes its negative row with no floor. The result
  is an **overdraft, not free credit**: the balance goes negative, the next gate refuses, and the household
  has to fill the hole before spending again, so it self-corrects and errs toward the operator for exactly
  one burst. Closing it properly means a reservation row at the gate and a release path on every exit of
  every act, which is real work for a bounded, self-correcting exposure — parked deliberately, and named
  here because it was previously nowhere. Found 2026-09-19 while writing up why the charge lands first
  (`docs/subscription-plan.md` §4.w). **Revisit if a balance is ever seen materially negative.**

- **Tag dedup does not see through Unicode confusables.** `TagVocabulary.Normalize` now folds to one
  Unicode normal form, so a precomposed "Café" and a decomposed one are the same tag. A Cyrillic "Ѕoda"
  against "Soda" is still a new tag. That needs a confusable *skeleton* mapping rather than a normal
  form, and the blast radius is small and household-local: the advisor can only ever return an element
  of that household's own list. On the receipt path the result is a suggestion the user accepts or
  overrides; on the recipe path `RecipeTagService.SuggestAndApplyAsync` applies and saves it with no
  confirmation step, so there it IS a silent write — the first version of this note claimed otherwise.
  Raised by the pre-merge security gate, 2026-09-19.
- **A handful of invisible code points still read as an "answer" and are charged.** `ProviderReply`'s
  allow-list excludes marks, punctuation, separators, controls and the replacement character, but a
  Hangul filler (category `Lo`) and BRAILLE PATTERN BLANK (`So`) are categorically letters and symbols
  while rendering as nothing. Left alone deliberately: a model emitting only one of those is not a shape
  anyone has seen, and chasing every invisible code point by hand is how the deny-list this replaced got
  it wrong. Revisit if a real reply ever lands in that gap. Raised by the pre-merge code gate, 2026-09-19.
- **Advisor prompts interpolate the household's whole vocabulary with no cap.** `AnthropicTagAdvisor`
  joins every existing tag into the prompt, the tag input on `/receipt` has no `maxlength`, and
  `AnthropicRecipeTagAdvisor` and `AnthropicPantryChat` do the same with known tags and the full product
  list. That is an unbounded per-call input-token cost the household controls, and it is what makes a
  provider timeout reachable on purpose rather than by luck. Pre-existing; raised 2026-09-19.

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
- ⚠️ **Speech is neither metered nor gated** (found by the phase-7 security gate, 2026-09-19).
  `ElevenLabsTextToSpeech` and `ElevenLabsSpeechToText` are typed `HttpClient`s, not `IChatClient`s, so
  they never reach `MeteredChatClient`: nothing records their cost, nothing charges for them, and
  `RecipeReadAloud.razor` has no `AiErrorText.BlockedReasonAsync` gate (unlike `PushToTalk.razor`). A
  household at **zero balance** can still burn the host's ElevenLabs quota. The published prices for
  `TtsSynthesis` and `RealtimeMinute` have been WITHDRAWN from the Settings price list
  (`CreditPricing.MeteredActions`) so no false statement stands, but the spend gap is still open.
  Wiring it needs two things the app can't see from inside: a real ElevenLabs invoice to price a read
  against (the 3-credit figure is an estimate, never a measurement), and a charge point that isn't an
  `IChatClient`. Jordan's call whether to wire it or leave speech free.
- **The remediation arc** — all seven phases landed 2026-09-19, designed in `docs/remediation-plan.md`,
  with the review-gate pass on phase 7 written up in its §9. What's left out of that arc is deliberate:
  draining logic out of `.razor` (D1) and EF Migrations (D3), both with reasons in §8.

- **A pre-cap tag stored in decomposed form drops out of dedup.** `FindNearDuplicate` skips a vocabulary
  entry whose RAW length is over `TagVocabulary.MaxLength`, measured raw on purpose — the cost it bounds
  is the cost of normalizing, so a cap that normalized first to decide would have already paid it. But
  `Normalize` shrinks (NFC composes, whitespace collapses), so a legacy tag written before the cap
  existed — 33 decomposed "é" is 66 characters raw and 33 composed — is inside the cap once normalized
  and is skipped anyway. That household's dedup stops seeing it and the tag cloud can fragment on a
  difference nobody can see, which is the exact thing `Normalize` was added to prevent. `Canonicalize`
  caps what it writes, so only pre-cap rows can be in this state. Pinned by
  `An_entry_whose_raw_form_is_over_the_cap_is_not_a_dedup_target`. The fix is a one-off normalize-and-
  rewrite pass over the tag column, which wants EF Migrations (D3) first.
- **The advisor prompt caps each tag's length but not the tag COUNT.** `AnthropicTagAdvisor` interpolates
  the whole vocabulary on every tag add, so a household with N tags sends N × 64 bytes per charged call,
  unbounded in N. Self-inflicted and behind the credit gate, so not the denial of service the candidate
  cap closed — but the "untrusted input to a charged call" axis is not fully shut until it is bounded.
  A plain `.Take(…)` silently degrades dedup quality for exactly the households with the most tags, which
  is the wrong trade; the honest fix is to send the nearest-N by the cheap matcher, which is a change to
  what the advisor is asked, not just how much.
- **The recipe request box has no cap, client or server.** `Recipes.razor`'s input goes straight into a
  charged prompt in `AnthropicRecipeAdvisor`. One gate weaker than the tag path was, since it sits behind
  `AiErrorText.BlockedReasonAsync`, but it is the same shape and should get the same treatment: a
  server-side refusal with a visible reason, not just a `maxlength` attribute.
- **Four pages cannot be read by the razor lift the build rules use.** `MainLayout`, `Accuracy`,
  `GroceryList` and `MealPlanPage` each define a `RenderFragment` with a razor TEMPLATE expression
  (`=> @<div>…`), which the razor compiler turns into C# but a plain brace-match lift cannot. They are
  named in `SourceTree.Unliftable` and asserted to be exactly that set, so a fifth page fails the build
  rather than dropping out of the scan — but they are genuinely unjudged today. None calls a provider.
  If one ever needs judging, move the fragment into a component rather than widening the lift.

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

- **A meal plan charging for meals it does not deliver** — closed 2026-09-19, in two passes. The first
  fixed the gate: it asks whether the balance covers *this act's* price, so a household is refused a plan
  it cannot afford before any of it runs. The second (Jordan's call: "any call that fails should probably
  be refunded") made every act settle up. An act now declares how many units it asked for and reports how
  many it delivered; disposing it reverses the difference as a `Reversal` ledger entry priced by the same
  `CreditPricing` call that charged it, so a 124-meal plan that returns 7 meals keeps 3 credits of its 42
  and gives 39 back. The refund is keyed to a charge that really happened — `ChargeRecorded` is only
  handed a settlement when the meter actually wrote one — so a Founder or a billing-off box, which never
  charged, cannot mint credit by failing. `UnspentAllowanceCreditsAsync` counts reversals alongside
  consumption, so a refunded allowance credit still expires with its month rather than outliving it.
  A build rule (`Every_act_reports_what_it_delivered`) fails the build if a new charging site forgets to
  say what it delivered, which would silently refund the whole act.

- **Phase-5 cloud deploy** — LIVE on a DigitalOcean droplet since 2026-08-11 (not Azure), via
  `docs/deploy-droplet.md` + `deploy/`. The demo link points at https://demo.shelfaware.net.
- **Learning corrected names and brands from receipt review** — the corrected product NAME via the
  alias's product (PR #19, item 53), the corrected BRAND per (merchant, raw text) via PR #46 (item 61).
- **The ~768–1400px header overflow** — retired by the left sidebar nav rail (PR #21, 2026-08-22).
- **Downloading a receipt's saved image copy** — household-scoped `/api/receipt-image/{id}` plus a
  Download link on `/receipts` (PR #22, 2026-08-22).
