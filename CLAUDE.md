# CLAUDE.md — process notes for AI-assisted sessions

Working notes for Claude Code sessions on this repo. The authoritative spec is
[DESIGN.md](DESIGN.md) — read §0 (rules) and §10 (phases) before doing anything.
This file records build state, decisions, and environment quirks the spec and
code don't capture. **As-built deviations from the spec live here, not in DESIGN.md.**

**Audience & quality bar:** a portfolio piece with real users (Jordan + his wife)
and professional viewers (current + prospective employers). Demonstrate
production-ready work — robustness, clean atomic git history, tests, accessibility,
and visual polish are in-scope and expected, not gold-plating. Don't dismiss polish
as overkill "because it's single-user."

## Design directives

- **Co-creation — stop and discuss before diverging.** Jordan and Claude are
  co-creators. Always stop and talk it through if you disagree about a direction,
  or see a better/riskier/materially harder path than what was asked. Don't silently
  build what you think is best, and don't silently implement something you believe is
  wrong — surface the trade-off, reason it out together, decide jointly, then code.

- **Never push or merge to `master` without a code review AND a security review.** Run
  **`/pre-push`** (`.claude/commands/pre-push.md`), which drives `/code-review` + `/security-review`
  over the whole branch diff and spells out what "security" means in this repo (the tenancy boundary,
  new settings keys, anything written to disk per household, new endpoints). This is a hard gate, not
  a suggestion, and it applies to a one-line fix as much as an arc. **Reviewing after the merge is
  worth much less than before it** — the voice-engine arc's pre-merge review found five real bugs
  including an open microphone, and the 7/15 no-household 500 shipped past a fully green test suite
  and was only caught by running the app. Green tests are not a review. Report the findings and then
  **stop: pushing is Jordan's call, always.**

- **One prediction, one story — never let a screen state something the engine didn't do.** Anything a
  surface says *about* a prediction must come from the same `PredictionResult` that produced the due
  date it sits beside. Don't re-derive "is it due" from a median, don't render a factor you computed
  before the engine clamped it, don't ask `Predict` with different flags than the page next door.
  **This is a rule because it has been broken four times in one branch** (v4.0's backlog work): a
  report re-deriving a due date from the rebuy median called an item overdue while its own product
  page called it Stocked; `StockUpFactor` reported a raw ratio beside a bounded projection, so Product
  Detail claimed a 500× stretch the engine never made; the backlog check ran expiration-blind while
  the dashboard didn't; and a row said "1 days over". Every one shipped past a fully green suite, and
  every one was found by a human noticing two screens disagreeing. **Green tests cannot catch this
  class** — only asking "where did this number come from, and did the due date beside it come from
  the same place?" can.

- **If a fact is shown or used in more than one place, it gets ONE accessible definition — and
  converting the sites one at a time is worse than not starting.** The general form of the rule above.
  Any value a surface displays, or a guard acts on, that more than one place needs: give it a single
  property, method, or shared helper that everyone asks. Never let two call sites answer the same
  question with their own arithmetic, their own string comparison, or their own copy of a predicate.
  **This is the single most expensive failure in this repo's history**, and it is always the same
  shape: two places agree today, one is edited later, and the disagreement ships silently because
  every test still passes. Cases on file — the ✓ mark and the "Ate it" decrement using two different
  matchers (item 25); a suppressed row's phrasing reinvented per page (item 20's `CountNote`); seven
  readings of when a signal happened, one of them different (item 19's `SignalDate.Of`); a page
  re-deriving "exact vs fuzzy" from raw strings the matcher had already normalized (item 39).
  ⚠️ **And the sharpest lesson, from the census branch's own review cascade (item 41): "which product
  does this name mean?" was answered in NINE places, and fixing them one per round produced three
  consecutive rounds of new data-harm defects** — each round left a half-converted state where one
  guard promised something its neighbour then contradicted (a grid offering "leave this to create a
  separate item" over a write that replaced an existing product's count). Rounds ran 5 → 3 → 8
  findings; the count only fell when the *rule* moved into one place (`ProductMatcher.IdentityKey`)
  instead of the sites moving one at a time. So: when you find two sites disagreeing, the fix is the
  shared definition and **every** caller in the same change — a partial conversion is a new bug with a
  green suite over it.

- **Craftsmanship — take pride in every change; no shortcuts.** Always do the polished,
  professional thing, not the quickest thing that happens to pass. Concretely: **no empty
  or catch-all `catch` blocks that swallow errors** — catch specific exceptions, log via
  `ILogger`, and let cancellation (`OperationCanceledException`) propagate; don't duplicate
  logic that should live in one shared place; don't ship behavior without tests; leave no
  dead code, orphaned state, or TODO-shaped gaps. If you spot a corner being cut — yours or
  the existing code's — fix it or flag it, never leave it. Assume every line will be read by
  a prospective employer, because it will.

## Build state (updated 2026-07-07)

| Phase (DESIGN.md §10) | Status |
|---|---|
| 1 — Skeleton + data | ✅ Done, acceptance verified |
| 2 — Extraction pipeline | ✅ Done, 3 acceptance criteria verified with live calls |
| 3 — Prediction engine + dashboard | ✅ Done, engine tests green + dashboard verified |
| 4 — Chat tools (IPantryChat) | ✅ Done, acceptance verified with a live tool-call |
| 5 — Cloud deploy + README | ◑ README ✅ done + pushed (`4757839`); **LIVE on the DigitalOcean droplet since 2026-08-11** (demo box, BYOK; deployed end to end via `deploy/deploy.ps1` per the runbook — publish → install.sh → systemd → Caddy cert → registration). First live deploy surfaced the systemd-no-locale gotcha (invariant-culture `¤` prices) — fixed in the kit (`LANG` in `deploy/env.example`). Remaining: `docs/accuracy.png` only (README line-190 TODO) — `docs/demo.gif` has existed since 2026-07-12 (`5f34b24`), though it pre-dates every feature from v3.5 on, so re-recording it is optional polish, not a gap |

Everything below is built, verified live, committed, and **pushed** (master, through the 2026-07-05
v2.3 full-site-audit + BYOK arc — see item 8 below and timeline.md).
Beyond the spec's 3 pages, the app now has Dashboard (`/`), Upload (`/receipt`),
Products (`/products`), Grocery List (`/list`, by aisle + copy/print + a manual **Extras**
section), Trends (`/trends`, price tickers + spend forecast — page component is
`SpendInsight.razor`), Product Detail (`/product/{id}`, rhythm + price-history chart),
Accuracy (`/accuracy`, renders `eval-results.json`), **Recipes (`/recipes`)**,
Receipts (`/receipts`, added 7/12 — per-receipt line-item totals via `ReceiptTotals`, Core), and
**Count from a photo (`/pantry-photo`, added 8/2 — §13.8's shelf census; see item 37)**.
Extensive polish stretch done: design-system + dark mode (CSS vars) + site-wide a11y
pass; LLM-assisted product matching in extraction; GitHub Actions CI (restore + build
+ unit tests; Evals excluded — needs a live key). **1576 green xUnit tests across four
projects** (pure engine · faked-IChatClient AI layer · persistence on in-memory SQLite ·
bUnit pages/components — see items 31, 42, 43, 45, 46, 47 and 49).

**Post-Phase-4 feature arc (all ✅ committed + pushed):**
1. **Size loop closed in the buying UI** (`cc21250`) — recommended size + usual brand now show
   on the Grocery List and dashboard cards (not just Product Detail / Products grid).
   `ProductEstimate` carries `RecommendedSize` + `UsualBrand` (shared `ShoppingEstimator.
   UsualBrandOf`); sizes are display-normalized via `SizeFormat.Normalize` (cosmetic only);
   est. cost prices the recommended (dominant) size, so **`Size` was added to `ReceiptLine`**
   (mirrors `Brand` on both `ReceiptLine` + `PurchaseEvent`; `ConfirmAll` writes it).
2. **Real accuracy numbers** (`af19103`, then `b250103`) — 3 real Walmart receipts hand-labelled
   (PDFs gitignored; only `*.expected.json` + `eval-results.json` committed). **99% recall /
   99% precision / 100% field accuracy** on `/accuracy`. First run read 58% — the flaw was the
   symmetric-Jaccard name matcher, switched to the **token containment coefficient (≥ 0.6)**;
   the honest 58%→100% metric-fix story is in the README.
3. **Two-layer categories** (`9670d39`, `b250103`, `628fecf`, `994ead7`, `8da2114`) — see the
   Tags section below.
4. **Recipes** (`ff1fd83` P1, `612fcbd` P2) — see the Recipes section below.
5. **README capstone** (`4757839`), **rewritten 2026-07-04** per Jordan's "more casual /
   usage-focused" feedback — now covers the v2 arc (voice, graduated auto-import, two-stream
   cadence) and the both-halves accuracy story (extraction eval + prediction backtest).
   **Placeholders Jordan must still fill:** live-demo URL (`<!-- LIVE_DEMO_URL -->`),
   `docs/demo.gif`, `docs/accuracy.png`. **[As of 2026-08-11 only `accuracy.png` remains:
   demo.gif landed 2026-07-12 (`5f34b24`); the URL swapped to demo.shelfaware.net when the
   droplet went live.]**
6. **Small UI adds:** always-available **"Out" button** on the Products grid (`9c78a14`) — the
   dashboard only lists running-low items, so the grid is the home for marking any product out;
   grocery-list item names link to `/product/{id}` (`b6afb35`).
7. **v2.2 review-hardening pass (2026-07-04, from the 7/3 code review — see timeline.md):**
   - **`ReceiptConfirmationService` (Web/Data) is THE confirm path** — Upload's ConfirmAll and the
     auto-importer both go through it. Idempotent (already-Confirmed = no-op), clamps qty ≤ 0 → 1
     and future dates → today, canonicalizes tags against the GLOBAL vocabulary, and takes a
     `writeAliases` flag: **only human-confirmed receipts write merchant aliases** (machine matches
     must not become sticky). Don't add a second confirm path.
   - **`ReceiptLine` gained `TagsJson` + `SuggestedProduct`** (additive EnsureColumn migrations in
     Program.cs) so queued receipts keep tags + the LLM match through review.
   - **ImportMode setting** (Review/Smart/Auto; Smart default; legacy `AutoConfirmImports` still
     honored) — Smart auto-confirms only when every line resolves via alias or ≥ 0.8-confidence
     match to an existing product. Importer holds a static scan lock; failed imports are listed on
     Upload ("couldn't be read") with Retry (re-extracts from the saved audit copy) and Discard.
   - **Engine:** `IntervalSpreadDays` (IQR of the driving samples) widens the DueSoon window;
     `StockUpFactor` (extend-only, **uncapped since 2026-07-28** — see item 19) stretches the due date
     after a bigger-than-usual buy;
     same-day signal ties deliberately lose to the purchase (documented + pinned by a test).
   - **`PredictionBacktest` (Core)** — walk-forward self-scoring of the engine, rendered live on
     `/accuracy` next to the extraction eval.
   - **`tests/ShelfAware.Web.Tests`** — real EF on in-memory SQLite (FKs + unique indexes enforced);
     covers the confirmation service, importer routing, and the product-delete FK regression.
   - **Chat can navigate the UI:** `ChatResult.NavigateTo` (a relative URL) is set by the `open_page`
     and `read_recipe` tools; the voice/chat surfaces apply it via NavigationManager after showing/speaking
     the reply. `open_page` also handles `page="recipes"` + `product_name` → `/recipes?uses={id}` (recipes
     that use a product). Recipe names resolve exact → substring → token containment ≥ 0.6 (unique winner).
     **"Stop listening"** (`VoiceCommands`, Core, plain code — whole-utterance match, filler tolerated)
     ends the conversation BEFORE the LLM is called; cookalong.js also force-closes the session on the phrase.
   - **Hands-free navigation (v2.2):** the conversational agent moved out of the dashboard into
     `Components/Layout/VoiceAgent.razor`, hosted in `MainLayout` so it **survives navigation and keeps
     listening** — enabling a chain like "go to the chicken → recipes that use it → read me the second
     one". This required going **global interactive** (see Decisions). It keeps listening after an
     `open_page` nav but stands down on a hand-off (`ChatResult.HandsOff`, set by `read_recipe`) where the
     reader makes its own audio. `read_recipe` navigation prefers the **listening cook-along agent** when
     the ElevenLabs agent is configured (fully voice-controllable: next/back/stop + "go to the assistant"),
     and **falls back to the button-controlled read-aloud** if cook-along can't connect. Both readers expose
     a "🎤 Back to assistant" hand-back (a button; cook-along also takes the spoken phrase) that resumes the
     agent via `VoiceCoordinator`. **Screen-aware references** ("the second one") work because the page on
     screen publishes its list to `VoiceCoordinator.ScreenContext`, which the agent passes into
     `IPantryChat.HandleAsync(screenContext)` for injection into the system prompt.

8. **v2.3 — full-site audit, BYOK, and fixes (2026-07-05; all ✅ committed + pushed):**
   - **Audit hardening** — `QuerySplittingBehavior.SplitQuery` + `AsNoTracking` on read loads (kills the EF
     cartesian-`Include` [20504] warning) (`c526648`); page catch-alls log via `ILogger`, rethrow
     `OperationCanceledException`, and stop leaking `ex.Message`, and `AnthropicPantryChat` wraps each
     tool-handler call so a thrown tool exception becomes an error result instead of blanking the dashboard
     box (`50b9e2b`); ProductDetail reloads on id change (`d1618ff`); NotFound/Error use the design system
     (`d927f56`); dashboard double-tap guard + SplitButton a11y + tidy EF writes + table captions (`5739c3a`).
   - **BYOK — bring your own key** — public/source-available posture: the deployed demo ships **no usable keys**;
     visitors bring their own with minimal effort; Jordan's keys are never used live. The `IChatClient` seam
     means service CODE didn't change — BYOK is a lifetime/wiring change (singleton→**scoped**), not per-call:
     - **Provider seam** (`10a8fcb`): `AiProvider` enum + `IChatClientFactory`/`ChatClientFactory` (Anthropic
       via the SDK adapter; OpenAI via `Microsoft.Extensions.AI.OpenAI`). Config-driven; keyless boot preserved.
     - **Per-circuit clients** (`5ffa466`): `CircuitAiSettings` (scoped, defaults to `LlmOptions`, overlaid by
       the browser) + `ByokChatClient` (scoped delegating `IChatClient` that builds the real client at CALL
       time, robust to the browser settings loading late) + `AiSettingsLoader.razor` + `wwwroot/js/ai-settings.js`
       (localStorage `shelfaware.ai`). AI services + importer are **scoped**; store/inbox/settings/confirmation
       stay singleton; the startup receipt scan runs in its own scope, owner-key-only (skipped on a keyless deploy).
     - **CSP + security headers** (`4a6cb0f`): `script-src 'self' https://esm.sh` (no unsafe-inline/eval),
       `connect-src` locked to self + ElevenLabs, object/base/frame-ancestors/form-action locked, +
       nosniff/Referrer-Policy/X-Frame-Options/Permissions-Policy(mic=self). Keys never persist/log; they transit
       server RAM only during a call. (Dev relaxes script/connect-src — see the CSP-vs-hot-reload gotcha below.)
     - **Settings UI** (`fba756f`): provider dropdown, masked key, editable per-module model datalists, optional
       EL key + agent id, **Forget-my-key** (clears both stores) + **session-only** toggle.
     - **Voice keyed per-circuit** (`b140b48`, `b959d4a`): `IVoiceCredentials`/`CircuitVoiceCredentials`; speech
       services attach `xi-api-key` PER REQUEST from the scoped creds (fail soft without one); the signed-url
       endpoint uses the visitor's key/agent, **rate-limited 12/min per IP**; cook-along sends the visitor's key
       headers; EL SDK pinned `@elevenlabs/client@1.14.0` (kept on `esm.sh` — a multi-module ESM SDK can't be
       vendored without a build step).
     - **README/BYOK docs DONE (2026-07-09):** "Whose keys?" section in the README (byok/managed/`Llm:KeyMode`,
       quota keys, the honest key-custody story). The remaining README placeholders are the two captures —
       capture plan in `docs/demo-gif-storyboard.md` (delete that file when `docs/demo.gif` lands).
       **[demo.gif landed + storyboard deleted 2026-07-12 (`5f34b24`); this note was never updated — which
       cost a wrong "remaining work" claim on 2026-08-11. Only `accuracy.png` is still open.]**
   - **Fixes** — (a) short-cadence items never left Running Low after a restock: the flat 3-day DueSoon floor
     could span the whole cadence, so a fresh stock-back re-anchored straight back into the window; now capped
     at `interval - 1`, regression-tested (`6b2c32b`). (b) `/recipes?uses=` only matched top-level recipes, so an
     adapted variant that swapped in a product its original never used didn't show; variants now match on their
     own ingredients, with the non-matching original shown as a muted "for reference" row (`7c805e5`). (c) The
     strict CSP broke VS Browser Link + hot reload in dev — relaxed `script-src`/`connect-src` in Development
     only (`fd580bd`; see the gotcha in Environment notes).

9. **v3 — Accounts & households (2026-07-07, branch `feature/auth-households`):**
   - **Every page requires a signed-in user.** ASP.NET Core Identity, cookie auth, local email+password.
     **Identity lives in its OWN SQLite file (`app-data/auth.db`,** `AuthDbContext : IdentityDbContext<AppUser>`)
     so `EnsureCreated` builds the auth schema everywhere with no migrations and the pantry context stays
     free of Identity noise. `Auth/` holds the domain (`AppUser`, `Household`, `HouseholdService`,
     `HouseholdClaimsPrincipalFactory`, `AuthOptions`).
   - **Households are the tenancy unit** — accounts belong to exactly one (created at registration, or
     joined via a CSPRNG **invite code**); ALL pantry data is household-scoped. Every pantry entity
     implements `IHouseholdOwned`; `ShelfAwareDbContext` has a per-instance `HouseholdId` driving a global
     query filter on every table + SaveChanges stamping on inserts (and, since the 7/15 hardening pass in
     item 12, **refusing** cross-household updates/deletes — the filter never sees those). `AppSettings` =
     composite PK `(HouseholdId, Key)`; alias uniqueness = `(HouseholdId, Merchant, RawText)`.
   - **`IHouseholdDbFactory` is THE way to a pantry context** (scoped; pre-sets `HouseholdId` from the
     scoped `ICurrentHousehold`: `UseFixed` pin → HttpContext claim → circuit auth state). The raw
     `IDbContextFactory` is bootstrap-only. Formerly-singleton data services (store, settings, inbox,
     confirmation, rename, seeder) are **scoped** now. The startup receipt scan runs once per household
     that configured a folder (`IgnoreQueryFilters` enumeration + `UseFixed` per scope).
   - **Account pages are Blazor components on static SSR** (`Components/Account/`): auth cookies can't be
     set over a circuit, so they carry `[ExcludeFromInteractiveRouting]` and `App.razor` picks the render
     mode per page (`HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null` — BOTH outlets).
     They use `AccountLayout`, NOT MainLayout (whose VoiceAgent/AiSettingsLoader islands must not spin up
     circuits pre-auth). Zero scripts beyond `js/account.js` (progressive enhancement) — strict CSP holds.
   - **Security posture:** registration gate is server-side (`Auth:AllowRegistration`; first-user bootstrap
     + invite-join always open — but invites expire/limit/revoke since item 12); logout bumps the security
     stamp (all circuits/devices die within the
     5-minute revalidation) then clears the cookie; per-IP rate limit on `/Account` POSTs atop Identity
     lockout; `/api/data/export` + `/api/cookalong/signed-url` require auth (they answer with a status
     code rather than an HTML redirect — see the block above them in `Program.cs`: **there is no API**,
     they're the only two things the browser needs a real HTTP request for, and a real API would go under
     `/api/v1/` with its own auth story);
     DataProtection keys persist to `app-data/keys` (DPAPI-encrypted on Windows) so republish doesn't log
     everyone out. **Backup set is now `shelfaware.db` + `auth.db` + `keys/`.**
   - **BREAKING SCHEMA — v3 needs a fresh pantry DB.** No in-place upgrade (Jordan's call: wipe + re-import
     receipts). The old EnsureColumn/CREATE-IF-NOT-EXISTS additive block is REMOVED (it only served pre-v3
     DBs); `PantryDbGuard` fails fast on an old file with delete-and-restart instructions.
   - Managed (non-BYOK) keys stay **server-wide** — exactly as before; BYOK stays per-circuit/browser.
   - **Metering (managed mode only):** `AiUsage` (one row per household/day) + `AiUsageMeter` +
     `MeteredChatClient` atop `ByokChatClient` — every IChatClient call quota-checked/recorded; the
     cook-along endpoint gets a per-household mint quota. Config: `Llm:DailyCallLimit`,
     `Llm:DailyTokenLimit`, `ElevenLabs:DailySignedUrlLimit` (all null = unlimited, the self-host
     default). BYOK circuits are NEVER metered. Billing/pricing = Jordan's separate workstream.
   - **OAuth (config-gated):** Google login registers only when `Authentication:Google:ClientId` (+
     `:ClientSecret`) is configured — put them in user-secrets, never committed. Unconfigured = zero
     OAuth surface. First external sign-in runs the SAME registration gate + household chooser
     (`Components/Account/Pages/ExternalLogin.razor`).

10. **v3.2 — verified-receipt self-eval + usage transparency (2026-07-12):**
   - **`Receipt.VerifiedForEval`** — the user's explicit "I checked every line" assertion (Upload review
     checkbox, or retro-verify on `/receipts`). THE trust boundary for accuracy ground truth: machine
     confirms can never set it (same principle as `writeAliases`), and it's a parameter on the ONE
     confirm path. Ships via **`AdditiveSchema` (Web/Data) — the post-v3 additive-migration seam**:
     idempotent `ALTER TABLE … ADD COLUMN` on startup after EnsureCreated. Additive DEFAULT-valued
     columns only; anything structural stays a fresh-DB change.
   - **`ExtractionScorer` (Core/Evaluation)** — the scoring math (containment matcher, plural folding,
     aggregates) moved out of the Evals console so the offline harness and the in-app check share one
     definition of "accurate". Unit-tested now (it wasn't testable as console-local functions).
   - **`/accuracy` "Your receipts"** — `ReceiptSelfEval` (Web, scoped) re-reads each verified receipt
     from its stored audit copy (`app-data/receipts/<folder>/page-*`, the Retry path's files) and
     scores against the confirmed lines. On-demand button only (a vision call per receipt — token
     disclaimer shown, with today's usage); last run persists per household in AppSettings
     (`SelfEvalResults`). Runs on the circuit's key: BYOK grades on the visitor's wallet, managed is
     metered/quota'd like any call. "Export fixture labels" downloads the harness's expected.json shape.
   - **Usage recorded in EVERY key mode** — `MeteredChatClient` now always records calls+tokens to the
     household's `AiUsage` row; quotas remain managed-only (BYOK: recorded, never limited). Settings
     gains an "AI usage" panel (today + 14-day daily table via `AiUsageMeter.GetRecentAsync`).

12. **Security hardening from the adversarial tenancy review (2026-07-15 — ✅ MERGED to master, PUSHED, and
   LIVE on the tailnet; 17 commits, 609 tests green):**
   An adversarial review hunted for a path where household A reads/writes B's data and **found none** — the
   boundary held (raw `IDbContextFactory` really is bootstrap-only; the one `IgnoreQueryFilters` really does
   only enumerate which households exist **[true when written — items 47/49 later added the admin reader's
   cross-household read and the resolve's column-scoped write, each gated and documented in place]**; both
   API endpoints scope to the caller's claim; every tenant table
   is filtered). Two suspicions were tested rather than assumed and came back clean: **EF's `FindAsync` DOES
   apply global query filters** (so `EfPantryStore`'s "the filtered lookup enforces it" comment is correct),
   and **`AddDbContextFactory` registers the context type as Scoped** (so `HouseholdService`/`Register.razor`
   injecting a bare `AuthDbContext` is right, and its one-transaction claim holds). What the review did find,
   all fixed here:
   - **Tenancy is enforced on WRITES now, not just reads** (`ShelfAwareDbContext.EnforceHousehold`). The query
     filter protects reads; EF builds updates/deletes from the change tracker keyed on the PK alone, so no
     filter is ever consulted for them. Added → stamped when empty, **refused** when it names another
     household (the stamp used to be permissive by design); Modified/Deleted → **refused** when the entity's
     household isn't the context's. Unscoped context untouched. This closes the `?? f` detached-delete shape
     for good; the three call sites dropped the fallback anyway (it also turned a double-tap into a
     `DbUpdateConcurrencyException`).
   - **`ReceiptStorage` (Web/Data) owns receipt images**, filed per household under a hash of its id, the way
     `CachingTextToSpeech` owns clips. "Delete my data" left every receipt image on disk **permanently** —
     `ImagePath` was the only pointer and the same transaction destroyed it. Deletion now runs by tree AND by
     each row's stored path (reaches pre-scoping rows; no file migration). Fell out of it: five hand-rolled
     `Path.Combine(ReceiptsDir, …)` call sites collapsed, the extension↔media-type map went from THREE copies
     to one (`ReceiptMediaTypes`), and the household-folder hash is now shared (`HouseholdFolder`).
   - **`SettingKeys` classifies every key `Config` vs `UserContent`.** The delete skipped AppSettings as "app
     configuration", which stopped being true when it grew `LastRecipeSuggestions` + `SelfEvalResults`
     (merchant names, dates). `SelfEvalResults` wasn't even declared there. A reflection test fails if a new
     key is in neither list, so the choice can't be defaulted to "survives a delete".
     **[GONE 2026-08-01: the delete now takes the whole settings table (item 33), so the split had no
     consumer left and both it and its reflection test were removed. The rule it enforced survives in a
     stronger form — nothing can outlive a wipe, so nothing has to be classified to stop it.]**
   - **`Receipts:AllowedRoot`** (unset = today's behaviour, so the self-host is unchanged) confines the receipt
     folder. Unvalidated, it's an arbitrary-path read of every image/PDF the server can see. `ReceiptFolderPolicy`
     is asked by Settings (friendly refusal) **and** by the inbox (the real boundary — a stored setting can
     outlive the rules it was written under). GetFullPath first; trailing-separator compare so `<root>-old`
     isn't "inside" `<root>`; UNC refused when confined. **[GONE 2026-07-22: the whole folder-import feature
     was retired (item 17), taking the policy, the inbox, and the read it confined with it.]**
   - **Invite codes are no longer permanent bearer credentials**: `Auth:InviteCodeLifetimeDays` (unset = never),
     `InviteMaxUses`/`InviteUseCount` (a "single use" checkbox), and **member removal** — which never existed.
     The use is claimed with a **conditional update**, not read-then-increment, or two people redeeming a
     single-use code race past the check. **Removal works because it bumps the security stamp** — the household
     id is in the COOKIE, so clearing the column alone leaves them reading the pantry until it's re-issued
     (bound: the 5-minute revalidation). Can't remove yourself or the last member (a household with nobody in
     it is data nobody can reach).
   - **`/Account/Household`** is where a signed-in account with no household lands (reachable for the first
     time now that removal exists). **The guard is MIDDLEWARE, not a component** — found by running it: the
     page body initialises before the layout, so a component guard loses the race and the user meets a 500
     from `GetRequiredIdAsync`. ⚠️ Don't move it back into `HouseholdInitializer`.
   - **`AdditiveSchema.Apply` now covers `auth.db` too.** It was described as "a fresh file per deployment
     site", which stopped being true once a deployment had accounts worth keeping; EnsureCreated never alters
     an existing file.
   - **Speech-cache trim is per household** — one shared budget deleted the oldest clips anywhere, so a heavy
     household evicted a light one's and made them re-buy the audio. Total disk is now households × `Speech:CacheMegabytes`.
     Clips loose at the cache ROOT (pre-split, from before `8cd4029`) are swept outright whatever the budget:
     every lookup goes through a household folder, so nothing can read, export, or **delete** them — 5 MB of
     unattributable recordings on the dev box, 0 on the server (it had no cache yet).
   - **"Download my data" is a ZIP**, not just JSON: `data.json` (every table) + `receipts/<ImagePath>/page-*`
     + `recipes/<name>/step-N.mp3`. The audio naming is why `RecipeNarration` (Core) exists — the cache keys a
     clip on its text AND its neighbours, so the export must segment a recipe EXACTLY as the reader did or it
     silently finds nothing. ⚠️ Don't let the reader keep its own copy of either half of that rule.
     **`ZipArchive` is a synchronous API** and Kestrel refuses sync IO on a response, so the endpoint opts in
     via `IHttpBodyControlFeature` — tests pass without it (MemoryStream doesn't care) and a browser doesn't;
     a stream that refuses sync writes pins it. The export never synthesizes: asking for your data must not
     spend your AI budget.
   - **Deploy notes (2026-07-15):** `AdditiveSchema.Apply(authDb)` migrated the live `auth.db` in place on boot
     (the three Invite columns verified present — check the `-wal`, not just the `.db`, or a fresh change looks
     missing). Pre-deploy backup at `ShelfAware-server/app-data/backup-2026-07-15-pre-security-hardening/`.
     `appsettings.json` preserved at its 7/8 timestamp per the runbook (hash-compared before/after).

13. **v3.4 — an invite code is an act, not a fixture (2026-07-15, branch `feature/invite-redesign`):**
   Item 12 made invite codes expirable, limitable, and revocable, but every household still *had* one from
   the moment it was created — permanently advertising a bearer credential to its own pantry whether or not
   anyone had ever wanted to invite a soul. The shape was wrong, not just the lifetime. Now:
   - **`Household.InviteCode` is `string?`, null by default.** `CreateForAsync` no longer mints one;
     `GenerateInviteCodeAsync` (was `RegenerateInviteCodeAsync` — it's now the *only* way a code appears, so
     "generate" and "regenerate" are the same call) mints on demand, **defaulting to `maxUses: 1`**; new
     `ClearInviteCodeAsync` revokes in one click. Settings shows "—" + Generate, or code + Copy/Replace/Clear.
   - **Spending the last use retires the code**, in the SAME `ExecuteUpdate` that claims the use — a second
     write would reintroduce the race the conditional claim exists to close. Both `SetProperty` RHS's read the
     pre-update row, so `InviteUseCount + 1` is the count the claim is about to produce. Consequence worth
     knowing: a used-up code can no longer exist, so `InviteStatus`'s not-usable branch is reachable only by
     expiry.
   - **NULL, not `""` — and this is load-bearing.** The unique index on `InviteCode` is deliberately
     unfiltered: SQLite counts NULLs as distinct, so every code-less household coexists while two households
     can never share a live code. `""` would let exactly ONE household have no code; the second registration
     on a deployment would fail to save. Pinned by `Two_code_less_households_can_coexist`.
   - **`NullableInviteCodeMigration` is the documented exception to `AdditiveSchema`** (which stays
     additive-columns-only, and stays accurate — the rebuild lives in its own class rather than making that
     docstring lie). SQLite cannot ALTER a column to nullable, so relaxing NOT NULL needs the create/copy/
     drop/rename rebuild. **It must run STRICTLY AFTER `AdditiveSchema.Apply`** — that's what puts the three
     Invite columns on a pre-7/15 auth.db, and the rebuild copies them by name. Guarded on
     `pragma_table_info.notnull` (idempotent, and a no-op on a fresh DB), transactional, and it **asserts the
     column set it knows** rather than trusting it: it names columns explicitly, so a `Household` property
     added later would otherwise be silently DROPPED on any deployment that hadn't migrated yet. Deletable
     once every deployment has booted past v3.4.
   - **The migration wipes existing codes** (Jordan's call): every one was minted permanent + unlimited by
     rules that no longer exist, so carrying one across would import exactly the credential this change stops
     issuing. Wiping evicts nobody — membership is `AspNetUsers.HouseholdId`, untouched (pinned by
     `Members_keep_their_household`).
   - **Dry-run before deploy:** the migration was run against a *copy* of the live `auth.db` (3 households, 4
     users) before merge — codes wiped, every user's household intact, and a probe insert proved the rebuilt
     index still admits multiple NULLs. Green tests wouldn't have proven the rebuilt index; do this again for
     any future rebuild.

11. **Ordering + duplicate guard + substitution-matrix batch (2026-07-12):**
   - **Grocery list "Coming up" walks the store** — same Category → urgency → name order as Buy now,
     so the whole page reads as one list (the date column still carries chronology).
   - **Duplicate guard on product adds** — the Products form and the chat `create_product` tool resolve
     through `ProductMatcher` BEFORE inserting (a twin product splits purchase history and blinds the
     predictor): exact dupes are blocked outright with a link to the existing product; fuzzy near-misses
     get a use-existing / "Add anyway" prompt (fuzzy can false-positive — the user decides).
   - **The substitution matrix feeds Adapt** — `IRecipeAdvisor.AdaptAsync` takes `PantryProduct`
     (name + also-works-as) instead of bare names; the adapter loads `Substitutes`; prompt rule 9
     prefers curated stand-ins and pins matched_product to the product name only (never the note).
   - **Swap clouds show curated stand-ins first** — `SwapCloud` (Core, tested): products whose name or
     also-works-as covers the ingredient come first (token-equal self-swaps excluded via the new
     `IngredientMatcher.IsSameFood`), AI generic forms dedupe behind them; clouds draw from EVERY
     tracked edible product, so an out-of-stock stand-in renders as a "grab" bubble.
   - **Variants adapt + swap (re-root)** — the `!isVariant` gates are gone; adapting a variant uses the
     variant's content as the base but saves the result as a sibling under the ORIGINAL (ParentRecipeId
     re-rooted), so families stay flat and the signature dedupe sees the whole group.
   - **Red recipe rows explain themselves** (same day, from Jordan's real ground-beef confusion) —
     suggestion-card ✓/🛒 trusts the model's matched_product only when POSITIVE and falls back to
     `IngredientMatcher` (`HaveSuggested`), so pre-save and post-save views can't disagree; and when a
     red row's covering product is merely predicted run-out (`PantryOnHand.EdibleOutOfStock`, the exact
     complement of on-hand), the row says "you may still have X — it just looks run-out" with a one-tap
     Restocked (the same status-only signal as the dashboard). A red mark with no hint = a genuine gap.
   - **"Get ideas" batches persist** (same day) — the latest suggestion batch is stored per household
     (`SettingKeys.LastRecipeSuggestions`, JSON `SuggestionSnapshot` in Recipes.razor) and rehydrated in
     OnInitializedAsync, with an "Ideas for '…'" header + Clear-ideas button. Replaced only on a
     SUCCESSFUL new batch (a failed call keeps the old cards on screen AND in storage). `Have`/`ToGrab`
     are `[JsonIgnore]` — availability marks must recompute live, never replay the stored verdict.

14. **v3.5 — Variety (2026-07-17, branch `feature/variety`):** flavor/varietal is per-purchase
   metadata now, exactly like Brand and Size — the fourth line of the data-model rule below.
   - **`Variety` (`string?`) on `ReceiptLine` + `PurchaseEvent` + `ExtractedLine`** (AdditiveSchema
     columns, so live DBs migrate on boot). Extraction prompt rule 3 now STRIPS flavor/varietal into
     the `variety` field ("Kool-Aid Strawberry Drk Mix" → name "Drink Mix", variety "Strawberry")
     while type/cut/form/lean% STAY in the name — "Whole Milk" keeps Whole, "Chicken Breast" keeps
     Breast, and an ingredient that IS the item ("Chicken Jerky Dog Treats") is not a flavor. Rule 12
     matches existing products across flavor differences like it already did across brand + size.
     Live-verified: a synthetic receipt extracted Strawberry/Grape/Gala; milk got null.
   - **The cadence stays the ITEM's** — pooled over every brand and variety (Jordan's spec:
     "frequency is determined collectively, not individually"). Nothing in the predictor changed.
     Product Detail gains "Varieties bought" (count · last bought · avg price — strawberry pools
     across Kool-Aid AND Crystal Light) plus a Variety column in Recent purchases; the Upload review
     grid gains an editable Variety column.
   - **`ProductMergeService` + a ⇆ Merge panel on Product Detail** — the repair path for history:
     pre-variety products carry the flavor in their NAME ("Strawberry Drink Mix") and can never roll
     up on their own. Merge moves purchases/lines/aliases/signals (immediate `ExecuteUpdate` through
     the household filter, BEFORE the source delete, one transaction — purchases/signals/tags cascade
     on product delete and ReceiptLine.ProductId has no delete action), unions tags+substitutes,
     re-points `RecipeIngredient.MatchedProduct` (the rename rule), and can stamp the moved rows'
     Variety — `SuggestVarietyLabel` pre-fills it from the name diff, filling only NULLs (COALESCE).
     ⚠️ Until old split products are merged, extraction's existing_product/matcher may still route a
     new flavor into a variety-named product (substring match) — review catches it; merging fixes it.
   - **Transient edit panels reset on product switch** in ProductDetail's OnParametersSetAsync —
     found live: a merge navigates the reused component instance to the TARGET, which arrived with
     the panel still open offering a stale candidate list including itself. Don't remove that reset.
   - Demo seeder: `Seed.BuyVariants` rotates brand+variety per buy (Drink Mix hero, Apples, yogurt).

15. **v3.6 — Expiration dates, opt-in (2026-07-18, branch `feature/expiration-dates`):** the label's
   date as per-purchase metadata — the fifth line of the data-model rule (after Brand/Size/Variety),
   with one difference: **human-entered only, never extracted** (receipts don't print it).
   - **`ExpirationDate` (`DateOnly?`) on `ReceiptLine` + `PurchaseEvent`** (AdditiveSchema columns).
     Only the LATEST purchase's date governs — rebuying supersedes the old jug even dateless; among
     same-day purchases the LONGEST date wins (you'd open the shorter-dated one first). Never feeds
     either cadence rhythm.
   - **Derived state, not a fired event.** `ReplenishmentPredictor.Predict(product, today,
     honorExpirations)` computes it: past the label ("best by" day itself is still good) → pinned
     Overdue with DueDate = the label date; `ExpiresOn`/`Expired`/`ExpirationOverridden`/
     `DueCappedByExpiration` ride on `PredictionResult`. No background sweeper exists to
     double-fire, miss a slept-through day, or re-flag after an override. ⚠️ **`honorExpirations`
     defaults FALSE deliberately** — a forgotten call site fails INERT (no expiry state for an
     opted-in household, a visible gap) rather than LOUD (phantom pins for an opted-out one).
     Don't "fix" the default.
   - **Before the label, the date HARD-CAPS the due date** (Jordan's call, 7/18): the cadence
     estimates how long stock usually lasts, the label bounds how long it CAN — `DueDate =
     min(rhythm, label)`, never max. Escalate-only: the cap pulls dates earlier and bumps status up
     (it even gives a still-learning one-purchase item a real due date from the label alone), never
     calms a warning. Consequence that IS the feature: an expiring item flows into Due Soon → the
     dashboard and grocery list BEFORE it dies, through the existing machinery — no expiration
     column on any grid, deliberately. Only Product Detail annotates ("· 🏷️ capped by the
     expiration date").
   - **Restocked dated AFTER the label overrides it** ("I froze it" beats the sticker) — pin AND
     cap stand down (half an override would be a lie), and the Product Detail panel SAYS
     "overridden" (Jordan's requirement: the human must never wonder why a date they set stopped
     counting). **Restocked ON/BEFORE the label day is NOT an override** — it's just "I have it";
     the item in hand IS the labeled item, and people tap Restocked casually, so a casual tap must
     not silently disarm the feature. The expected freezer-household flow is reactive: the app asks
     once at the label ("still good?"), one Restocked tap answers for that purchase; chronic
     freezer items should Clear the date or not be dated. An explicit OutNow keeps its own due
     date; `Expired` still reports.
   - **Toggle:** `SettingKeys.TrackExpirationDates` (Config; default off — it's the most
     ritual-heavy field in the app). Off is DORMANT, not destructive: dates kept, nothing fires or
     renders anywhere (grid column, panel, dashboard note, chat tool all gate on
     `GetTrackExpirationDatesAsync` — THE one definition of "on"). `PantryOnHand` threads the flag
     (expired chicken ≠ on-hand chicken); **the backtest stays expiration-blind on purpose** (it
     scores the learned rhythm, and an expiry pin would overwrite DueDate with a label fact).
   - **One write path:** `IPantryStore.SetExpirationAsync` stamps EVERY latest-day purchase (the
     engine takes that day's longest date, so a stale longer sibling would silently outvote the
     user) — shared by the Product Detail editor and the `set_expiration` chat tool. The tool
     errors on unparseable dates instead of clearing ("Friday" passed raw must not wipe a date),
     and the chat system prompt now includes **today's date with weekday** so the model resolves
     relative dates itself (rule 6b: an expiration statement is never a `record_signal`).
   - Live-verified end to end on the dev server (fresh household + sample data): toggle → panel →
     past date pins with "Expired Jul 16" dashboard note → Restocked → visible override; review
     grid Expires column → confirm → purchase carries it; quick-update chat "expires July 30th" →
     resolved date → panel. 658 tests green (19 new).

16. **v3.7 — Reports tab (2026-07-18, branch `feature/reports`):** printable, configurable reports —
   wife-envisioned, preset-first (a form is never the front door). As-built decisions the code can't say:
   - **`AdditiveSchema.EnsureTable` is the pattern for post-v3 NEW TABLES** (MealEvents, SavedReports):
     the DDL is lifted from EF's `GenerateCreateScript()` at runtime — no hand-written second copy of the
     schema — and a schema-parity test compares sqlite_master fingerprints of the migrated vs fresh paths.
     Every new table also walks the full drill: query filter + stamping, isolation tests, export
     `data.json`, delete-my-data, CountAll. There is no reflection test that enforces this — it's manual.
   - **`ReportSpecRules` is the one home of the chart honesty rules**, consumed by BOTH the builder UI
     (human-readable objections, Run disabled) and `ReportEngine.Run` (throws). Don't let a new surface
     run a spec without it. The rules encode: quantity never sums across products; unit price is
     dominant-size PAID prices only (gaps, not zeros); tag series OVERLAP by design and never
     stack/total; partitioning splits POOL their remainder (dropping = falsifying the stack — caught
     live); TopN's ≤8 cap is about chart COLOR SLOTS so it deliberately spares `Chart=Table` (the
     report card's top-10 items — the overbroad version 500'd the page).
   - **`ReportSpecUrl` is THE spec serialization** — URL, saved report row, and chat nav are all the
     same string; parsing is forgiving by design (old links degrade to defaults, never fail the page).
   - **Charts are hand-rolled SVG** (Jordan's call: no vendor) — validated 8-slot palette
     (`--chart-1..8`, fixed order = the CVD mechanism, re-stepped for dark, `--chart-pooled` gray for
     pooled series), data table always rendered beneath (three light slots are sub-3:1 contrast; the
     table is the relief rule, the reader surface, and what makes print a document). ⚠️ Razor reserves
     lowercase `<text>` (RZ1023) — SVG text renders via `ChartFormat.SvgText` (encoding MarkupString).
     ⚠️ Browsers strip HTML backgrounds in print — legend swatches/rank bars carry
     `print-color-adjust: exact` + a hairline border; `.voice-agent` is hidden in GLOBAL print rules.
   - **Waste watch never claims waste**: `ExpirationOutcomes` (Core) judges dated purchases from
     evidence only — a quiet label-pass is "worth checking, $ at stake". Don't strengthen the claim.
   - **`MealEvent` vs `TimesEaten`**: the counter is lifetime (Pick-for-me) and keeps pre-log history;
     the event log is dated and started 7/18 — they legitimately disagree by the pre-log remainder.
     Never backfill dates.

17. **v3.8 — Folder import retired; Smart confirm moved to uploads (2026-07-22, branch
   `feature/upload-smart-confirm`):** the folder-watching auto-importer was built for the bootstrap
   era's mass imports; multi-receipt upload superseded it, and on a box shared beyond the household it
   was the app's one arbitrary-path filesystem read. The transport was DELETED (inbox seam,
   `ReceiptFolder` setting, startup scan, Settings "Scan now", `import_receipts` chat tool,
   `Receipts:AllowedRoot` + `ReceiptFolderPolicy`) — deleting the surface beats confining it. The
   graduated-trust brain survived as **`ReceiptAutoConfirmer`** (Web/Ingest):
   - **Every upload path routes through it AFTER persisting the PendingReview receipt** — single,
     combined-pages, and per-file in a batch. It works off the STORED lines (same rows the review
     pre-fill reads), resolves by the same trust order (alias → model suggestion → matcher), and
     confirms via the ONE confirmation service with `writeAliases: false`. `ImportMode`
     (Review/Smart/Auto, per household, legacy `AutoConfirmImports` still parsed) kept its name, its
     setting key, and its Settings UI (reworded for uploads).
   - **Deliberate tightening vs the old importer: Smart queues an UNDATED receipt.** The purchase
     date drives every prediction; "assume today" is the silent guess review exists to catch. Auto
     keeps its all-or-nothing contract (undated = today). Pinned by a test.
   - **The Upload page says what will happen and what happened**: an active-mode hint before upload,
     per-file "recorded automatically" vs "in the review queue below" in the batch readout, and an
     auto-confirm Done panel that mirrors the manual summary + links to /receipts and Settings.
   - `Receipt.SourceFile` column stays (dropping a SQLite column is a structural rebuild old rows
     aren't worth) — documented HISTORICAL on the entity. Stray `ReceiptFolder` AppSettings rows in
     live DBs are inert; nothing reads the key.
   - "Import my receipts" in chat/voice now lands on the Upload page via `open_page` (which already
     mapped "upload"/"receipt") — no prompt change needed.

18. **v3.9 — Remove a receipt (2026-07-22, same branch as v3.8):** `ReceiptRemovalService` (Web/Data)
   is the confirm's inverse — one transaction that removes a receipt and everything its confirm did.
   Built because v3.8 sharpened a pre-existing risk: uploads have no file dedup, and Smart confirm
   commits a trusted duplicate without the review pause where a human used to notice.
   - **Provenance, never value-matching.** Purchases come back by `PurchaseEvent.ReceiptId` (already
     stamped); two NEW additive columns carry the rest: `Product.CreatedByReceiptId` (the confirm
     that created it) and `ProductAlias.TaughtByReceiptId` (the confirm that taught/last re-pointed
     it). A receipt with zero provenance-linked purchases REFUSES removal ("can't be safely
     identified") rather than guessing — pre-provenance history is not undoable, by design.
   - **⚠️ An alias is re-stamped only when re-POINTED to a different product** (see the condition in
     `ReceiptConfirmationService`): a duplicate confirm re-walking the same pairing must not become
     its teacher, or removing the dupe un-teaches what the original taught. This was a REAL bug the
     removal test suite caught pre-commit (`The_duplicate_upload_scenario…` failed on exactly this)
     — don't simplify the stamp back to "always".
   - **A product the receipt introduced is deleted only while it has NO other history** — a purchase
     from any other source (including chat, which has no ReceiptId) or any inventory signal means
     the household invested in it: it stays, with the breadcrumb nulled (no pointing at ghosts).
     Deliberately NOT undone: re-tracking, and tags added to pre-existing products (no provenance;
     cosmetic residue at worst).
   - **Files after the commit**: the image folder is deleted via `ReceiptStorage.DeleteFolder` only
     once SaveChanges succeeds — an orphaned folder beats a row whose image is gone.
   - UI: /receipts gets "Remove receipt…" (inline consequences-first confirm; per-row `Removable`
     from one provenance query), and the Upload done-panel gets ↩ Undo (offered after auto AND
     manual confirms; hidden on AlreadyConfirmed — nothing coherent to undo). Both live-verified,
     including the AdditiveSchema pass on an existing DB.
   - **`ReceiptDuplicateDetector` (Web/Ingest) is the front door to the same problem**: a detected
     exact duplicate NEVER auto-confirms, in ANY mode — Auto included, deliberately breaking its
     "confirm everything" contract for this one case (silent double-recording is the mistake the
     router exists to not automate). Strict match = same date + merchant + line count + lines +
     prices; cheapest-first (one indexed SQL prefilter, then sorted-multiset comparison with
     early-outs). ⚠️ **Lines compare on RawText FIRST, normalized names second** — review edits
     never touch RawText, so a re-scan of the same photo matches even after the original's names
     were corrected; don't "simplify" to names-only. Prediction impact of removal needs no code:
     cadences are derived from live PurchaseEvents on every read, so deleted purchases stop
     counting immediately.

19. **The stock-up ceiling is gone (2026-07-28, branch `feature/quantity-on-hand`).** `StockUpFactor`
   used to be `Math.Min(ratio, 3.0)`. Jordan's call, and right: buy twelve when you usually buy one and
   you *have* twelve — the ceiling meant the app started asking for more while nine were still in the
   freezer, which is the exact behaviour v4.0 exists to stop. Nothing in the data ever justified "3".
   - **What the cap was really guarding, and why it's now redundant.** The comment said "so one bulk run
     can't push an item out of sight for a year". But a **dated** item can't stretch past its label
     either way — v3.6's expiration cap is escalate-only and applies on top of the stock-up (pinned by
     `AStockUp_CannotStretchTheProjectionPastTheLabel`). So the uncapped range only ever covers undated
     non-perishables, which are precisely the things it IS safe to be quiet about. The risk it removed
     was concentrated where the risk doesn't live.
   - **The honest limit that remains, and the cap never fixed:** the engine can't tell a freezer
     stock-up from twenty sodas bought for a party. A ratio of 20 is right for one and badly wrong for
     the other, and a ceiling of 3 was merely wrong for both. A real count (DESIGN.md §13) is what
     actually answers it.
   - Ripple worth knowing: fixtures tuned around the ceiling had to be re-aged, including the demo
     seeder's hoard hero. A 6× buy now projects ~84 days on a 14-day rhythm, so the hero's last trip
     moved to 130 days ago to still read as overdue. **Its dates are load-bearing** — shorten the
     silence and the grocery list is right to ask again.
   - ⚠️ **`MaxProjectionDays` (730) replaced it, and is a different KIND of thing.** The `/pre-push`
     gate caught what removing the ceiling exposed: `Quantity` is clamped upward from zero on the way
     in (`ReceiptConfirmationService`) but has **no upper bound**, so one misread line — a price or a
     size read as a count — could project an item years out. The failure was SILENT (no exception, the
     item just vanishes from every list with nothing saying why, and it isn't overdue so "What's piling
     up" can't surface it either); only an absurd value crashed, and a probe pinned that at
     500,000,000 → `ArgumentOutOfRangeException` from `DateOnly.AddDays`. The bound is on the
     ARITHMETIC: it clamps in double space (so the `int` cast can't overflow), its floor is the
     unstretched median (so a legitimately slow rhythm is never shortened), and twelve roasts on a
     14-day rhythm is 168 days — nowhere near it. **Don't read it as the 3× ceiling coming back.**
   - **`SignalDate.Of` (Core/Domain) is now the ONE reading of when a signal happened**, from the same
     gate. Seven call sites used `DateOnly.FromDateTime(s.SignaledAt.Date)` and one (Waste watch's)
     used `.LocalDateTime` — identical on a single-timezone box, and a silent one-day shift of every
     historical row on any deployment that moves timezone (the `TZ` gotcha below). `.Date` wins because
     it keeps the day a signal was RECORDED on; the predictor already used it, and a signal that pairs
     into a burn cycle in the engine but reads a day later in a report is two screens disagreeing.

20. **Review pass on the counting arc (2026-07-28, same branch).** An in-process review of the whole
   v4.0 diff found nine real things; the four that changed behaviour are decisions worth keeping:
   - ⚠️ **A count may only silence a recommendation that rests on "how many".** It now stands down for
     an expiration label (cap or pin) and for a `RunningLow` tapped SINCE the attestation, as well as
     for the OutNow it already deferred to. The label case was the sharp one: suppression turned a
     DueSoon-by-label item Stocked, so v3.6's escalate-only cap silently became escalate-then-mute and
     the household would first hear about the milk the day AFTER it died. Both directions are pinned
     (`ACount_CannotSilenceAnApproachingExpiration` + `TheSameCount_SuppressesOnceTheLabelIsOutOfTheWay`).
     Falls out of it: under suppression `DueDate` can only ever be the rhythm's own projection, which is
     what makes Product Detail's "its rhythm would otherwise have asked for it on X" true by construction.
   - ⚠️ **The drift horizon is per PACKAGE: `median ÷ typical trip × count`.** The driving median is how
     long a *trip's* worth lasts — the same reading `StockUpFactor` asserts when it stretches a due date
     by (this buy ÷ typical trip) — so multiplying it by a package count gave a household buying six at
     a time a 360-day horizon instead of 60, and the drift check could never fire for exactly the bulk
     buyers §13 was built for. Two rules in one file must not disagree about what the median measures;
     `tripTotals`/`typicalTrip` are computed once and shared for that reason.
   - ⚠️ **`TypicalPackage.Of` takes `DefaultUnit`, and a COUNTED item's package is exactly 1.** A receipt
     line reading "× 6" is one purchase OF six, not a six-pack, so the median returned 6 for a habitual
     bulk buyer and one dinner emptied the freezer — which lifts suppression and re-adds the item to the
     list. Its own docstring had always said "for a counted item that's the number 1"; the code never
     had that branch, and the tests only covered a bulk buy as an *outlier* (`[1,1,1,1,6]`).
   - **An EMPTY count box is not a zero.** `editQuantity` starts null for every never-counted product, so
     `?? 0` meant one stray click on "Set count" asserted an outage — writing a real `OutNow` into the
     cadence engine from a field nobody typed in. §13.4 exists to stop machine inference becoming ground
     truth; a mis-click must not be the exception. The absolute-negative path is refused too, matching
     `SetPurchaseQuantityAsync` (relative still clamps — "used two" against one really is none).
   - **`ProductEstimate.CountNote` is THE phrasing for a suppressed row**, because the "Stocked · 3 days
     overdue" self-contradiction shipped TWICE: fixed on the grocery list, then left on the products
     grid, which got `honorQuantity: true` without the matching display change. It's a computed property
     rather than per-page markup so a new surface can't reinvent it wrongly. Same class as the
     "One prediction, one story" directive above — that rule has now been broken five times on this branch.
   - **`MealStock` (Web/Data) owns the "Ate it" decrement**, moved out of `Recipes.razor` for the §13.7
     reason (logic private to a page is logic no test can reach) — plus the §13.3 confirm step the spec
     always required, a double-tap guard, and case-insensitive matching (SQLite's `IN` is
     case-SENSITIVE, so a renamed product silently stopped decrementing with no error at all).
   - Smaller: the spend forecast now steps from `CountRunsOutOn` rather than a due date the app is
     currently telling you to ignore (`honorQuantity` was threaded there and had NO effect); DESIGN.md's
     three "not built yet" markers were stale within their own branch; and the demo seeder gained a
     **counted** hero ("Canned Black Beans", 5 on hand) — the backlog check names items worth counting
     and nothing showed what happens once you do.
   - **824 tests green, 0 warnings** (777 before this pass). A `/code-review` over these five commits
     then found 15 more, fixed in item 21 below. `set_quantity` gained the chat-layer tests and the
     system-prompt rule (6c) it shipped without — `set_expiration`, the closest precedent, had four
     tests and rule 6b. ⚠️ Its fake was ALSO more permissive than the real store, so the refusal tests
     passed vacuously until it was taught the real contract.

21. **`/code-review` over the review pass itself (2026-07-28, same branch).** 15 findings on item 20's
   own five commits; all fixed. The ones that carry a rule:
   - ⚠️ **`CountLooksStale` reports the AGE of a count and nothing else.** Moving the drift computation
     out of the suppression branch decoupled it from `Status`, so it can now be true while an item is
     **Stocked** (a later purchase re-anchored the rhythm without anyone re-counting) or already pinned
     by an OutNow. Product Detail was asserting "so it's back on the list" from the flag alone and
     telling a Stocked item's owner it was on a list it wasn't on — the "one prediction, one story"
     directive broken by the commit that cited it. **Read `Status` for the consequence, never the flag.**
   - **A same-day `RunningLow` now LOSES to the count.** `>=` gave the tie to the signal; the ordinary
     flow is "this looks low" → then go and actually count, so the count is the later and far more
     precise act. Same shape and same resolution as §6.6's documented same-day tie.
   - ⚠️ **The "Ate it" confirm re-plans in the COMMIT's own context and refuses to write if the numbers
     moved** (`MealStock.Matches`). Preview and commit are two user actions on two `DbContext`s with an
     unbounded gap — a receipt confirm or a second cook can move a count in between. The original test
     shared one context, so it asserted a guarantee the production path did not have; that is exactly how
     this shipped. Two-context tests now pin both directions.
   - **No fallback when there is no typical trip quantity** — the drift horizon stays null rather than
     reverting to the raw median, because that fallback would silently restore the per-trip reading the
     whole rule exists to remove, on the one product nobody would check. No horizon is a visible gap.
   - **`SpendForecast` (Core/Shopping)** — the forecast's stepping and its count-aware start date came
     out of `SpendInsight.razor` for the same reason `MealStock` came out of `Recipes.razor` one commit
     earlier: it is the only place a count moves a number denominated in money, and it had no test
     because it lived in a page. Applying that lesson in one file and not its neighbour was itself a finding.
   - **A counted main at zero no longer raises a confirm step** (the write is a provable no-op, so the
     friction bought nothing), and the grocery row's `Used one` finally delivers §13.5's one-tap
     correction *where the claim is made* — `ProductEstimate.OnePackage` carries the amount so the list,
     the product page and "Ate it" cannot disagree about what "one" is.
   - Smaller: `UsedOne` guards `product` in its own body (an argument is evaluated before the callee's
     null check); `onePackage` is computed once on load instead of per render *and* per click; a missing
     recipe clears the panel with a message instead of stranding it; `eatError` is keyed by recipe since
     the page renders them all; `labelIsSpeaking` dropped a redundant `|| expired` (step 7 always pins).
   - ⚠️ **The doc numbers were wrong** (`808` in CLAUDE.md and timeline.md, actual 810 at the time). Written
     from an intermediate run and never re-checked — the same false-claim-in-the-record class the
     `/pre-push` gate exists to catch, in the file that states the rule. **Re-read the number off the
     final run before writing it down.**

22. **⚠️ `DefaultUnit` is a display label and NOTHING else (2026-07-29).** It was §13.3's discriminator
   between a counted item and a weight item, and it was the wrong field twice over. Measured, not
   assumed — a read-only probe of the real dev DB: **0 of 190 products have `DefaultUnit` set, and 0 of
   537 purchases have a fractional quantity.**
   - **Nothing populates it.** The ONLY writer in the app is the manual add-a-product form
     (`Products.razor` `CreateAsync`); `ProductMergeService` merely propagates an existing value. There
     is **no editor for it afterwards**, so a receipt-imported product has it null forever. Extraction
     never sets it: prompt **rule 6** puts a weight-priced line's unit in the per-purchase **`Size`**
     ("2.31 lb @ 1.99/lb" → quantity 2.31, size "lb"). So the weight branch was unreachable, and would
     have deducted an arbitrary 1 from a 2.31 lb count — the exact arbitrariness §13.3 forbids in its
     own words.
   - **Where it IS set it can mislead.** A product declaring `"each"` or `"ct"` with quantities
     `[6, 6, 6]` took the median path and charged six for cooking one — the freezer-emptying bug the
     counted rule exists to prevent, arriving through the field meant to prevent it.
   - **The quantities are the discriminator now.** Whole-number median → counts → one is 1; fractional
     median → a measure → one package is the median. That is the same fact §13.1 already gives as its
     reason for the decimal type ("weight items are already fractional"), and it is a stronger signal
     because it is written by the same path that writes the number. The MEDIAN decides, so one
     hand-corrected 1.5 among whole counts can't flip a product into weight mode.
   - Accepted residual: a weight item whose median lands whole (beef at exactly 2.00 lb every time) reads
     as counted and deducts 1. Pinned by a test so it's a known cost, not a surprise.
   - **The lesson worth carrying:** the spec asserted a premise about its own data
     ("`Quantity` carries 2.34… display follows `DefaultUnit`") that no one had checked against the
     database. Half of it was true (extraction really does write fractional quantities for weight-priced
     lines) and half was fiction (that field is never populated). **Before building on a field, grep for
     its writers.** One `grep -rn` would have caught this before any of it was designed.

23. **The demo catalog is the test rig for states the UI can't reach (2026-07-29).** Every v4.0 concept now
   has a seeded hero, and each one is asserted by running the SEEDED rows through the real engine — the
   standard the hoard hero set ("assert the data reads as a hoard, not merely that the rows exist"). Two
   of these could not be verified any other way, which is the point:
   - **`Canned Diced Tomatoes` — a count gone stale.** Counted 3 · 110 days ago on a ~14-day rhythm, so
     three should have been gone ~68 days back. ⚠️ **No UI path can produce this**: every write stamps
     `QuantityCountedAt` as NOW, so before this hero the drift check could only be seen by waiting three
     months. Its dates are load-bearing.
   - **`Ground Chuck` — a weight item.** Fractional quantities (1.18–1.31 lb) — the shape extraction
     writes for a weight-priced line (prompt rule 6). Before this, §13.3's median branch had **no
     real-world instance at all**: 0 of 537 purchases on the real dev DB are fractional. `Unit: "lb"` is
     set for DISPLAY only; the test proves the amount comes from the fractionality, not the label.
   - **`Heavy Whipping Cream` — a counted item with a label.** 7-day rhythm putting the next buy 3 days
     out with a best-by in 2, so the label lands INSIDE the rhythm's projection — the only arrangement
     where the two can be seen to disagree. Tested both ways from one product: toggle off → the count
     suppresses; toggle on → the label wins and it reaches Due Soon before it dies. Also the catalog's
     only dated purchase, so Waste watch has something to judge.
   - `Beef Chuck Roast` (the hoard, §13.7) and `Canned Black Beans` (a fresh count that suppresses) were
     already there.
   - `Seed` gained `Unit` and `ExpiresInDays`. The label stamps only the LATEST buy (v3.6's rule), found
     by minimum days-ago rather than index 0 so a seed can list its buys in any order.
   - **Deliberately NOT seeded: a misread quantity for §13.6's correction.** A demo must not ship
     known-wrong data to show off a repair tool; the pencil is discoverable on any purchase row.

24. **A count with no rhythm behind it now does something (2026-07-29).** Checking §13.8's premises against
   the code found that every use §13 makes of a count except one is gated on a **learned rhythm** — and
   §13.8's population (bought pre-app, elsewhere, gifted, bulk) has 0 or 1 purchases by construction, so
   it has none. Measured: a counted-12-no-purchases product came back `Status=Unknown`,
   `Suppressed=False`, `RunsOut=null`, `onePackage=1`, and **on-hand for recipes whether the count said 12
   or 0**. A census would have written a number that influenced nothing. §13.7's documented hand-off
   ("that case is exactly what `TrackQuantity` exists for") was therefore a no-op too, including for the
   single-purchase quarter cow. Two rules fix it:
   - ⚠️ **`CountStaleReason.Unattested` — a rhythm-less count is asked about on AGE alone, at 90 days**
     (`UnattestedCountDays`). Without it the drift check simply doesn't apply, so the count is trusted
     FOREVER on the one population no receipt will ever correct — the longest trust given to the weakest
     evidence. The threshold is a judgement and says so. **No date is invented**: `CountRunsOutOn` stays
     null rather than implying a projection the engine can't make, and `CountStaleReason` is what lets a
     surface word the two findings differently (only one has a rhythm to have outlived). Explicit enum
     rather than inferring from a null date — a screen guessing at engine reasoning is this branch's
     signature failure.
   - **`PantryOnHand` reads the count directly.** A fresh count decides recipe stock in both directions;
     a stale one defers to the rhythm. This is the only way a count reaches makeability for rhythm-less
     stock, and it closes the sharpest hole: told the app it had twelve, ate all twelve, and recipes went
     on believing there was beef. ⚠️ A zero withholding an item here is a DISPLAY inference — §13.4 is
     untouched, a derived zero still can't write an `OutNow`.
   - **Jordan's call, and right: NOT suppressing a rhythm-less item is a FEATURE, not a gap.** The app was
     never asking you to buy it, so there is nothing to hold back; suppression silences a request, it
     doesn't announce stock. So `Status` stays `Unknown` and only the two rules above changed.
   - Fell out of it: **staleness now covers a count of ZERO.** It was gated on `> 0`, which left a stale
     zero deciding recipe stock outright while a stale positive deferred — one fact treated two ways.
     Suppression still needs `> 0` (a zero has nothing to hold back). Caught by a test I'd written
     expecting the opposite, which is the second time this arc a wrong test expectation exposed a real
     asymmetry.
   - Seeded as `Quarter Cow Ground Beef` (a count, no purchases, no receipt) so all of it is demonstrable
     now. It is the catalog's one product with no priced receipt line, and
     `Seeds_confirmed_receipt_prices_for_every_product` names it as an exact exception rather than
     loosening to a skip.

25. **The decrement asks the makeability question, and a count now bands by CONFIDENCE (2026-07-29).**
   Designing §13.8's census decrement turned up that the bug was never "nothing back-fills
   `MatchedProduct`" — it was that **`MealStock` used a different matcher from the ✓ mark above it**.
   - ⚠️ **Makeability asks `IngredientMatcher.IsSatisfied(name, matchedProduct, onHand)`** (core words, plus
     the product's curated "also works as"); the decrement matched `MatchedProduct` by name alone. So a
     recipe row could read **✓ you have this** while "Ate it" moved **nothing**. Two rules for "which
     product does this ingredient mean", which is the "one prediction, one story" fault again — in code I
     wrote four commits earlier. One rule now.
   - That fix is also what makes census stock maintainable at all: nothing back-fills `MatchedProduct` when
     a product appears, so a census product was named by no saved recipe and no tap could reach it.
   - **Ambiguity is refused, not guessed.** The looser matcher means an ingredient can be covered by
     several counted products ("ground beef" by two cuts). Taking a package off each is wrong and picking
     one silently is arbitrary, so it decrements NONE and reports them in the confirm panel
     (`MealStock.Ambiguity`). The grounded `MatchedProduct` still wins outright — that is what it's for.
     `PlanAsync` returns a `Plan` now, and `NeedsConfirmation` is takes-OR-ambiguities: a decrement the app
     declines to make is as much the household's business as one it makes.
   - **`CountConfidence` (Counted / Aging / Spent) replaces `CountStaleReason`.** Jordan's idea, and it
     resolves §13.9's conflict: one enum, one stored truth (the number + its attestation date), and
     confidence decides whether a surface may **assert** it ("4 on hand") or must **attribute** it ("you
     counted 9 on Mar 12") — the second is still true when the first has become a lie. `CountLooksStale` is
     derived from it, so flag and reason can't drift.
   - ⚠️ **A low-confidence count is NOT banded by depth, and that's the honest half of the idea.** "Plenty"
     vs "nearly out" needs a consumption rate, and `Aging` is *defined* by not having one — elapsed time
     says nothing about how much got eaten. Only `Spent` (which has a rhythm) may make a depth claim.
     §13.9's rejection of coarse depth levels therefore still stands, for a sharper reason than before.
   - **Measured, since it decides how much any of this is worth:** ~3–4 "Ate it" taps a week (17 across 8 of
     15 recipes since 6/22) against 537 purchases — so roughly a third of meals are logged through a recipe.
     A census count will be **directionally right and precisely wrong**, which is exactly the case the
     attribute-don't-assert rendering exists for. (`MealEvents` alone reads 1 and is misleading: that log
     only started 7/18. `TimesEaten` is the honest measure.)
   - Seeded `Home-Canned Tomato Sauce` (counted 140 days ago, no purchases) beside `Quarter Cow Ground Beef`
     (counted 20 days ago) — same shape, opposite confidence, so both bands are visible in the demo.

26. **`/code-review` over item 25 (2026-07-29) — 10 findings, all fixed. The first was a live regression.**
   - ⚠️ **`PantryOnHand` let a fresh count override a PINNED item.** Probed: chicken counted 3 with a label
     that passed → `Status=Overdue, Expired=True, Pinned=True`, and `inStock=True`. Same for a counted item
     with an explicit `OutNow`. So recipes offered to cook with food the app knew was **expired**, and with
     food the household had just said they were **out of** — breaking this file's own docstring ("an expired
     chicken must not count as on-hand chicken") and §13.5's "an OutNow beats the count outright", which the
     predictor implements correctly. Fixed by reading the engine's `Pinned` instead of re-deriving
     precedence. **The irony worth remembering: fixing "the count never reaches recipes" overshot into "the
     count reaches them too far" — and all four tests I'd written covered cases where the count SHOULD win,
     so 849 green tests said nothing.** `Out_of_stock_is_the_exact_complement…` can't catch it either: both
     methods negate one predicate, so they stay complements while putting the item in the wrong bucket.
   - **`IngredientMatcher.Covering` now owns the grounded-link precedence** and returns the pinned product
     ALONE when on hand. `IsSatisfied` is defined as `Covering(...).Count > 0`, so the tick on a row and the
     action taken on its behalf are one question asked once — and `MealStock` stopped re-implementing the
     precedence. Also computes the ingredient's core tokens once per candidate SET rather than once per
     candidate (asking `IsSatisfied` per product to learn which matched was M×C token work).
   - ⚠️ **Ambiguity is judged in a SECOND pass, against the complete chosen set.** One main pinned to a
     product plus a looser main covered by that same product made the panel say "not touching these" about
     an item its own take list was decrementing. Can't be folded into one loop — `chosen` is only complete
     after every main is read. Also grouped by ingredient name, since a recipe may list one main twice.
   - **`CountConfidence.NotCounted = 0` is the new default.** With `Counted` at zero, every uncounted product
     reported that its nonexistent number was believed — exactly the implicit answer a surface reads without
     checking `TrackQuantity`. `CountLooksStale` now names the two disbelieved states rather than negating
     `Counted`, since `NotCounted` is neither believed nor stale.
   - **One resolution per tap.** `ResolveAsync` → `Describe` → `Apply`: the write acts on the same loaded
     products the description came from, instead of `PlanAsync` and `ApplyAsync` each re-querying and
     agreeing by luck. Halves the queries on a confirm and strengthens the guarantee.
   - **`PantryOnHand.EdibleSplit`** returns both lists from one pass — a recipe row needs the on-hand set for
     its tick and the run-out set for its hint, so the pair used to run the full predictor twice per product
     per render. Same predicate, so no third definition of on-hand exists.
   - Smaller: a distrusted ZERO gets its own sentence ("you recorded none on … — long enough ago that Shelf
     Aware treats it as unknown now"), since "You counted 0 — isn't counting on that any more" reads as
     though the app now suspects there might be some; the band is gated on `CountLooksStale` rather than
     `== Counted` so a `NotCounted` result can't fall into the attributed form; a stale docstring claiming
     the first query reads "NAMES only" now says it also loads substitute phrases; and three tests dropped
     `null!` for a properly nullable helper parameter.
   - **861 tests green, 0 warnings.**

27. **Independent `/pre-push` gate over the finished branch (2026-07-30) — six findings, each fixed or
   decided.** Run in a fresh context against the whole diff, with every authored claim re-measured
   (the 0-of-190 / 0-of-537 probes reproduced exactly; 861/0 confirmed before the pass).
   - ⚠️ **`query_status` was the SIXTH "one prediction, one story" break, and the first that TALKS.**
     A suppressed item's reply read "Stocked (bought 4×, ~every 22 days), due 2026-07-21" — eight days
     past, spoken aloud, count never mentioned — the third surface to get `honorQuantity: true`
     without the matching display change (confirmed by probe against the seeded beans hero before the
     fix). Fixed from the same SOURCE the grids read — `SuppressedByCount`/`CountRunsOutOn` plus the
     product's own count — with wording for speech rather than `ProductEstimate.CountNote`'s cell
     form; the comment states that the wording may differ but the facts must not. Two chat tests pin
     it, including the list branch that was already right.
   - **Removal past a newer attestation is §13.2's documented ACCEPTED edge, not new code.** The
     obvious guard — skip the subtract when `QuantityCountedAt` postdates the confirm (needs a new
     `Receipt.ConfirmedAt`; none exists) — was weighed and REJECTED: the attestation date also
     advances on relative moves ("Used one"), which carry a duplicate's phantom stock forward rather
     than re-baselining, so the guard would trade today's safe-direction error (one early rebuy, one
     recount fixes it) for an inflated count and an over-silenced buy list — the §13.5 direction you
     only discover by running out. Distinguishing the attestation kinds needs the change log §13.6
     defers. §13.6's "every automated path is self-documenting" was corrected in the same pass (a
     confirm's own clock time is unrecorded — its purchases carry the receipt's purchase date).
   - **`MealStock` survives two counted products sharing a name.** No unique index exists on product
     names (probed) and the duplicate guard is a UI prompt with a real "Add anyway" — and the
     name-keyed dictionary THREW on the pair, taking down every "Ate it" in the household, planned or
     not. A shared name now maps to "cannot address" and its ingredient is refused and reported like
     any other ambiguity (candidates listed by distinct name). Pinned.
   - **§13.3's "one-tap decrement on the dashboard" described the wrong control.** The dashboard lists
     running-low items only, so a counted item appears there only once its count has STOPPED being
     believed — where the useful act is re-attesting or asserting zero, both on the product page's
     panel one tap from the card, beside the staleness sentence that makes them safe. Sentence
     reworded; no new control built.
   - Smaller: the negative-absolute count refusal has its own message (it shared the relative-move
     one, telling a "-3" typist to "set a starting count"); `CommitEatAsync` gained the
     catch/log/friendly-error shape its ProductDetail siblings in the same branch already had (a
     transient DB error mid-"Ate it" tore down the circuit with the confirm panel open).
   - Deliberately unchanged: GroceryList's `UsedOne` still ignores a `false` return from
     `SetQuantityAsync` — the only road there is a concurrent stop-counting, and the reload it already
     performs renders the corrected state; a banner for that race would be noise.
   - ⚠️ **Re-gating the fix pass caught the fix pass's own defect** — the class this branch keeps
     warning about. The new `CommitEatAsync` catch said "didn't save — try again" for a RELOAD failure
     after a successful save, inviting a second tap on a non-idempotent write (a second `MealEvent`, a
     second package off) — strictly worse than the circuit crash it replaced. And ProductDetail's
     handlers, which the fix had copied as the good precedent, carried the same latent flaw ("Used
     one" is relative: repeated, it double-decrements). All three now track whether the save completed
     and give OPPOSITE advice for the two failure points ("recorded — don't tap again" vs "didn't save
     — try again"). No page-test harness exists (no bUnit anywhere in the repo), so these handlers are
     review-verified rather than unit-pinned — stated here so the gap is a known one.
   - **864 tests green, 0 warnings** (861 before the pass).

28. **v4.1 — the feel pass (2026-07-30): six agreed design changes from the gate reviewer's "do you
   agree with the implementation?" conversation.** Jordan agreed with all six pushbacks and asked for
   them fixed, with "I do NOT want to be bothered while keeping track of my quantity" as the ruling
   constraint. The changes, and the couplings that mattered:
   - ⚠️ **A relative move no longer re-anchors the attestation clock** (`StockLedger.AdjustByHuman`).
     "Used two" states a DELTA — the person saw what they took, not the rows behind it — so stamping
     `QuantityCountedAt` let a household dutifully tapping "Used one" renew a count's credibility
     forever without anyone looking, and the drift check could never fire for the most engaged users.
     Exception, deliberate: a relative move landing at ZERO (clamp included) stamps and asserts the
     out — taking the last package IS seeing the shelf empty. §13.1 is rewritten around "the date of a
     LOOK".
   - **…which made the removal guard sound, so it exists now.** `Receipt.ConfirmedAt` (AdditiveSchema,
     NULL on pre-v4.1 confirms = subtract as ever, stamped once on the PendingReview→Confirmed
     transition) lets removal skip the subtract for a product whose count was attested AFTER the
     confirm — the recount already reflects the shelf, phantom excluded. Only sound because ONLY an
     absolute look advances the clock; a relative move deliberately does not shield the count (its
     case NEEDS the subtract or phantom stock survives). Both directions pinned. This replaces the
     "accepted edge" documented one commit earlier — its justification died with the stamping change.
   - **"Ate it" is tell-don't-ask now.** The confirm panel and its whole preview machinery
     (`MealStock.Plan`/`Describe`/`PlanAsync`/`Matches`, the two-context re-plan dance) are DELETED —
     a confirmation on every cook of the same stew gets blown through unread, protecting nothing.
     One tap commits; the notice says exactly what was taken with **↩ Undo**. `MealStock.Apply` now
     returns the ACTUAL per-product deltas (clamp-aware — taking "one package" from half a pack
     reports 0.5), and `MealStock.Restore` reverses precisely those, so the undo can never invent
     stock; restore commutes with intervening receipts/cooks, and a product whose counting stopped
     meanwhile stays dormant (the ledger's gate). Undo also removes the MealEvent and steps
     TimesEaten back. Every "Ate it" gets the notice + Undo (a mis-tap was previously permanent).
   - **Stop-counting is dormant, not destructive** — `TrackQuantity` false, number + date KEPT, the
     v3.6 toggle semantics ("off is dormant"). Every reader gates on the flag (verified: predictor,
     PantryOnHand, MealStock's query, backlog's AlreadyCounted, the ledger's Move), so the pair
     influences nothing; the product page attributes it ("you counted 14 on Mar 12") instead of
     amnesia. Receipts leave a dormant number frozen at its date — pinned.
   - **`CountingAdvice` (Core) steers against counting fast movers** — ≤10-day rhythms get a passive
     "hard to keep true" sentence on the count panel (never a gate, and never shown for a rhythm-less
     item: §13.8's census stock is the feature's best case). Someone WILL count the milk; the drift
     questions that follow must not read as the feature being broken.
   - **`IPantryStore.SetDefaultUnitAsync` + a unit box on the count panel** — display only (§13.3's
     decrement still reads fractionality), but previously the manual add form was the field's only
     writer, so a receipt-imported weight item could never say "lb". Walks the tenancy drill
     (isolation test) like every new write path.
   - **Comment trim** — the incident-retelling clauses ("caught by running it", "shipped twice",
     "0 of 190") came out of Core/page comments in favor of the constraint alone; the history lives
     here and in timeline.md. The ⚠️ constraint comments stay.
   - **884 tests green, 0 warnings** (864 before; +20: ledger relative/dormant semantics,
     CountingAdvice, unit setter + isolation, removal-guard both directions, ConfirmedAt migration,
     Apply/Restore incl. the clamp case). ⚠️ The Recipes/ProductDetail page flows (notice, Undo
     button, unit box, nudge) have no unit tests — no page-test harness exists in this repo — and
     were NOT live-verified this session; the logic beneath them is covered.

29. **v4.2 — the walkthrough pass (2026-07-30): what a full live click-through of v4.0/v4.1 turned up,
   fixed to Jordan's calls.** Every flow was exercised in a real browser against a throwaway demo
   household (real data untouched); the six findings and their resolutions:
   - **The "Ate it" decrement ASKS via a picker now** (Jordan's call on the walkthrough's one design
     finding). Ambiguous mains open a tiny modal — swap-cloud styling, each counted candidate a bubble
     with its live count — pick what came off the shelf, or click away and no count moves (skips are
     said in the notice, not hidden). ⚠️ **A grounded link to a product that exists UNCOUNTED also
     routes to the picker even with one candidate**: live-verified before the fix, the taco recipe's
     tick credited the uncounted store pack while the decrement silently took a freezer package — the
     app guessing WHICH ground beef got cooked. A grounded link naming a product that no longer exists
     stays automatic (stale link — the §13.8 census fall-through). Both directions pinned. Picks land
     in `EatDone.Taken`, so ↩ Undo reverses them with everything else (`MealStock.TakePicked` — same
     ledger, no signal, clock untouched). Fell out of it: candidate ids ride beside the matcher's
     candidates by REFERENCE (`ReferenceEqualityComparer`), so two counted rows sharing a name are two
     picker bubbles told apart by their counts — the duplicate-name defense got better, not just safe.
   - **`.linkish` never existed in app.css** — the class both the purchase pencil and the grocery
     list's "Used one" wore fell back to full blue-button styling ("that giant pencil/blue button").
     Defined now (inline link-styled action); the pencil itself became a plain quantity label + a
     small `icon-btn` ✏️ per Jordan's ask.
   - **Enter didn't submit the Quick update box.** The submit button was disabled while the input was
     blank, and the browser's implicit form submission NO-OPS on a disabled default button — enabling
     it rides a circuit round-trip, so "type, Enter" lost the race and did nothing, silently. The
     button now disables on busy only; `SendChat` already refuses blanks.
   - **The stock-up annotation gates at 1.25×** (`NoteworthyStockUp`, display only — the engine still
     stretches for any above-typical buy): a corrected 2× purchase read fine, but a 1.07× meat-pack
     swing rendered "last buy was ~1× the usual — due date pushed out to match", a story about nothing.
   - **`QuantityFormat.Describe` singularizes exactly-1 plurals** ("1 can", not "1 cans" — visible the
     moment units became editable), naive English trim documented as such ("glass" keeps its name);
     Product Detail's and the Products popover's "Typical buy" now route through `Describe` instead of
     gluing the unit on by hand — one rule, three surfaces.
   - **Transient errors clear on every ProductDetail (re)load** (a stranded empty-box error sat beside
     unrelated content after a purchase save), and a suppressed item's rhythm row is labelled **"Rhythm
     would ask"** with the bare date — "Next buy · 8 days overdue" beside a Stocked chip was the
     contradiction shape one glance-width from its explanation.
   - Walkthrough evidence worth keeping: the beans "Used one" moved 5→4 with the exhaustion date
     (Nov 14→Oct 23) while the attestation stayed Jul 27 — the v4.1 keystone seen working; a purchase
     correction 1→2 cascaded into a live StockUpFactor stretch AND stood suppression down, then
     reverted exactly; dormant stop-counting flipped the cream to Due soon and surfaced the fast-mover
     nudge in the same render; the fixed `query_status` answered through a REAL model call ("you have
     4 cans on hand as of July 27… no action needed"); every logged query carried the household filter.
   - **889 tests green, 0 warnings** (884 before the pass).

30. **The official `/code-review` over the whole branch (2026-07-30) — 9 findings, all fixed; then the
   branch was regated and pushed.** Ten angles run inline (no fan-outs). Everything clustered in the
   newest code — the day-old picker — while the multiply-reviewed older code came back clean:
   - **The picker's close paths are gated on `eatBusy` now.** `ClosePicker` (the backdrop) and
     `DismissEat` could interleave with an in-flight pick's awaits: a backdrop click mid-save filed
     the very question being answered as "skipped" (notice claiming one ingredient both taken and
     left uncounted), and a dismiss mid-save orphaned a SAVED decrement with no notice and no Undo.
   - **A pick that finds nothing to take lands in Skipped** instead of vanishing (the shelf can move
     between the ask and the answer), and `saved` is only set after a real write, so the failure
     messages can't claim a no-op was recorded.
   - **The resolve's second query re-filters** (`TrackQuantity && QuantityOnHand > 0`) and both
     consumers tolerate a dropped row — a product deleted/emptied between the two queries is omitted,
     as pre-picker code did, instead of `byId[id]`/`!.Value` throwing mid-tap.
   - **`MealStock.TakeOne` is THE take** — `Apply` had re-implemented its body; now every decrement
     (auto and picked) is one definition. The all-names scan is paid only when a grounded link
     actually points outside the counted set. The stale "refused, not guessed" docstring now says
     what the file's own header and §13.3 say: asked.
   - `QuantityFormat` singularizes case-insensitively ("1 Can" from "Cans"); the pick-clock test
     seeds a real attestation date (it compared null to null and pinned nothing); a blank Quick
     update send answers with a hint instead of an active-looking button doing nothing in silence.
   - **889 tests green, 0 warnings.**

31. **The test-suite audit & rebuild (7/30–8/1, branch `feature/test-suite-rebuild` — ✅ MERGED via
   PR #1, CI green, smoke-tested).** Jordan's bar: everything covered, useless tests deleted, no test
   ever weakened to pass, page flows TESTED not walkthrough-verified. `docs/test-audit.md` is the
   arc's full record (worklists, verdicts, hunt-list classes, coverage numbers); what belongs HERE:
   - **All 66 pre-existing test files were read and verdicted; zero deletions were earned.** The one
     rewrite was hunt-list class 6 (a test file that never constructs its subject); the audit added
     that class to the list after diagnosing it.
   - **`tests/ShelfAware.Web.UI.Tests` is the page harness** (bUnit 2.8.6, 219 tests): real pages over
     the SAME TestDb/EfPantryStore the persistence suite trusts (shared via InternalsVisibleTo), fakes
     only at the AI/browser seams. `FlakyDbFactory`'s FailAfter/HoldNext knobs model the per-context
     boundary production genuinely has — that's what makes split failure advice and busy-gate
     interleavings honestly testable. ⚠️ Its `RegisterAdditionalServices` hook runs from the BASE ctor
     (before the provider locks); overrides must only touch base members and field-initializer state.
   - **Four product bugs found by the audit/harness, all fixed on the branch:** a relative chat move
     edited a DORMANT count (refused at ledger+store+chat now); ProductDetail's reload-failure advice
     was unreachable and its catch threw NRE (a SWITCH blanks, a same-product REFRESH keeps the view —
     and computes prediction/estimate into locals so no frame shows a fresh count beside a stale
     projection); `ApplyExpirationAsync` had no catch at all; removing the LAST receipt swallowed the
     removal accounting behind the empty state.
   - ⚠️ **"0 Warnings" from an incremental build is vacuous** — MSBuild doesn't re-emit warnings for
     up-to-date targets, so a `dotnet build` right after `dotnet test` reports zero no matter what.
     CI caught five analyzer warnings local checks had "verified" away. Check with
     `dotnet build --no-incremental -c Release`.
   - **Voice-loop tests sequence through the STT fake, not JS handlers** (bUnit REUSES a handler for
     an identical Setup, so "fresh pending handler" tricks silently return stale results): the fake
     capture is sticky, `FakeSpeechToText` queues transcripts, and its exhausted-queue backstop
     answers "stop listening" so loops always wind down.
   - CI runs the bUnit project as a fourth test step. GitHub's Node-20 deprecation notice on
     checkout@v4/setup-dotnet@v4 was the one open CI annotation — **cleared 2026-08-01** (item 35).

32. **The demo seed audited against the whole feature set (2026-08-01, branch `feature/demo-seed-coverage`).**
   The sample pantry is the app's test environment, so a feature with no seeded instance is a feature nobody
   can look at. **Measured, not eyeballed** — a throwaway probe ran the seeded rows through the real engine,
   `ReportDataService` and `MealStock`: 4 of 17 tables empty, 6 of 19 enum values unused. What it found, and
   the rules the fixes carry:
   - ⚠️ **`SignalKind.Restocked` had ZERO instances**, which silently took three behaviours with it: an
     outage cleared by a stock-back rather than a purchase, a due date re-anchored to one, and v3.6's
     "I froze it" override (`ExpirationOverridden` was unreachable — it *needs* a Restocked). One missing
     enum value, three dark features.
   - ⚠️ **The catalog's only dated purchase was in the FUTURE**, so `Expired` never fired and Waste watch
     could only ever return `StillAhead` — its four evidence-reading verdicts had no data and its headline
     list rendered empty. The seeder's own comment claimed it "has something to judge"; it had one row it
     was definitionally unable to judge. **Four past labels** (milk/dog food/spinach/bacon) make all five
     reachable, one per verdict.
   - ⚠️ **"Ate it" took NOTHING for every recipe in the catalog** — no counted product was any main's
     grounded match, so v4.1's flagship flow reported "nothing to take" and that was indistinguishable
     from a bug. `White Rice` is counted now (~30-day rhythm, so it suppresses nothing — it exists purely
     so cooking moves a number). The picker keeps its own case on the tacos.
   - **Nothing was bought in two sizes**, so BOTH branches of the dominant-size rule — the case the whole
     size-is-metadata decision exists for — were unexercised. Orange Juice (a size bought ≥2× drives the
     cadence alone) and Peanut Butter (no size twice → falls back to all purchases) cover them.
   - **`AlternativesJson` and `LastRecipeSuggestions` were empty, and that's a KEYLESS problem**: an
     un-cached swap cloud needs an AI call to open, so the feature was dead for most visitors. Seeding the
     cache is the same move the speech cache already makes to let sample recipes talk without a key.
   - **The sample pantry now sets `TrackExpirationDates=true`** and says so in the load message. It ships
     OFF for a real household deliberately (most ritual-heavy field in the app), but a sample pantry that
     leaves it off renders none of v3.6 and an empty Waste watch. ⚠️ It's a `Config` key, so it survives
     "delete my data" — someone who loads samples, wipes them and goes real keeps it until they turn it off.
   - **A PendingReview receipt ships with the image it was read from** (embedded resource, not wwwroot —
     demo content, not a public asset). The insight that made it worth building: the review grid never
     needed a key, only extracted LINES, so review + confirm now work on a keyless visit. ⚠️ **Its lines are
     that image transcribed** and tests pin enough of them that the two can't drift — a row describing a
     different receipt from the one it can show you is this area's signature failure. ⚠️ It is the ONE
     fixed-date row in a catalog that is otherwise relative to today, because the date is printed on the
     picture; an ageing review date is exactly what an abandoned review looks like.
   - Also seeded: aliases with their teaching receipt, two saved reports (**asserted against
     `ReportSpecRules`** — a saved spec the engine refuses would greet a visitor with an error), `ConfirmedAt`
     + `CreatedByReceiptId` provenance, a second merchant (aliases are keyed per merchant), an untracked
     product, a dormant count, a pre-variety merge candidate, `Category.Other`, and all three
     `PurchaseSource` values.
   - **Deliberately still absent, each for a stated reason:** seeded `AiUsage` (would misreport what a
     household spent), a `Discarded` receipt (every surface filters it out by design, so the row would be
     invisible), and a misread quantity (a demo must not ship known-wrong data to show off its repair tool).
   - **`DemoSeeding` (Web.Tests) is the one way to construct the seeder** in either suite, since seeding now
     writes a real file through real `ReceiptStorage`.
   - Fixed in passing: `PredictionResult` still documented `StockUpFactor` as "capped at 3×" (removed
     2026-07-28 — the predictor's own comment said so), and `Product.CreatedByReceiptId` named the demo
     seeder as a reason the column is null, which this change made false.
   - **1174 tests green, 0 warnings** (1162 before; +12). Live-verified end to end on a throwaway household:
     the expired pin on the dashboard, the label beating the count on the cream, the review grid with every
     line pre-filled and the low-confidence row styled, the audit image byte-exact on disk under its
     household folder, Waste watch showing all five verdicts, both saved reports running, "White Rice — 1
     off, 3 left" with Undo restoring it, and a swap cloud opening with no key configured.

33. **v4.3 — "delete all my data" takes the settings too, and the `Config`/`UserContent` split is gone
   (2026-08-01, branch `feature/delete-resets-settings`).** Jordan's call.
   - **Why the classification stopped being right.** `Config` justified itself as "wiping your pantry
     shouldn't forget how you like receipts confirmed" — which presumes the household CHOSE the
     setting. The demo seeder writing `TrackExpirationDates` (item 32) breaks that presumption: load
     samples, wipe them, and a toggle the app turned on for data that no longer exists survives, and
     it is neither configuration nor pantry-derived content. It's residue, and no third category or
     per-row provenance is worth inventing for it.
   - ⚠️ **The structural reason, which is the stronger one: the split had exactly ONE consumer.**
     Export takes all settings regardless; `CountAll` consulted it only to count what deletion
     removes. With nothing surviving a delete, the classification has no job left and the reflection
     test policing it guards a risk that cannot occur — "a new key silently survives a delete" is
     impossible when nothing does. **Removing a concept beats policing one.** A "reset by default with
     a keep-my-settings opt-out" variant was weighed and REJECTED: it keeps the split load-bearing
     (`LastRecipeSuggestions`/`SelfEvalResults` must go regardless, so "keep my settings" can't mean
     "keep all rows") and puts a conditional in the copy of a destructive flow, to save re-picking four
     toggles that all have defaults.
   - **The delete is the TABLE, not a key list** (`db.AppSettings.ExecuteDeleteAsync`). That's what
     makes the guarantee unfalsifiable by a later key, and it finally clears rows from retired features
     (`ReceiptFolder`) that no list mentioned. Safe because every key has a default when absent — no
     setting is required to exist. Pinned by a test that seeds an undeclared key alongside the real
     ones. **Export is untouched** (asking for your data still returns everything), and **`AiUsage`
     still survives a delete** so a wipe can't double as a quota reset — that asymmetry is now the only
     one, and it's stated on both the service and the export test.
   - ⚠️ **The Settings page re-reads itself after the wipe.** Not in the brief; found by asking what the
     screen says once the rows are gone. It loaded its three controls once in `OnInitializedAsync`, so
     a delete left them offering the old Import mode, recipe-add preference and expiration toggle — a
     screen stating what the database no longer holds, one scroll above a message claiming the
     opposite ("one prediction, one story", applied to settings). `LoadSettingsStateAsync` is the one
     reader now, called on init and after the delete; failure advice splits by which half failed,
     because a delete that went through must not be reported as one that didn't.
   - ⚠️ **`PageTestContext` was faking the settings store, which is why the reset was untestable.** It
     registered `FakeAppSettings`, an in-memory dictionary — but settings are DATA, not one of the
     AI/browser seams that harness fakes (item 31's own bar), and a dictionary cannot observe a change
     the product makes to the table. The first reset test performed the delete, rendered the success
     message, and still showed the old radio, because the page re-read the fake. It uses the real
     `EfAppSettings` over the same TestDb now. **The same class of trap as item 20's `set_quantity`
     fake**: a stand-in more permissive than the real store makes its tests pass vacuously.
   - Fell out of that swap: **`SetAsync(key, null)` writes an EMPTY value, it does not remove the
     row** — so two "cleared" assertions had been pinning `null`, a state the real store never
     produces. Both pin `IsNullOrEmpty` now, which is exactly what `RestoreSuggestionsAsync` treats as
     "nothing saved" (no product bug — the page guards correctly). One of the two sat inside
     `WaitForAssertion(async …)`, whose lambda parameter is synchronous: it ran unobserved and had been
     pinning nothing at all. That `async` lambda shape appeared seven more times in the UI suite and
     was deliberately left for a pass of its own rather than drive-by patched — **swept in item 34**.
   - **`CountAllAsync` had no production caller** (only tests) while its docstring claimed the confirm
     dialog shows "this removes 214 records" — it didn't. Jordan's call: make the docstring true rather
     than delete it. The confirm now counts when it opens and says "N records will be removed", which
     this change makes more useful, not less — the settings rows are IN that number, and a warning that
     under-reported by the rows nobody thinks of would be the wrong kind of reassuring. **A failed count
     omits the sentence rather than guessing**: no number beats a wrong one on a destructive flow, and
     failing to count must never stand between someone and deleting their own data.
   - **1162 tests green, 0 warnings** on a non-incremental Release build (1163 before: −3 for the
     removed `SettingKeysTests`, +2 page tests).

34. **v4.4 — the seven waits that could never fail (2026-08-01, branch `fix/waitforassertion-sweep`).**
   The pass item 33 deferred. ⚠️ **bUnit's `WaitForAssertion` takes an `Action`, so an async lambda
   binds as `async void`** — the helper calls it, the lambda returns at its first `await`, the helper
   observes no exception, and the wait passes having pinned nothing. Not flakiness: a test that cannot
   fail. Four sites in `SettingsPageTests` (import mode ×2, expiration toggle, recipe-add), two in
   `ReceiptsPageTests` (`VerifiedForEval` both directions), one in `ProductsPageTests` where the wait
   held the test's **only** assertion, so `The_tracking_checkbox_writes_through` verified nothing about
   the write it is named for (its `Find` calls still proved the row renders).
   - **The fix is bUnit's own async API, verified rather than assumed:** 2.8.6 ships
     `WaitForAssertionAsync` with a `Func<Task>` overload (checked in the package's API docs), so each
     site is `await cut.WaitForAssertionAsync(async () => …)` — awaited on the renderer's dispatcher
     and retried per render. ⚠️ It also has an `Action` overload, so the fix had a live failure mode
     of its own: bind wrong and seven tests go on passing vacuously in a new way.
   - ⚠️ **Green cannot validate this fix, because green is what the defect produces.** Each site was
     proven to observe by breaking its expected value and confirming the failure. Five methods failed
     on the first pass; the two that hold a SECOND wait were re-run with only that second wait broken,
     since the first failure masks it. Do this for any future change to a wait's shape — the suite
     passing is not evidence.
   - **No product bug surfaced and no expectation was wrong** — every one was read against the page's
     own write path before being touched, and all seven matched. Worth stating plainly because item 33
     found this class the opposite way (a real bug behind a fake store); a sweep that finds nothing is
     a result, not a wasted pass.
   - **`WaitForState(async …)` is a non-issue in C#** (the brief expected it to be a second hazard):
     its parameter is `Func<bool>`, which an async lambda has no conversion to, so the shape doesn't
     compile. Every other async lambda in the repo already binds to a Task-returning delegate —
     `ThrowsAsync`, the `Func<Task>` `VoiceCoordinator` events, one `Select` projection.
   - The constraint lives on `PageTestContext` beside the suite's other bUnit gotcha: an assertion that
     must `await` goes in the Async overload; pure DOM/markup checks stay synchronous.
   - **1174 tests green, 0 warnings** on a non-incremental Release build — unchanged, as a repair of
     existing tests should be.

35. **CI actions off Node 20 (2026-08-01).** `actions/checkout@v4` → **v7**, `actions/setup-dotnet@v4`
   → **v6**, clearing the runner's Node-20 deprecation annotation. Item 31 said "bump to v5", which
   was already stale when read: v5 is merely the FIRST major on `node24` for both, not the current
   one. Checked against the published `action.yml` per tag rather than trusting the note.
   - The one release marked breaking is **`setup-dotnet@v5`** — Node 24 (needs runner ≥ v2.327.1,
     which GitHub-hosted `ubuntu-latest` long since passed) and it drops older .NET SDKs. We pin
     `10.0.x`, so neither bites. `checkout@v7` blocks fork-PR checkout under `pull_request_target` /
     `workflow_run`; this workflow triggers on `push` + `pull_request`, so it can't apply.
   - Majors stay pinned bare (`@v7`, not a SHA) — same posture the file already had; tightening to
     digests is a separate decision, not a side effect of a deprecation fix.
   - ⚠️ **Nothing about a workflow change can be verified locally, and a BRANCH push proves nothing
     either**: CI triggers on `push: [master]` and `pull_request: [master]`, so pushing a topic branch
     runs zero jobs (found by doing it — the v4.4 branch pushed green-looking and had in fact run
     nothing). The only proof is the run on master, which is why this landed as its own revertible
     commit rather than riding along with unrelated work.

36. **v4.5 — the guided walkthrough (2026-08-01, branch `feature/guided-tour`).** A new visitor — above all
   one who just loaded the sample pantry — got a catalog full of unfamiliar data and ten nav links with
   nothing saying what any of them were for. `GuidedTour` walks them through eleven surfaces in turn and
   closes for good the moment they've had enough. Jordan's calls: docked coach panel over a modal
   spotlight, all core features over a short happy path, offered to any new user but auto-started only
   after seeding.
   - **`TourScript` (Core) is the step list AND the movement rules**, in Core for the `MealStock`/
     `SpendForecast` reason — logic private to a page is logic no test can reach. Clamping is what a
     stored position needs: it's read back from the visitor's own browser, where a *shorter tour shipping
     later* leaves a real person parked past the end.
   - ⚠️ **The copy is deliberately data-INDEPENDENT.** The tour is offered to a real household as well as
     a demo one, so a step naming a seeded row ("see the overdue roast at the top") is a screen stating
     something the engine never produced the moment it runs against real receipts — the "one prediction,
     one story" failure, pre-empted. A test asserts no step names a hero from the demo catalog.
   - ⚠️ **…and DEPLOYMENT-independent, which the first version missed** (found by Jordan running the tour
     on the tailnet box, 2026-08-01). The last step pitched "bring your own API key" unconditionally,
     which is false on a **managed** deployment — the host's keys are authoritative, the browser can't
     override them, and the Settings key panel is HIDDEN. So the walkthrough's final act was to name a
     control the visitor cannot see and an act they cannot take. Same class as the data rule, one level
     up: independence from the household's DATA is only half of it. `TourStep.WhenManaged` carries the
     alternative wording and `TitleFor`/`BodyFor` pick it from `CircuitAiSettings.Managed`; a Core test
     asserts no step's managed body contains key-custody phrasing. ⚠️ That test names only phrases that
     can ONLY mean key custody — a bare "your own" false-positived on Reports' "build your own report",
     and a rule that cries wolf is one someone later loosens.
   - The BYOK wording changed too: it opened "Shelf Aware runs on your own API key", which reads as a
     requirement immediately after ten steps of a working app that needed no key. It now leads with
     "everything you've just seen works without an API key" and describes what a key ADDS.
   - **It lives in `MainLayout`, not a page**, for a sharper version of VoiceAgent's reason: it NAVIGATES
     between its own steps, so a page-hosted tour destroys itself on the first Next. It also SKIPS the
     navigation when two consecutive steps share a page (steps 1–2 are both the dashboard) rather than
     tearing the page down to land where it already is. `TourCoordinator` is the bus, same shape as
     `VoiceCoordinator`, so the banner and Settings can start something hosted above them.
   - ⚠️ **Progress is per BROWSER (localStorage), not per household.** One member finishing the tour must
     not silently retire it for the other, and it isn't pantry data — AppSettings would also mean a
     "delete my data" wipe (item 33) resets the tour, which is unrelated to either act.
   - **The ring is best-effort and must degrade to nothing.** The anchor usually isn't in the DOM when the
     tour arrives (pages load their data asynchronously before rendering anything to ring), so `tour.js`
     polls ~3s rather than coupling the tour to any page's lifecycle, and gives up quietly. `.page-head, h1`
     resolves to "this page's title block" everywhere; the one hand-written selector (`[data-tour=ai-keys]`)
     is pinned by a test that renders Settings, because a selector that stopped matching would fail
     SILENTLY and forever.
   - ⚠️ **A ring's visibility must not depend on its animation running.** The first version animated
     `outline-color` from `transparent`; a stalled animation holds its first keyframe, so the ring was an
     invisible outline exactly when it was needed. Probed live: `outline-color rgba(0,0,0,0)` with
     `playState "running"` and `currentTime` pinned at 0. The animation now moves only the OFFSET, so
     every frame is a visible ring. **Found by running it — no test would have.**
   - ⚠️ **`GoToAsync` requests `StateHasChanged` itself.** Two of its three callers get no render for free:
     the coordinator raises it from another component's event, and the resume runs in `OnAfterRenderAsync`,
     which — unlike `OnInitializedAsync` — does not re-render on completion. Without it a returning
     visitor's walkthrough stayed invisible. Caught by the resume tests.
   - ⚠️ **Assert navigation on `BunitNavigationManager.History`, never `Uri`.** Re-navigating to the page
     you are already on leaves the URI identical, so a Uri comparison cannot tell "didn't navigate" from
     "navigated to the same place" — and it is the navigating that does the damage. The Uri-based test
     survived deliberately removing the guard it was written to protect; the History-based one kills it.
     **Every new test here was mutation-checked** (item 34's rule: green is what the defect produces).
   - **The `/pre-push` gate found five, all fixed** (2026-08-01; security review found nothing — the
     diff adds no DbContext, `IgnoreQueryFilters`, endpoint, settings key or disk write, and
     `TourCoordinator` is scoped so a start event can't cross circuits):
     - ⚠️ **`JSDisconnectedException` is NOT a `JSException`** — it derives straight from `Exception`, so
       catching `JSException` alone lets it escape. That is why the repo already has 16 explicit clauses
       for it, and why `AiSettingsLoader` catches it AND `Exception` (a dead clause otherwise). Sharper
       here than in the page-local readers the pattern was copied from: this component is in
       **MainLayout**, so an in-flight save during a circuit teardown throws on every page in the app.
     - ⚠️ **Fixed-position screen furniture must be added to the `@media print` hide list.** `.tour-panel`
       wasn't, and the walkthrough deliberately VISITS the two pages built to be printed (Reports, the
       grocery list) — it would have landed in the printout of a page it had just invited you to print.
       `.voice-agent`'s comment right above it already stated the rule.
     - **`aria-hidden` on the step counter** left screen-reader users the only ones who couldn't tell how
       far in they were or how much was left — the fact you need to decide whether to skip. It reaches
       them through the focused heading's `aria-describedby` now rather than as loose text.
     - **z-index dropped 70 → 55, below the modal layer** (`.picker-backdrop` is 60): a walkthrough is
       transient furniture, and floating it over the "Ate it" picker left that modal not modal.
     - **Escape closes it**, the gesture expected of dismissible overlay furniture (the ✕ was already
       tab-reachable, so this was a gap rather than a blocker). Mutation-checked: `"Esc"` instead of
       `"Escape"` fails the Escape test and correctly leaves the other-key test passing.
   - **1210 tests green, 0 warnings** (1174 before; +36). Live-verified end to end: all eleven steps
     navigate and find their anchor, the panel docks clear of the voice assistant (measured, no overlap),
     a reload resumes mid-tour, Done persists across a reload, Settings replays from step 1, and on mobile
     it becomes a full-width sheet with the voice FAB standing down. No console or server errors, so the
     strict CSP holds.

37. **v4.6 — the shelf-photo census (2026-08-02, branch `feature/shelf-census`).** §13.8, the last unbuilt
   phase of the counting arc: photograph a shelf, freezer or cupboard and the app lists what it can see with
   how many of each; correct it and it becomes an attested count. Jordan's ask, and its ruling constraint,
   was epistemic — *"best guess what's there… don't make stuff up, but if a can says what it is then good."*
   - ⚠️ **`CensusEvidence` is that constraint made structural, and it is the whole design.** A receipt is
     text — `raw_text` is either there or it isn't — so extraction never has to say how it knows. A photo
     has no such floor: a freezer LOOKS like a freezer, and a model asked "what's in here?" can produce a
     plausible pantry out of priors alone with every word invented, indistinguishable from having read the
     labels. So every item says how it was known: `Label` (printed text, kept verbatim in `LabelText` — the
     census's `raw_text`, checkable against the photo in a second), `Appearance` (no legible label, but a
     bunch of bananas needs no barcode), `Unidentified` (a package is there and it can't say what — the NAME
     then describes the package). One enum, three answers, rather than blending "I read it" and "I reckon"
     into one confidence number.
   - ⚠️ **Three of the contract's rules are enforced in the PARSE, not trusted to the prompt**, because a
     shelf photo's output can't be checked against anything the way a receipt's can: a `Label` claim with no
     readable text is downgraded to `Appearance` (nothing to check = not a label claim); an `Unidentified`
     item's confidence is capped below the grid's tick threshold and may never carry a product match; and
     `visible_count` floors at 1 — reporting an item means something was SEEN, and a zero reaching the grid
     could be confirmed into an **attested** zero, which mints a real `OutNow` into the cadence engine
     (§13.4). A machine's arithmetic must never mint one; a human typing 0 in the grid still can.
   - **Ticked at ≥ 0.6, the SAME number the receipt grid highlights a low-confidence line at** — deliberately
     not a second threshold that could drift from it. A guess is opted into; a legible label isn't punished.
     ⚠️ **Confidence is necessary, not sufficient** (added by the post-merge review, item 38): a tick
     authorizes a WRITE, so it also requires the row not be `Unidentified` (held on the PAGE, not left
     implied by the reader's 0.3 cap in another assembly) and not be a name-SIMILARITY match (confidence is
     certainty in the ITEM and says nothing about which product `ProductMatcher` then picked).
   - **`CensusConfirmationService` is the census's own confirm path** (§13.8's ⚠️), and the ★ rule is the
     reason: it writes products + `StockLedger.Attest` and **never a `PurchaseEvent`**. Three calls the spec
     didn't settle, all now in DESIGN.md: rows are **summed per product** before one `Attest` (an
     attestation states a TOTAL, so row-by-row would let the second silently overwrite the first); a
     negative count is **refused, not clamped** (clamping lands on an asserted out and files an `OutNow` off
     a typo); and an unmatched row whose name already exists **resolves to that product** — the duplicate
     guard where it matters most, and what lets the failure message honestly promise that pressing Confirm
     again is safe. (Item 38 added three more refusals and reworked the zero rule — see below.)
   - **Nothing is persisted but the counts** (Jordan's call). No audit copy, no census table, nothing new
     for export or "delete my data" to reach — so a photograph of the inside of someone's home never lands
     on disk and the feature adds no tenant table to get wrong. Costs the receipt path's Retry and an
     abandoned review; both are cheap when you're standing at the shelf.
   - ⚠️ **`IShelfPhotoLoader` exists because `RequestImageFileAsync` cannot run under bUnit at all** —
     probed, not assumed: it throws outright, so without a seam the entire review grid, its tick defaults,
     its pre-fill and its confirm would be hand-verifiable only. It's a browser seam, which is exactly what
     `PageTestContext` fakes by policy. Worth knowing for the receipt Upload page, whose image path has the
     same untestable shape today (it tests via PDFs, which skip the resize).
   - ⚠️ **A live probe against the real API found a prompt gap that wasn't one.** Synthetic shelves (labelled
     cans, a labelled box, unlabelled tubs, an EMPTY shelf, a non-shelf) went through the real reader. The
     anti-invention rules held first time and the empty shelf is the one that matters — a shelf that *implies*
     a pantry returned nothing. But the unlabelled tubs were dropped, so I strengthened three prompt rules;
     they were still dropped; raising the tubs' CONTRAST fixed it. **Isolating it — original prompt, high
     contrast — showed the tubs reported fine, so contrast was the whole story and the prompt edits had
     addressed a failure mode with no evidence behind it.** The counterweight paragraph I'd added to rule 2
     was reverted (rule 2 carries the primary "don't invent" instruction and must not be diluted by an
     unvalidated hedge); rule 3's "you must still return a row" and rule 10's clutter carve-out were KEPT,
     on wording grounds that stand on their own — rule 3 read as permissive ("is useful") where an
     instruction was meant, and rule 10's "skip clutter / report food and household consumables" genuinely
     left an unnamed container in neither category. **The lesson is the old one: change one thing.**
   - ⚠️ **Every new page test was mutation-checked** (item 34's rule), and one was found vacuous by it:
     `An_unidentified_package_is_never_pre_matched_to_a_product` passed with its guard removed, because the
     two names I'd picked didn't fuzzy-match anyway. It now seeds "Freezer Bag" against a read of "frosted
     freezer bag" — a collision `ProductMatcher` really makes, asserted in the test itself — so four frosted
     parcels can't pre-fill the household's box of freezer bags. Both that guard and the tick default fail
     when mutated.
   - **The `/pre-push` gate found eight, two of them able to destroy or invent data (2026-08-02).** Security
     review came back clean and said what it checked: both new DB call sites go through `IHouseholdDbFactory`,
     no new `IgnoreQueryFilters`, no new settings key / endpoint / per-household disk write, `[Authorize]`
     cascades and isn't shadowed, and a foreign `ProductId` from a tampered circuit message fails to resolve
     against the already-filtered list rather than reaching across. All three top code findings were
     REPRODUCED with probes before being accepted:
     - ⚠️ **An explicit "➕ create new product" silently overwrote the product it collided with.** `ProductId`
       0 meant both "the grid never matched it" and "the human chose create-new", and the exact-name fallback
       couldn't tell them apart: a household's `Ground Beef` counted at **12** became **4**, no new product,
       summary silent — and the grid had just *removed* the "Was 12" note, so the screen stated the opposite
       of what the write did. `CensusRow.CreateNew` now carries the intent, an explicit create-new whose name
       is taken is refused and named, and the row warns before the confirm. **This is the "one prediction,
       one story" class in code written by the same session that cited the rule.**
     - ⚠️ **A zero on an unmatched row invented a product and pinned it Overdue forever.** Probed:
       `'Frozen Peas' onHand=0 signals=[OutNow] purchases=0 → Status=Overdue Pinned=True`. The row arrives
       ticked and typing 0 is what "fix the numbers" invites, so an item the household has never owned went
       to the top of the dashboard AND the grocery list permanently. Refused now when the row would CREATE
       the product; a zero on an existing product is still §13.4's real evidence.
     - The button counted ROWS and the result counted PRODUCTS with nothing explaining the gap — which the
       reader's own contract makes routine (a row per variety, matched across varieties), and which read as
       a dropped row beside a refusals clause that *does* explain itself. `CensusOutcome.Rows` reports it.
     - Smaller: a blank name refused a row that named its product by id (the name is only needed to resolve
       BY name); "every **1 days**", the third outing of that bug here; a comment on `MaxUploadBytes` that
       claimed a guarantee it does not provide (it bounds the *resized* stream); and a missing
       `JSDisconnectedException` clause, so closing the tab mid-read logged an error.
     - ⚠️ **Refusals are NAMED, not tallied** (`RefusedRow` + `CensusRefusal`), because the three reasons need
       genuinely different sentences — a typo, a name clash the household can resolve, and a claim the app
       declines to make for them. A row someone ticked and then didn't get is the one outcome they cannot
       discover for themselves; every other one is visible on the product's own page.
     - **Test gaps the same review found, all closed:** `A_read_that_names_an_existing_product_pre_selects_it`
       was a **mutation survivor** — its suggestion named a product `ProductMatcher` would have found anyway,
       so deleting the entire suggestion branch left 21/21 green. It now makes the reader and the matcher
       *disagree* and asserts the reader wins. Also: the "Was N" note couldn't tell right-product from
       any-product (two products now, and the test changes the dropdown and watches the note follow); a
       two-rows-one-product test never asserted the sum; the unidentified confidence `Math.Min` was untested
       (a flat assignment survived); and the ✕ button, zero rows, and every summary sentence but one had no
       coverage. **Every new test was mutation-checked in three batches** — 11 mutations, each failing
       exactly the tests it should and nothing else.
   - **1285 tests green, 0 warnings** on a non-incremental Release build (1210 before; +75).
     No schema change — `TrackQuantity`/`QuantityOnHand`/`QuantityCountedAt`
     already exist, and a census writes nothing else. No new demo seed either: the census's OUTPUT (a counted
     product with no purchases) is already seeded as `Quarter Cow Ground Beef` (item 25) — a census is an act,
     not a state.
   - **Live-verified end to end** (2026-08-02, dev server on the alt port against the sample catalog; the
     photo was drawn on a canvas and handed to the file input, the documented no-real-files technique). One
     synthetic shelf produced exactly three rows: `Canned Black Beans` ×3 read off the label at 95%, ticked,
     **matched to the existing seeded product** and annotated "Was 5, counted Jul 29"; `Tilapia Fillets` ×1
     at 95%, ticked, create-new; and `lidded plastic tub` ×2 as **Unidentified at 20%, unticked,
     `low-confidence`, unmatched**. Confirming said "Counted 2 items (1 new product)", and the two product
     pages then proved the ★ rule: the beans read "3 on hand · you last counted Aug 2" with **the same four
     purchases as before** and the suppression sentence beside a `Stocked` chip, and the new Tilapia product
     read "1 on hand" with **"Last bought: never"** and no purchases at all. No console or server errors, so
     the strict CSP holds.
   - **Two findings the run and a re-read produced, both fixed:**
     - ⚠️ **`Read()` had no busy guard.** Setting the phase hides the button, but a second click can already
       be queued before that render reaches the browser — and on a slow circuit it will be — so BOTH would
       run: **two vision calls billed to the visitor's own key for one press**, the second overwriting the
       first's rows. The same class the dashboard's quick buttons were guarded for in item 8. Pinned by a
       test that parks the reader mid-flight, mutation-checked.
     - **A two-word nav label wrapped INSIDE itself.** With 11 links the row wraps, and "Count Stock" split
       across two lines — both halves highlighted when active — reading as two separate links. Measured
       (`getClientRects().length === 2`), not eyeballed. `white-space: nowrap` on `.site-header nav a` fixes
       it and closes the same latent hole for "Grocery List", which had simply never landed on the break.
       ⚠️ **A CSS edit needs a server RESTART to show up** — static assets are fingerprinted
       (`app.mq67gixxrj.css`), so the running server keeps serving the old file and a reload proves nothing.

38. **A second `/code-review` over the finished census branch (2026-08-02, same branch) — 15 findings, all
   fixed.** Ten finder angles plus a gap sweep, every finding independently verified (two by throwaway
   probes: a bUnit harness for the page flows, a console probe against the real `ReplenishmentPredictor`).
   The `/pre-push` gate in item 37 had already swept this code; this pass found a further fifteen, which is
   the honest measure of how much a second reviewer is worth on a branch this size. The ones carrying a rule:
   - ⚠️ **An EMPTY count box bound to `0` and filed a real `OutNow`** — the sharpest defect in the arc, and
     a rule this repo had already written down and then not applied. `@bind` on a non-nullable `decimal`
     converts `""` to `default`, so clearing the "how many" box to retype it and then clicking Confirm
     (the blur's `change` beats the click, so the 0 is never even seen) attested an outage against a
     freezer full of the stuff. `ReceiptConfirmationService` defends the identical control
     (`line.Quantity > 0 ? line.Quantity : 1m`) and ProductDetail's `editQuantity` is nullable for exactly
     this reason — item 20 states it as "An EMPTY count box is not a zero". `CensusRow.Count` is
     `decimal?` now and null is REFUSED, never coerced.
   - ⚠️ **The zero-refusal was drawn at "the product is NEW" when the harm tracks "it has no PURCHASES".**
     Probed: `onHand=0, OutNow, zero purchases → Status=Overdue Pinned=True`, unchanged two years later,
     and **a later census counting it at 3 does not lift it** (no purchases → `lastStockBack` null → every
     OutNow stays active). So the second census of a shelf hit precisely the state the first was refused
     for — because **a census's own output has no purchases by construction**, which is the entire point
     of §13.8. A guard keyed on the wrong property looked equivalent for one release and was reachable by
     the feature's own happy path.
   - ⚠️ **A census silently re-armed and destroyed a DORMANT count.** `ProductNote.Counted` required
     `TrackQuantity`, so a stopped count rendered no "Was N" note and was indistinguishable from
     never-counted; `Attest` then overwrote the number and date item 28 promises to keep, and the summary
     stayed silent because `Retracked` counts `IsTracked` — a different property. Every other mutator
     respects dormancy (`AdjustByHuman` and `Move` both return early). The grid shows the stored number now
     and `CensusOutcome.ResumedCounting` names the act. The demo catalog already ships an instance
     (`Cat Litter`), and the page harness could not even express dormancy, so nothing could have caught it.
   - ⚠️ **A tick authorizes a WRITE, so confidence alone must not grant one.** Two conditions were missing.
     `ProductMatcher`'s bidirectional substring rule matches "Peanut Butter" to a catalog's `Butter`, and
     `Attest` REPLACES that product's count with no undo — a flawless 0.95 read of the ITEM pre-authorizing
     an unscored guess at the TARGET. And `Unidentified ⇒ unticked` was left implied by the reader's 0.3
     cap in another assembly; the page holds it itself now.
   - ⚠️ **`Enum.TryParse` SUCCEEDS on a numeric string.** `"evidence": "3"` produced an undefined value that
     was neither `Label` nor `Unidentified`, slipping past BOTH honesty rules the parse exists to enforce
     while the grid's switches fell to their "couldn't tell" arms — a row reading "couldn't tell, 95% sure",
     ticked and pre-matched. `Category` was the same and **persisted** onto a real Product. The repo already
     knew this (`ReportSpecUrl` pairs it with `IsDefined` under a test named
     `Enum_parsing_is_case_insensitive_but_refuses_numeric_smuggling`); fixed as a SET, including the three
     pre-existing sites in `AnthropicReceiptExtractor` and `AnthropicPantryChat`.
   - ⚠️ **`catch (OperationCanceledException) { throw; }` produced a silent, permanent spinner.**
     `ComponentBase` IGNORES a canceled task, so the rethrow yielded no error, no log line and no final
     render — and the reachable trigger is the vision call's own `TaskCanceledException` on an HttpClient
     timeout. The page owns a `CancellationTokenSource` now and threads it, so "the visitor left" and "the
     call timed out" are distinguishable: only our own token is teardown. **The house convention
     ("let cancellation propagate") is right where a token means what it says and wrong where nothing
     supplies one.**
   - **Two screens stating what the engine didn't do**, the class this arc keeps producing: "✅ Counted 0
     items" when every ticked row was refused, and "Nothing recognisable turned up in that photo" after the
     human ✕'d every row of a read that worked fine.
   - Also: an unresolvable `ProductId` fell through to create-new/by-name silently (a merge in another tab
     is enough) — refused as `ProductGone` now, and **the tenancy test got stronger for it**: it asserted
     the row lands on household B's own same-named product, which is a second wrong answer, and now asserts
     the refusal; the census created products behind an exact-name check only, where every other creation
     path resolves fuzzily first (the grid names a near-miss now — resolving one in the service would
     attach a count to a guessed product); `ShelfPhotoLoader` handed undecodable files to a JS promise that
     never settles (Blazor's `toImageFile` revokes the object URL on error and never rejects), so a PDF or a
     corrupt JPEG bought 30 seconds of spinner and a message that never named the file; and the fast-mover
     nudge had dropped ProductDetail's `!TrackQuantity` gate, so it argued against a count the household
     already keeps, on the act that refreshes it.
   - **The prompt's one worked example contradicted its own arithmetic** — "FIVE items in three rows" then
     enumerating two — inside the `Unidentified` rule, the one the parse can least check.
   - ⚠️ **A test that asserted what its own fixture guaranteed.** `An_unidentified_package_is_shown_unticked`
     seeded confidence 0.2, so it re-tested the threshold and would survive deleting every `Unidentified`
     branch on the page. That it survived the earlier gate is the point: item 34's rule is that **green is
     what the defect produces**, so a test is not evidence until it has been made to fail.
   - **All 17 mutations killed exactly their tests and nothing else**, run in four batches. Both halves of
     each new rule are pinned (the guard AND its complement), so no fix can quietly become a wall.
   - **1316 tests green, 0 warnings** on a non-incremental Release build (1285 before; +31).
   - **Live-verified end to end** (2026-08-02, dev server on the alt port, throwaway `test@shelfaware.net`
     household on the sample catalog; three real vision calls on synthetic canvas-drawn shelves). What the
     run proved, in the order it was checked:
     - An unidentified tub came back at 20% and arrived **unticked** with "couldn't tell"; the labelled cans
       came back at 95% **matched via the model's own `existing_product`** — so ticked with no similarity
       warning, which is the trust order working rather than the fuzzy path failing to fire.
     - Clearing the "how many" box rendered **"Enter how many."** inline, and confirming refused the row:
       *"the 'how many' box was empty. An empty box isn't a zero."*
     - A zero on a would-be-new product refused with the no-rhythm sentence, and the panel led with
       **"Nothing was counted."** and **no ✅** — verified `innerHTML` carries no tick glyph.
     - **Nothing was written by that refused confirm**: no `Diced Tomatoes` twin on `/products`, and
       `Canned Black Beans` still read "You have 3, counted Aug 2". Pre-fix, the same click would have
       zeroed the beans, filed an `OutNow`, and left a permanently-pinned ghost product.
     - Typing "Diced Tomatoes" on a create-new row raised **"Looks like your existing 'Canned Diced
       Tomatoes'"** — the twin that would otherwise have been created silently.
     - `Cat Litter` (the dormant hero) showed **"Was 2, counted Jul 2. You'd stopped counting it — this
       starts again from your new number."** on a genuinely-read match; pre-fix that row showed *nothing*.
       Confirming said **"✅ Counted 1 item. Started counting 1 item you'd stopped counting."**, the product
       page then read "You have 4, counted Aug 2", and its purchase count stayed **×4** — the ★ rule holding.
     - Deleting every row said **"You've removed everything that was found… The photo was read fine"**
       rather than blaming the photo.
     - Zero console errors and zero server errors across the whole run, so the strict CSP holds.
     ⚠️ **The fuzzy-tick rule was NOT exercised live** — the model suggested a product every time, which is
     the trusted path, so `ProductMatcher`'s substring fallback never drove a pre-fill. It is unit-pinned
     and mutation-checked; a live case needs a shelf item absent from the catalog whose name is a substring
     of one that is present.
     Side effect on the throwaway household, stated so it isn't mistaken for seed data later: `Cat Litter`
     is no longer dormant — it is counted 4 as of 2026-08-02.

39. **The `/pre-push` gate over the finished branch (2026-08-02) — seven more, five of them regressions
   introduced by item 38's own fix commit.** Security review CLEAN and said what it checked (both new DB
   call sites through `IHouseholdDbFactory`; a tampered `ProductId` refused rather than resolved, and the
   refusal doesn't leak existence — "belongs to household B" and "never existed" are the same branch, the
   same sentence, and the name echoed back is the caller's own string; `CensusRow.CreateNew` is
   server-authoritative; the photo verified never to touch disk). The code half is the lesson:
   - ⚠️ **A fix pass needs its own review, and this is the evidence.** Item 38 fixed 15 findings and
     introduced 5 — including two that the first gate's class of bug would have called serious. Reviewing
     the branch as a whole would have missed them all: the older code came back clean, and every new
     defect was in the 988 lines that had never been read by anyone but their author.
   - ⚠️ **A guard must not be narrower than the thing it guards.** The content-type ALLOWLIST added for
     the 30-second-hang fix refused real photos: Blazor's `toImageFile` never inspects MIME — it paints
     into an `<img>` and re-encodes through a canvas — so it decodes whatever the BROWSER can, including
     HEIC on WebKit, AVIF, BMP, TIFF. iOS transcodes HEIC→JPEG only when the accept list asks for it, so
     the Photos path was safe and the **Files-app path was broken**. Now a `image/` prefix test, with an
     empty content type let through (the OS supplies none for an extensionless file) and `accept="image/*"`.
     ⚠️ **Never name `image/heic` in accept** — Safari 17+ then stops transcoding and converts JPEG and PNG
     INTO HEIC, making the rare case universal.
   - ⚠️ **`CancellationTokenSource.Token` THROWS after `Dispose()`** (unlike `IsCancellationRequested`,
     which stays safe), and `ObjectDisposedException` derives from `InvalidOperationException`, so it
     cleared every specific catch clause and logged an ERROR on an ordinary navigate-away — the exact
     teardown noise the neighbouring `JSDisconnectedException` clause exists to prevent. Capture the token
     ONCE before the first await; a captured token survives its source's disposal.
   - ⚠️ **A read may be cancelled when the visitor leaves; a WRITE may not.** Threading the page token
     into the confirm discarded a census the household had already pressed Confirm on — no row, no
     message (the component is gone), no log line — and a census keeps no audit copy and has no Retry, so
     recovery meant re-photographing and paying for the vision call again. The read keeps the token; the
     confirm takes `CancellationToken.None`. **Pinned on the TOKEN (`CanBeCanceled`), not by racing a
     detached write** — the first version of that test polled the DB after disposing the component and was
     flaky 1 run in 5, which is the honest cost of testing a timing-dependent thing by its timing.
   - ⚠️ **Don't re-derive from strings what a collaborator already computed.** The page decided "exact vs
     fuzzy" by comparing raw names while `ProductMatcher` had matched on the NORMALIZED form (punctuation
     folded to spaces), so a rule-1 identity was rendered as a guess: the seeded `Home-Canned Tomato Sauce`
     read as "Home Canned Tomato Sauce" arrived unticked saying "not read off the package" one cell from a
     chip reading "read the label". `ProductMatcher.ResolveWithKind` reports which rule fired now.
   - **The zero rule, third time.** See DESIGN.md §13.8 — the decision moved off the row, and only a zero
     that would CREATE a product is still refused. (This round also added a `ZeroedWithoutSignal` outcome
     for a withheld signal; both it and the rule behind it were reverted in item 40 — the premise was
     false. Don't go looking for the member.)
   - ⚠️ **"Both halves pinned" was itself unpinned.** Item 38's commit message claimed every new rule had
     both directions covered; deleting `!product.TrackQuantity` from the resumed-counting rule left the
     whole suite green, so every ordinary recount would have announced "Started counting 1 item you'd
     stopped counting". The existing complement test was satisfied by the OTHER conjunct alone (a
     never-counted product has no stored number). **A complement that shares a conjunct with the case it
     complements pins nothing.** Both places now have an already-counted test.
   - **`CLAUDE.md`'s build-state header said 1285 while item 38 said 1316** — the header was edited from
     1210 and never re-read off the final run, contradicting itself 1,365 lines later. Item 21's rule,
     broken in the file that states it. And it happened again in this very pass: 1327 was written into the
     header before the last test was added, and corrected to **1328** only by counting the final run.
   - **1328 tests green, 0 warnings** on a non-incremental Release build (1316 before; +12). Nine
     mutations, each killing exactly its tests. ⚠️ Not re-verified in a browser since these changes — the
     HEIC path in particular can only be settled on a real iOS device, and the fix is reasoned from
     Blazor's `InputFile.ts` plus WebKit's documented transcode behaviour, not observed.

40. **The re-gate, and the end of the patch cycle (2026-08-02).** The gate over item 39's own fix commit
   found seven more — five of them regressions that commit had introduced. Three rounds in a row had now
   done that (8 fixes → 15 found → 7 found → 7 found), so the pattern itself became the finding: **a fix
   pass needs its own review, and patching a rule at one call site keeps producing the next round's
   defects.** Jordan's call was to stop patching and fix the altitude. What that meant:
   - ⚠️ **The zero rule moved into `StockLedger` — and was then REVERTED WHOLESALE, because the rule
     itself was wrong.** ✗ **Do not rebuild it.** The premise for withholding an `OutNow` on a
     rhythm-less product was *"nothing can ever clear it, so the item pins Overdue forever"*. That is
     false, and one `grep` of `lastStockBack` would have shown it: it is the max of purchase dates **and
     restock dates**, so a one-tap **Restocked clears the pin** — on the very dashboard card the pin
     creates. Probed: `zero + OutNow → Overdue/Pinned=True`; `+ Restocked → Unknown/Pinned=False`.
     <br>The original gate finding said only *"a later census counting it at 3 does not lift it"*, which
     is true and still is. I generalised that to "unclearable" without checking, and three rounds of
     design followed from it.
     <br>⚠️ **And withholding actively broke something**: the `OutNow` was the only thing holding a zero
     once the count went stale. Probed — `zero, no purchases, counted 200d ago`: with the signal
     `InStock=0`; without it `InStock=1`, so recipes offered food the household had counted as none.
     That is item 24's bug with a 90-day fuse, and the change spread it from the census to the product
     page and the chat tool.
     <br>What survives the revert: the census's order-independence fix, the `ZeroOnNewProduct` refusal,
     the dead-clause and stale-comment cleanups. What went: `CountOutcome`, `hasPurchaseHistory`,
     `ZeroedWithoutSignal`, and `ProductDetail`'s third copy branch (unreachable again once a zero
     always pins).
     <br>**The transferable lesson is the cheap check I skipped**: before designing around "X can never
     happen", probe X. The whole arc cost three rounds and was refuted by four lines of probe output.
   - ⚠️ **A row-level decision that depends on other rows must be settled after all of them.** The
     `ZeroOnNewProduct` refusal was decided where the row sat, so `[Sardines 0, Sardines 2]` refused a row
     and said "nothing was created" about a product the next row created, while `[Sardines 2, Sardines 0]`
     refused nothing. Zero rows are deferred and settled once the census has been read. Pinned by a
     `[Theory]` running both orderings against one assertion set.
   - ⚠️ **"bUnit stops pumping continuations once a component is disposed" was MY false claim, corrected
     here.** It doesn't. The observation behind it was real — the reader recorded zero calls — but
     mis-attributed: disposal cancels `pageCts`, so `LoadCatalogAsync`'s own query throws
     `OperationCanceledException`, the `when` clause rethrows, and control never reaches the reader. The
     continuation ran fine. Classic failure to isolate one variable.
     <br>The captured-token fix **is** testable at the page level, and now is:
     `Leaving_the_page_mid_read_tears_down_quietly_instead_of_logging_an_error`. ⚠️ Two things make it
     work — upload **TWO** photos and gate the **FIRST** load, so the second loop iteration re-reads the
     token before any other token-aware work and nothing else can throw first. Gating the catalog load
     instead is what produced the vacuous version. Mutation-checked: the pre-fix `pageCts.Token` shape
     fails it.
   - **`RecordingLoggerProvider` (Web.UI.Tests)** exists because teardown behaviour is invisible in
     markup: once a component is disposed there is nothing left to render, so "this navigate-away wrote an
     ERROR into a real deployment's log" can only be observed through the log. Errors only — the level is
     the point.
   - Also fixed: `ConfirmAll`'s two cancellation clauses were dead by construction once the confirm took
     `CancellationToken.None`, and their message claimed a timeout that cannot occur; two comments still
     named the deleted `OutageWithoutHistory` (one in a test helper's docstring that asserted the OPPOSITE
     of the rule the same commit shipped); and a page test hard-coded loader copy the product no longer
     produces — the wording is pinned on both sides now.
   - **1339 tests green, 0 warnings** on a non-incremental Release build. ⚠️ Still not browser-verified
     since item 39: the HEIC path needs a real iOS device.
   - **The gate over the revert found no behaviour regressions — the first round of five that didn't.**
     What it did find was the revert's blind spot: deleting the withholding also deleted its tests, and
     the replacements went onto the census path only. Mutation-proven twice by independent reviewers —
     re-adding the withholding to `EfPantryStore.SetQuantityAsync` left **all 1333 green**, on the exact
     surface (product page + `set_quantity`) this arc says the rule had drifted to. ⚠️ **When a rule is
     deleted, its tests must be replaced with inverted ones on every surface that held it, not just the
     one the change was about.** Same for `ProductDetailCountPanelTests`: both zero-copy tests were
     deleted, but only one described the removed branch — mutating `@if (prediction.Pinned …)` to
     `@if (true)` then passed the whole 292-test UI suite.
   - ⚠️ **A third zero-copy state exists and had no branch: an outage asserted, then superseded.**
     That is the app's own recovery path — the one this arc cites as proof the pin is safe — and the
     panel told the household "nothing here has said so out loud", blamed cooking and receipts, and
     invited them to re-file the outage they had just cleared. `Restocked` is status-only, so the number
     stays at zero while the pin goes.
     <br>⚠️ **The first version of that branch's copy was itself wrong, twice over, and the gate caught
     both.** It named a **Restocked** while the predicate is "an `OutNow` exists" — equally true when a
     **purchase** cleared it (tap Out → buy it → cook back to zero), so it told people they had marked
     something restocked when they never had. And it claimed **"it isn't on the list"** from `!Pinned`,
     but the grocery list takes `Overdue` OR `DueSoon` — probed at `Status=Overdue Pinned=False`, the
     list showed it 23 days overdue while the panel denied it. That second one is item 21's bug
     reintroduced from the opposite direction. The copy now says only what `Pinned` licenses.
     <br>Fell out of it: `InventorySignal` rows are never pruned, so once a household has ever tapped
     Out on a product, the derived-zero branch is unreachable for it forever.
   - **My own numbers, corrected:** the revert commit said "16 tests across three suites"; it is **17
     cases / 16 methods across FOUR** — the UI suite simply wasn't run. Understating coverage is the safe
     direction, but it is a false number in the commit whose subject is correcting false numbers.
   - ⚠️ **Gate agents must isolate.** Reviewers running mutations concurrently in the shared working tree
     corrupted one reviewer's measurements (results came back inverted until it re-ran in a
     `git worktree`), and stray `Zz*Probe.cs` files from an earlier session compiled into the test
     projects and inflated a suite 532→535. A gate reviewer's numbers are only trustworthy if it isolated.
   - **The shape of this whole arc, for whoever reads it next.** Item 38 found 15, its fix introduced 5 of
     item 39's 7, whose fix introduced 5 of item 40's 7 — and item 40's "real" fix turned out to be built
     on a premise that four lines of probe output refute. **Every round after the first was spent on a
     problem that did not exist.** The gate caught it each time, which is the argument for the gate; the
     cheaper argument is the one skipped at the start — *before designing around "X can never happen",
     spend the grep.*

41. **The merge triage over the finished branch (2026-08-03, same branch) — ten findings probed, five
   phase commits of fixes, two deliberate stands.** Jordan's report listed ten open findings from the six
   review rounds with the standing instruction to probe before accepting any claim. Every claim was
   verified against b50a217 first: the baseline reproduced exactly (1339 green, 0 warnings), finding 1's
   data loss reproduced end to end through the real services (census counts 12 → remove the introducing
   receipt → product and count gone; the same product with one stray RunningLow survived), and finding
   8's mutation claim measured true — all 1339 stayed green with the three `IsDefined` guards deleted.
   All ten were real in substance; three sub-claims were corrected by probing; twenty-seven mutation
   rounds across the fixes and the two gate rounds below, each failing exactly its tests.
   - **The fixes, one commit per phase:**
     (1) *Removal counts an attested count as history* — `ReceiptRemovalService`'s delete half now agrees
     with its own subtract guard that an attestation is investment, keyed on `QuantityCountedAt`, NOT
     `TrackQuantity`: a dormant count is kept history (item 28) and must keep its product. Both
     directions pinned; the kept count asserts exactly 12 so the subtract stand-down rides in the same
     test. Pre-existing, but the census made "receipt-introduced product carrying a fresh count" the
     bulk state.
     (2) *Census grid honesty* — Tick all overrides the CONFIDENCE default only (§13.8's words), skipping
     Unidentified rows and STILL-SELECTED similarity matches via a shared `FuzzyStillSelected` so the
     row's warning and the bulk action can't drift; a similarity row the human resolved ticks normally.
     Variety/Brand/Size ride `ReviewRow` into a subtitle and `DescribeRow` — two same-named variety rows
     were identical to a screen reader while the page's own "counted twice" warning invited ✕ing one,
     silently shorting the summed total. The Category cell renders the matched product's REAL category
     as text and stays a select only where the value is actually written (create-new); the fixture for
     that test makes reader and store disagree, per item 38's cannot-tell-branches-apart lesson.
     (3) *Twins* — §13.8 gained the rule: never pre-filled (suggestion and matcher paths both), arrival-
     and bulk-tick stand down, dropdown options carry counts, the service refuses `AmbiguousName` rather
     than `First()`, and an explicit create-new on a twin name keeps its `DuplicateName` answer.
     `ProductMatcher.ExactMatches` (Core) answers the plural question so no page re-derives the
     matcher's normalization (item 39's rule). A human's pick by id is ordinary and pinned end to end.
     (4) *The zero-panel's advice* — `PredictionResult.SignalTodayWouldBeInert` (born
     `OutNowTodayWouldBeInert`; renamed in the re-gate below), computed in the engine
     beside §6.6's tie rule; both zero-copy branches split their advice on it, so "set it to 0 again"
     is no longer offered on a day it cannot work. The engine comment's "ignored until tomorrow" now
     says the truth (that signal is ignored forever; a fresh one tomorrow works). The 2c comment names
     the RunningLow road, the dead `!prediction.Expired` conjunct is gone (the chain's expiry arm above
     already takes every Expired state), and the RunningLow road's mechanism-free copy is pinned.
     (5) *Test hardening* — the zero-panel's negative guards are case-insensitive and phrase-loose now
     ("you Restocked it" and "it's not on the list" both previously passed all 296 UI tests — proven by
     planting them); the three `IsDefined` sites each have a numeric-smuggling test (`record_signal`'s
     pins that nothing is WRITTEN); the >8-photo refusal is pinned with its recovery; the teardown test
     awaits the click handler's own completed task instead of `Task.Delay(50)` before a negative
     assertion (item 34's class — under load the old wait could pass before the continuation ran).
   - ⚠️ **Corrections to the report, each probed:** (a) 2a is DAY-scoped, not permanent — each signal
     filed that day is permanently inert (strict `>` on dates that never change), but a fresh one works
     from tomorrow; `BurnCycles`' strict `o > start` keeps the dead rows out of the burn rhythm, so the
     residue is inert rows and nothing else. The sharpest road in is a mis-tapped Restocked and a
     same-day undo attempt. (b) 2b was already recorded (item 40) and STAYS accepted — Jordan's call:
     the rendered copy is true in every road, and every principled predicate collapses back to `Pinned`,
     so a day-window would be the judgment-call-around-an-edge pattern that produced rounds 2-5.
     (c) 2c's harm was the branch COMMENT, not the copy — the sentence survives the RunningLow road
     precisely because it names no mechanism.
   - **Finding 10 stays as §13.8 records it, with Jordan's rationale now part of the record:** a stale
     positive count keeps reading as in-stock — "if they said it's in stock we shouldn't consider it out
     unless they say so; marking out of stock or zero is one tap, and a recipe suggesting it lets them
     go 'oh snap, I'm out.'" The floated "show me my stale counts" view went to the backlog as its own
     idea, not a census-branch feature.
   - **Known residuals, stated rather than silent:** a census ZERO on a product whose stock-back is
     today writes an OutNow that is permanently inert (the same §6.6 tie — the summary's "recorded as
     running out" is true of the row and void of effect); twins with identical counts remain
     indistinguishable in the dropdown, said on `OptionLabel` itself.
   - ⚠️ **The gate over this pass found five more, all fixed — item 39's lesson held: a fix pass needs
     eyes that didn't write it.** Two review agents ran in ISOLATED worktrees; both worktrees arrived
     STALE at master — the exact failure the brief warned about — and both caught it through the
     verify-your-commit-first instruction and reset to the right SHA before reviewing. Security came
     back clean with probes (a cross-household plant wearing this household's receipt breadcrumb is
     invisible to removal — mutation-validated). Code review, each CONFIRMED with a probe:
     (a) the removal fix KEPT a null-`ConfirmedAt` introduced product but still SUBTRACTED — attested
     12 read back as 11, silently, on exactly the population the fix was for. The introduced-arm
     closes it: a product this receipt introduced did not exist before its own confirm, so every
     attestation provably postdates it even with the timestamp missing; the pre-existing-product
     sibling keeps the documented subtract-as-always arm.
     (b) `OutNowTodayWouldBeInert` was `==` where the filter is strictly-after — a FUTURE stock-back
     (chat's `add_purchase` has no future clamp; the TZ gotcha) discarded the signal while the member
     said otherwise, reviving the silent no-op one state over. `>=` now, future case pinned.
     (c) the twins guards held TWO definitions of "ambiguous" — `Match()` judged by the matcher while
     the warning, Tick all and the service judged by raw names — so the punctuation pair arrived
     unticked with NO reason shown and Tick all walked through the gap onto a real count: the exact
     drift `FuzzyStillSelected` exists to prevent, rebuilt one guard over. ONE definition now
     (`ProductMatcher.ExactMatches`) at every layer, pinned at all three guards and the service.
     (d) the member had no consumer beyond the count panel while two louder surfaces still promised
     the act: `record_signal` — which TALKS, item 27's class — now appends the tie caveat (the signal
     is still written; the caveat is honesty, not refusal), and the products grid's Out button says
     when a tap can't take effect, read off the same predictions dictionary its rows render from.
     Both directions pinned on both surfaces.
     (e) the SetAll comment counted "two" non-confidence guards; there are three.
   - ⚠️ **The re-gate over the gate's own fix commit found three more.**
     (f) The tie caveat covered ONE of the two signal kinds the engine's filter discards identically —
     "I'm running low on milk" the day milk arrived was the same spoken no-op one enum value over. The
     member's OutNow-specific NAME is what invited the OutNow-specific consumer, so it is
     **`SignalTodayWouldBeInert`** now (the doc says why) and `record_signal` caveats both kinds;
     Restocked is exempt because it IS a stock-back, not a subject of the filter.
     (g) The census refused ambiguity by the matcher's identity rule but RESOLVED by raw equality — so
     one census could MINT the punctuation pair (a row per variety, transcribed with and without a
     hyphen, both "new"), after which every later census of that shelf was refused `AmbiguousName`
     forever, the refusal's advice costing another vision call. One identity set for refusal AND
     resolution now: a same-visit variant row folds into the first row's product and the counts sum,
     and a lone variant resolves onto the existing product instead of minting its twin (rule 1 is
     identity, not similarity, so this is exactly as safe as the raw resolve).
     (h) `MarkOut` gained the dashboard-pattern double-tap guard — raced honestly in its test via the
     harness's HoldNext — and its note clears when a delete changes the shelf under it.
     Three more mutation rounds, each killing exactly its tests. The residual the re-gate signed off:
     an explicit CreateNew with a raw-unique, normalized-colliding name still creates (unreachable
     from the grid — an ambiguous row never gets `ChoseCreateNew`); and the introduced-product
     premise in (a) depends on every `Attest` caller passing "now", which all production callers do.
   - ⚠️ **The THIRD review round found eight more, all in the ~90 lines the previous fix touched —
     and the pattern itself became the finding.** Rounds went 5 → 3 → 8: each fix converted one more
     site of a shared rule and left its neighbours, so every round shipped a fresh half-state. The
     three worst were all one defect wearing different hats — **"which product does this name mean?"
     was answered in nine places** (`Match`, `AmbiguousClash`, `NameClash`, `NearMatch`, `twinNames`,
     the service's resolve, its DuplicateName guard, `createdByName`, the deferred-zeros key) and I
     had been converting them one at a time:
     (a) broadening the RESOLVE to the identity set while `NameClash` stayed raw made the grid's own
     copy a lie — a typed "Half-and-Half" fell past it to `NearMatch`, which offers *"or leave this to
     create a separate item"*, over a write that REPLACED the existing product's count of 9 with 1,
     with no "Was N" note (it keys on the dropdown) and no refusal. Probed end to end by the reviewer.
     (b) the create-new guard judged "taken" RAW, so an explicit create-new whose name was raw-unique
     but identity-colliding **minted** the punctuation pair — after which every later census naming
     either twin was refused `AmbiguousName` forever. That is the residual the previous round had
     signed off as "unreachable from the grid"; it is two clicks.
     (c) the deferred-zeros settle-up keyed RAW, rebuilding item 40's row-ORDER dependence for exactly
     the pair the identity rule exists for — invisible to its own `[Theory]`, which uses two
     *identical* raw names.
     **The altitude fix, per item 40's lesson: `ProductMatcher.IdentityKey` is now THE answer**, used
     by every guard, dictionary and warning on both sides of the census. A new site uses it or
     `ExactMatches` — never `string.Equals` on names.
   - Also from that round: the page-wide `markingOut` flag had no `disabled` binding, so a tap on a
     DIFFERENT product mid-write was accepted and **silently dropped** — the "tap that looks ignored"
     failure `markOutNote` exists to fix, reintroduced by the guard added beside it (the dashboard
     pattern is flag AND disabled; only half had been copied). `MarkOut` also gained the catch its
     `try` had been sitting there without. And the tie copy claimed "stock was recorded today" and
     "try tomorrow" — both false for a FUTURE stock-back, which `>=` deliberately covers; all four
     surfaces now say "as of today or later … once that date has passed".
   - ⚠️ **A fake that hands back its own live objects cannot model staleness — and hid both a bug and
     its fix.** `record_signal` asked the engine about the START-OF-TURN snapshot, so *"I bought coffee
     today but I'm still running low on it"* — one turn, two tool calls — answered a bare "Recorded",
     **aloud**, about a signal the engine had already discarded. The handler re-reads now; and
     `FakePantryStore.GetProductsAsync` returns a fresh snapshot per call (writes since an earlier read
     appear; objects from that earlier read do not change under them), which is what makes the fix
     observable. My first attempt at this instead taught `AddPurchaseAsync` to mutate the shared
     product — which made the mutation test pass with the fix reverted, i.e. it masked the very defect
     under repair. **Item 20's rule, in the other direction: a fake must not be more CONVENIENT than
     the real store either.**
   - ✅ **The FOURTH round is where the cascade broke: zero behaviour regressions introduced by the
     altitude fix** — the first fix pass of four that created no new defect. The reviewer pushed hard
     on the census write path and could not make it destroy or invent a count. What it did find was
     the same class **one level up**, which is the lesson: the rule was raised into `ProductMatcher`
     and then applied only to the nine census sites, leaving the app's other three product-identity
     guards on `string.Equals` — the Products add form (`duplicateIsExact`), chat's `create_product`,
     and `ProductRenameService`. Two of them MINT the very pair the census refuses: probed, typing
     "Half and Half" beside a catalog's "Half-and-Half" reached the FUZZY branch and offered
     "Add anyway", and one click then jammed every later census of that item on `AmbiguousName` —
     escapable only by picking from the dropdown, i.e. another vision call. All three now ask the
     matcher (`ResolveWithKind`/`ExactMatches`), each pinned by a punctuation-pair test.
     ⚠️ **"Convert the sites you were looking at, leave the neighbours" survived the very commit that
     named it** — which is why the directive at the top of this file says a partial conversion IS the
     bug, and why the scope of "every caller" has to be the app, not the file you have open.
   - Also from that round: the fourth surface's copy fix (`markOutNote`) was a **mutation survivor** —
     its only test asserted a phrase both wordings contained, so reverting it left 315/315 green,
     while the commit message claimed "two tests forbid the old claims" across four surfaces (item
     21's false-number class, in a claim about tests). And ProductDetail's negative guards pinned two
     literal phrasings rather than the rule: an evasive re-wording ("stock was **logged for it**
     today") restored the false claim and passed them. Both now assert the positive
     "as of today or later" as well. Smaller: `NearMatch`'s new `|| AmbiguousClash` was dead by
     construction (`NameClash` returns first for >1 too), `NameClash` is memoized like its two
     neighbours now that it normalizes the catalog, `record_signal`'s re-read moved inside the
     kind check (every `Restocked` was paying for a value it never used) and a product deleted
     mid-turn now gets the plain reply instead of a caveat computed from data those lines just called
     stale, and `Normalize` collapses ANY run of separators — a single `Replace("  ", " ")` left
     `"Yogurt - Strawberry"` as `"yogurt  strawberry"`, so the documented dictionary KEY was neither
     equal to its own spaced form nor idempotent (it failed safe — rule 1 missed, rule 3 caught it as
     similarity — but a near-key is not a key).
   - ⚠️ **An ambiguous SUGGESTION now says so — recorded as "leave it" and then FIXED, because the
     browser pass reproduced it on ordinary model output.** A can read as "Canned Tomato Sauce" whose
     `existing_product` was "Home Canned Tomato Sauce" — a name two products answer to — arrived
     unticked (right) with nothing able to explain it (wrong), because every live guard judges
     `row.Name` and the ambiguous string is the SUGGESTION. Worse, `NearMatch` filled the silence with
     *"or leave this to create a separate item"*, which answers a different question. Jordan's call:
     if the app knows why it withheld the tick, the row must say so. The read-time fact rides on
     `ReviewRow.AmbiguousSuggestion` now (the same shape as `FuzzyMatch`), gets its own sentence
     naming what the reader thought it was, stands `NearMatch` down, and is honored by Tick all;
     picking anything in the dropdown resolves it, exactly like `FuzzyStillSelected`.
     ⚠️ **The name rides along only when it is one the live guards cannot see.** When the ambiguous
     suggestion IS the row's own name, `AmbiguousClash` already says it and keeps saying it as the
     human edits — carrying it too put two sentences about one problem on one row (caught by an
     existing test, not by review).
     ⚠️ **And the flag is separate from the name for a reason found the hard way**: `Include` is
     computed once, at read time, before any live guard can run — so returning only the name (on the
     theory that `AmbiguousClash` would cover the tick) let a punctuation-twin row arrive **ticked**.
     The existing twin test caught it immediately, which is what a both-halves-pinned test is for.
   - **Live-verified after the conversions** (2026-08-04, dev server on the alt port, the throwaway
     household from the census walkthrough): the add form blocks "Slow Cooked Beans" against a catalog
     "Slow-Cooked Beans" outright — *"You already have Slow-Cooked Beans"*, no "Add anyway", no twin —
     where pre-fix it offered the fuzzy branch and one click minted the pair; renaming a DIFFERENT
     product to a punctuation variant is refused while renaming a product to its OWN de-punctuated
     form still works (the check excludes itself, so a household can still fix its own punctuation);
     and typing "Slow-Cooked Beans" into a census row renders *"This will go to the existing 'Slow
     Cooked Beans'"* instead of offering a separate item, with the confirm then reporting "Counted 2
     items" and no new product — screen and write agreeing, which is the whole of finding 1. Zero
     server errors. (The console's SignalR reconnect errors were ~87 minutes stale, from stopping the
     PREVIOUS dev server with the tab open — check timestamps before believing that class of error.)
   - **1384 tests green, 0 warnings** on a non-incremental Release build (1339 at the start of the
     pass; +45). Read off the final run before being written here, per item 21's rule.

42. **The census cascade RESOLVED — Group A + the `CensusPlan` redesign (2026-08-05, same branch).**
   Picked up `docs/census-branch-handoff.md` cold: item 41's `/code-review` had left 15 confirmed findings,
   partitioned into 8 stable-code bugs (Group A), 6 grid/service guard bugs the handoff wanted **deleted by a
   redesign, not patched** (Group B — patching this exact cluster had produced a fresh defect six passes
   running), and a `Normalize` nit. All addressed; **1438 green / 0 warnings**, live-verified, both halves
   independently reviewed clean, unpushed. This session added 7 commits (`b2142f6..a37a946`), 29 ahead of master.
   - **The redesign is the "one accessible definition" directive made structural, at the whole-FEATURE level.**
     The census grid answered FIVE questions — arrive ticked? tick-all eligible? what will confirm do? why
     isn't it ticked? what does confirm actually write? — each in a different place from a different subset of
     inputs, with the markup's if/else ORDER load-bearing. `CensusPlan` (Core, pure) is now the ONE function
     both the grid and the write path ask: `Prefill` (the read-time dropdown pre-fill, the old grid `Match`) +
     `Plan` (classifies every row in one whole-census pass into `Action` land/create/refuse, `LandsOn`, a
     single `Reason`, and `NeedsAHumanLook`). The grid message is a `switch` on `Reason` — one per row by
     construction, so no ordering bug is expressible. This DELETES the six Group-B findings rather than
     patching them (525 lines of guard soup out, 292 in): `Match`/`NameClash`/`NearMatch`/`AmbiguousClash`,
     their caches, the derived `FuzzyStillSelected`/`SuggestionUnresolved`, the whole if/else chain — gone.
     Precedent: `ReportSpecRules` (§16), one rules class the builder UI and the engine both consult.
   - ⚠️ **One function, two callers, because the WRITE decision depends only on name/count/dropdown — never on
     how the reader saw it.** The grid supplies the real read-time facts (evidence, confidence, a still-selected
     similarity guess, an ambiguous suggestion) and reads all four plan fields; the service supplies NEUTRAL
     facts and reads only `Action`/`LandsOn`/`Reason`. The neutral facts can only ever move `NeedsAHumanLook`
     (the tick), which a write path never renders — independently verified: the service reads `Reason` only in
     the `Refuse` branch, and every refuse reason is a function of name/count/dropdown/catalog alone. So the
     screen and the write CANNOT disagree about which product a row lands on — the "one prediction, one story"
     fault this arc broke through six rounds is now unexpressible.
   - `CatalogIndex` (identityKey→products, built once) replaces the O(N²) twin scan and the three per-render
     `ExactMatches` memos. ⚠️ **Deferred-zero settlement is whole-census** (a count-0 novel row joins a sibling
     that CREATES the same identity key, else `ZeroOnNewProduct`) — order-independent by construction, the exact
     row-order dependence items 40/41 kept re-introducing, now structural. The service keeps its public contract
     (46 tests untouched) and the grid its ~65 page tests: behaviour preserved, verified by keeping every one green.
   - **38-case pure `CensusPlanTests`** (Core, no EF/bUnit): evidence × suggestion × name × dropdown × count,
     plus the whole-census interactions. Every branch mutation-checked. Two deliberate deviations from the
     handoff's sketch: `CensusReason` isn't its exact 11 (the four match-provenance values collapse — only
     *similarity* is behaviourally distinct — and `ResemblesExisting`/`WillLandOnExisting`/`NoName` are added);
     and `ResemblesExisting` is a soft warning, NOT a tick-blocker — a page test
     (`Tick_all_ticks_a_similarity_row_the_human_already_resolved`) rejected the "improvement" of blocking it,
     so it was reverted, matching the app's standing NearMatch behaviour.
   - **Group A — six stable-code bugs the census cascade never touched, each tested + mutation-checked:**
     ⚠️ #1/#14 are item 41's "which product does this name mean, EVERYWHERE" reaching two more guards —
     `ReceiptConfirmationService`'s within-receipt `createdByName` keyed on the RAW name (two lines of one new
     item transcribed with/without a hyphen minted a twin, on the app's highest-volume creation path), and
     `ProductRenameService` re-pointing recipe links by `ToLower()` while the collision check one line above
     already used `ExactMatches` (a partial conversion INSIDE one method). Both on `ProductMatcher.IdentityKey`/
     `ExactMatches` now. #5: a model suggestion or matcher hit that names TWINS is a coin flip a machine confirm
     must not make — `ReceiptAutoConfirmer` + the Upload pre-fill route it to review (the break-Auto contract
     `ReceiptDuplicateDetector` holds for exact dupes). #3/#6: two chat tools re-read before speaking —
     `query_status` spoke a stale "Stocked, due <future>" about a product reported out earlier in the SAME turn,
     and `set_quantity`'s zero got the `SignalTodayWouldBeInert` caveat (the third talking OutNow-writer to
     reach it). #11: `Products.SetCategory` guards a browser-supplied `Category` with `Enum.IsDefined`.
     #15: **`Normalize` KEPT, not reverted** — the handoff recommended reverting its split/join body to a single
     `Replace`; that reintroduces a known near-key defect (`IdentityKey("Yogurt - Strawberry")` must equal
     `"Yogurt Strawberry"` and be idempotent), so it was kept and pinned instead.
   - Census-page findings folded into the grid rewrite: **#12** the whole grid (row inputs + the ✕) disables
     during a confirm (the confirm snapshots the ticked rows, so a mid-save edit would look like it un-counted
     a row it didn't); **#13** the empty `catch (JSDisconnectedException)` left a permanent spinner if it fired
     while the page was still ALIVE (a transient circuit drop mid-read) — split like the
     `OperationCanceledException` clause beside it (swallow on real teardown = `pageCts` cancelled, surface an
     error otherwise). Both mutation-checked.
   - **The gate. Live-verified on real model output** (throwaway household, alt port 5180): a synthetic 4-item
     shelf read produced a clean suggestion match (ticked, "Was N" note, read-only category), a novel create
     (editable category), a matched fast-mover (the nudge, keyed on the resolved product), and an unidentified
     parcel (unticked, "couldn't tell", "name it"); the flagship identity fix — typing `home canned tomato sauce`
     said **"This will go to the existing 'Home-Canned Tomato Sauce'"**, NOT "create a separate item", flipped
     the category read-only and showed "Was 9" keyed on the resolved target; confirm wrote counts with **no
     PurchaseEvent** (★) and attest REPLACED the old count (3→1), purchase history untouched. No console/CSP/
     server errors. ⚠️ Blocked from an interactive walkthrough by the auth wall until Jordan signed in (creating
     an account / entering a password is a prohibited action).
   - ⚠️ **`/code-review` (local) is model-invocation-disabled — user-trigger only.** The independent security
     and code reviews ran as one `general-purpose` helper agent each (NOT the billed cloud `ultra`). Security:
     CLEAN — no new DbContext outside `IHouseholdDbFactory`, no new `IgnoreQueryFilters`, no write path carrying
     a foreign household id, no new endpoint/settings-key/per-household disk write; a tampered circuit
     `ProductId` fails the household-filtered `ById` → `ProductGone`, never reaches across. Code: core sound
     (the read-facts/write-decision separation above was traced and confirmed), four LOW findings.
   - **The four LOW findings — three fixed, one documented (each fix mutation-checked, and the fix pass then got
     its OWN independent review, clean — item 39):**
     - **A** (a regression THIS branch introduced): #5's twin fix over-generalised "however it resolved" — an
       ALIAS resolves by `ProductId`, so it names one product outright even when that product's name is a twin,
       and bouncing that taught pairing to review was a false alarm. Gated on `alias is null` now (only a
       name-based resolution can be a coin flip; the review confirmed the gate rests on `ProductAlias.ProductId`
       being a non-nullable required FK, so an alias never falls through to the matcher).
     - **B** (parity with #11): the census and receipt create-product sites bound `Category` from a circuit
       `<select>` without `Enum.IsDefined`; a tampered message could persist `(Category)9999`. Both default to
       `Other` now. Self-scoped (own household, no tenancy crossing — graded LOW/not-a-vuln), same class as #11.
     - **D** (polish): a negative count showed no inline grid message though an empty count did — added.
     - ⚠️ **C — documented, NOT fixed.** The grid previews `Plan` over ALL rows while the confirm runs it over
       the TICKED ones, so a count-0 novel row with an UNTICKED positive sibling previews as "will create" but
       confirms as `ZeroOnNewProduct`. Safe-direction only (a ticked subset can never GAIN a positive sibling,
       so the dangerous preview-refuse→confirm-creates-a-phantom is impossible), REPORTED in the Done panel, and
       needs a contrived sequence. A "fix" would couple `CensusPlan`'s settlement to tick-state (`Include` isn't
       in `CensusRowState`) — the exact new-defect trap this arc hit six times. Comment at `ConfirmAll`.
   - **1438 tests green, 0 warnings** on a non-incremental Release build (1384 at the start; +54: 38 pure
     `CensusPlan` + 10 Group A + 2 census-page + 4 follow-ups). Read off the final run (item 21). ⚠️ **`/pre-push`
     was run with INLINE/agent reviews, not the independent cloud gate** — the two agent reviews are the closest
     available substitute and are author-adjacent by construction; a fresh independent pass is still the ideal
     before merge. Pushing is Jordan's call; unpushed.

43. **The max-effort `/code-review` over the whole branch + its same-session fix pass (2026-08-08,
   Jordan-triggered).** Ten finder angles, one-vote verification, a gap sweep — nine of the ten finder
   agents died on the session limit, so those angles ran INLINE per Jordan's instruction (the efficiency
   agent completed first and its candidates were re-verified inline). Nine findings, **zero serious
   correctness defects** — the first full-branch pass of this arc to come back without one; two PLAUSIBLE
   edges and seven cleanup/efficiency items, all nine fixed on Jordan's "fix all 9". What the fixes carry:
   - ⚠️ **Tool calls within ONE model round shared the round's product snapshot.** Parallel tool use ships
     several calls per assistant turn, and `products` refreshed only BETWEEN rounds — so a duplicated
     create_product ("Half and Half" beside "Half-and-Half", or the same name twice) walked past the twin
     guard and minted the identity pair item 41 closed everywhere else, and a create-then-use pair in one
     round answered "call create_product first" to the model that just did. The snapshot refreshes after
     each create_product WITHIN the round now (keyed on the one tool that adds products; the between-round
     refresh stays for counts/signals). Both directions pinned.
   - ⚠️ **A name with no letters or digits folds to an EMPTY `IdentityKey`, which the identity system
     cannot see** — `CatalogIndex` skips empty keys and `ExactMatches` refuses them, so a punctuation-only
     name ("!!") could only ever create a sight-unseen twin per census, and DISTINCT junk names in one
     census/receipt shared the "" key and silently merged into one product. Fixed at two altitudes,
     matching the architecture: `CensusPlan` refuses it as `NoName` (to product identity it IS no name;
     the dropdown-pick path deliberately untouched — the id names the product, complement pinned), and
     the receipt confirm's roll-up key falls back to the RAW text (the key kinds can't cross: an identity
     key is alnum+spaces only, and a name whose key is empty leads with a character no key contains).
   - **`ProductOptionLabel` (Core/Shopping) is THE twin-dropdown phrasing** — "N on hand / had N, counting
     stopped / not counted" existed twice (census grid + Upload), the top directive's two-definition
     shape on a rule this branch itself introduced. Both grids call the one helper; wording pinned in Core.
   - **`IPantryStore.GetProductAsync`** — the same-day-tie caveat's re-read loaded the ENTIRE catalog
     (purchases+signals includes) on every relative "used one" and discarded it whenever the landed count
     wasn't zero; it reads one product now. New store surface, so it walks the tenancy drill: a foreign
     household's id answers null exactly like a nonexistent one, pinned.
   - **`CatalogIndex.ResolveWithKind` memoizes per query** — the census grid re-ran a full catalog
     re-normalization + IDF rebuild per create-candidate row on EVERY render, and `MatchMessage` asked
     the identical question again per resembles-row; the catalog is immutable for the index's lifetime,
     so a resolve is pure (⚠️ the doc now says so — build a fresh index after any catalog change). The
     index also serves `ReceiptAutoConfirmer` and Upload's pre-fill now (one per receipt), replacing two
     full-catalog `ExactMatches` scans per line.
   - Smaller: census `Read()` starts the catalog load BEFORE the photo-transfer loop (independent work on
     one spinner) — ⚠️ the finally OBSERVES the still-in-flight task when a photo fails, and that observe
     block is review-verified, not test-pinned (an unobserved-task exception is invisible to bUnit;
     stated per item 27's precedent); `ShelfPhotoLoader` pre-sizes its buffer to the browser-reported
     size and skips `ToArray`'s second copy when that size was exact; the rename collision check reads
     `AsNoTracking` (the list only feeds `ExactMatches`, and tracked entities rode into SaveChanges'
     diff scan for nothing).
   - ⚠️ **The header's test count was stale AGAIN** — it read 1438 while the suite at HEAD stood at 1451:
     the three post-item-42 commits added 13 tests without re-reading the number off a run. Item 21's
     rule, third occurrence, in the file that states it.
   - **1463 tests green, 0 warnings** on a non-incremental Release build (1451 at the start of the pass;
     +12), read off the final run. Every new test mutation-checked — SEVEN mutations across five runs, each
     killing exactly the tests it should and nothing else (including the guard-placed-too-early mutation
     the dropdown-pick complement exists for). ⚠️ This line first read "six mutations in four batches",
     and the fix commit's message still does (uncorrectable) — item 21's false-number class, written from
     memory instead of recounted from the transcript, caught by the re-review below.
   - **The re-review of the fix commit itself (2026-08-09 — item 39's discipline, applied to this pass
     too) found two, both fixed:** the mutation-count error above, and the new junk-name `NoName` refusal
     rendering NO pre-confirm message — a visibly-named "!!" row was first told about its refusal in the
     Done panel, the say-it-before-the-confirm rule item 42's finding D applied to negative counts. The
     `MatchMessage` arm added covers BOTH `NoName` shapes, so the blank-name case gained the inline
     message it always lacked rather than the junk case inheriting its silence. Both mutation-checked
     (case disabled + branches swapped, each killing exactly the two new page tests). **1465 tests green,
     0 warnings** on a non-incremental Release build (+2), read off the final run.

44. **The family instance went public — Cloudflare Tunnel + Access (2026-08-12, branch
   `feature/family-cloudflare`).** The `ShelfAware-server` publish on Jordan's PC (port 5179, boot
   scheduled task) is now reachable at **https://family.shelfaware.net**: `cloudflared` as a Windows
   service dials out to Cloudflare, a published application route maps the hostname to
   `localhost:5179`, and **Cloudflare Access** (One-time PIN to two allow-listed emails, 1-month
   sessions) gates it at the edge. The tailnet door stays; the demo droplet is unaffected.
   **Zero app changes** — probed first: loopback-only binding, `AllowRegistration: false`, managed
   keys + quotas, and the loopback-proxy forwarded-headers trust all already fit this shape.
   `docs/family-cloudflare.md` is the full as-built runbook, including the SIX dashboard traps the
   2026 "Tunnels & Mesh" UI sprang in one evening. The two lessons that generalize:
   - ⚠️ **Judge Access state by the newest real log row, never by the config screens or the policy
     tester.** A policy showed as attached while the edge evaluated ZERO policies (real denial log);
     the fix was rebuilding the policy inline on the app. And the tester is unusable in a virgin org
     (`invalid_user_id` — it simulates an EXISTING user; there were none until the first successful
     login), reporting "0 policies evaluated" as its own failure debris. Verified working =
     `"connection": "onetimepin", "allowed": true` in the Access log, then data on screen.
   - ⚠️ **A negative DNS answer cached during setup outlives the fix** — SOA minimum here is 1800s,
     per resolver (router, each carrier). The tells and the don't-re-edit-healthy-config rule are in
     the runbook; `Resolve-DnsName … -Server 1.1.1.1` is the truth during the countdown.
   Queued next (Jordan's call, 2026-08-12): **forgot-password** — an `IEmailSender`/SMTP seam
   (config-gated like Google OAuth), Identity's existing reset-token flow, two static-SSR Account
   pages + tests + the gate; his wife (currently locked out, in no hurry) is the planned first
   real-world tester on the family box, which also needs its first publish since mid-July
   (`publish-family.ps1` to script it — AdditiveSchema handles the in-place DB migration, backup set
   first).

45. **Forgot password + the family publish script (2026-08-12, branch `feature/forgot-password`).**
   Born from Jordan's wife forgetting hers; she is the planned first real-world tester once the family
   box gets its first publish since mid-July. Seven commits, four independent review rounds (every
   round found something until the last — the house pattern held to the end), **1481 tests green, 0
   warnings**, the whole flow live-proven: a real Gmail SMTP send driven through the form's own
   protocol (antiforgery + `_handler` via curl), the email in Jordan's inbox, the link clicked, the
   password reset, the new one signing in.
   - **The feature:** `EmailOptions` ("Email" section; all-or-nothing `ValidateOnStart` — a partial
     section refuses to boot, live-verified) + `IAccountMailer`/`SmtpAccountMailer` (MailKit) + two
     static-SSR pages. **`EmailOptions.IsConfigured` is THE one definition of "this deployment can
     send email"** — the sign-in link, `/Account/ForgotPassword`'s explainer, and Settings' wording
     all gate on it, so the surfaces can't drift. Unconfigured deployments (droplet demo, keyless
     self-hosts) are untouched: the feature simply doesn't exist anywhere it shows.
   - ⚠️ **An `OnInitialized` redirect does NOT stop a form handler, and every static-SSR handler must
     self-guard.** With `BlazorDisableThrowNavigationException`, `NavigateTo` records the redirect
     and RETURNS — the framework still invokes the form handler in the same request.
     `IdentityRedirectManager`'s docstring warns "callers must return", but returning from
     `OnInitialized` is not enough. The review proved it with probes: a code-less POST to
     ResetPassword reached the handler, `Base64UrlDecode(null)` threw `ArgumentNullException` (NOT
     the `FormatException` the catch expected) AFTER the user lookup branched — **302 for unknown
     emails, 500 for existing ones: an account-enumeration oracle on the page built to prevent one.**
     Fixed by guarding `Code` FIRST (re-probed live: 302/302); a flow test pins the framework premise.
     The sibling hunt then found the same shape twice, pre-existing: **ChooseHousehold's handler could
     silently REPLACE a household** (double-click / second onboarding tab → `CreateForAsync` runs
     again → the first household orphaned with its pantry) and ChangePassword could 500 on a
     deleted-account cookie. Both guarded now.
   - **Enumeration discipline:** ForgotPassword gives ONE redirect-with-status response for every
     outcome — unknown email, sent, and **send failure** (which can only occur on the account-exists
     branch, so surfacing it would answer exactly the question the form refuses to). Timing residual
     documented-accepted; both new POSTs sit under the existing `/Account` per-IP limit. Mandatory
     STARTTLS (review finding: `SecureSocketOptions.Auto` on 587 is StartTls*WhenAvailable* — an
     active attacker stripping the EHLO got credentials + reset link in CLEARTEXT; now 465→SslOnConnect
     else StartTls, fails closed — a plaintext-only localhost relay won't work, deliberately). Host
     pinning documented (`AllowedHosts` in env.example + the runbook's Nginx note): every current
     front door pins the hostname, but the reset link is built from the request Host, so the Nginx
     path now says pin it or set `AllowedHosts`.
   - ⚠️ **The Account pages still have NO test harness** (none ever did — bUnit can't drive their form
     posts), so the oracle guard and both sibling guards are review- and live-probe-verified only;
     the flow tests pin the Identity-layer premises (token round-trip through the pages' exact
     Base64Url transport, tamper→InvalidToken, policy-vs-token error separation, stamp rotation =
     other-session eviction, single-use). Three logic mutations each killed exactly their tests.
   - **Secrets:** the Gmail app password (2FA'd for this) went file → user-secrets → the family
     server's `appsettings.json` through shell variables only, never echoed. ⚠️ The drop file landed
     at `src\app-data\` — a NOT-ignored sibling of the real gitignored path — caught untracked and
     destroyed after transfer; `git log --all` confirms it never entered history. The family config
     edit was a blind text insert after the opening brace (comments make ConvertFrom-Json a trap),
     with a timestamped `.bak` kept.
   - **`deploy/publish-family.ps1` — the family box's stage-and-swap publish**, reviewed twice before
     ever touching the live server, which is the only reason it's trustworthy:
     ⚠️ a `/MIR`-mirror draft's **list-only dry run** showed it would have DELETED the July pre-v3
     backup at the server root (`/XD app-data` protects only the literal name) — rehearse any
     destructive filesystem tool with `/L` first. It swaps instead: keepsakes sweep to
     `ShelfAware-server-attic` (Jordan's call: relocate, never delete; `runtimes` deliberately
     excepted — it's OLD portable-layout publish output a rollback needs). ⚠️ The review then found a
     **compound data-loss chain**: fail mid-copy → reboot → the boot task auto-creates an EMPTY
     app-data → a re-run's precondition passes → `-prev` deleted with the REAL database inside.
     Killed with three independent defenses: **data moves FIRST** (instant same-volume `Move-Item`
     before the slow binary copy), **deleting `-prev` refuses when it still holds an app-data** (the
     failed-swap fingerprint), and the task is **disabled during the swap** (a reboot can't
     interleave). Failure posture, from the final round (which caught the disable fix's own
     regression): pre-swap throw → task re-enabled, aborted publish ≠ outage; mid-swap throw →
     deliberately down with state-aware advice. Port 5179 is a param, verified against the live task.
     ⚠️ **MUST run from an ELEVATED PowerShell** — controlling the scheduled task
     (Disable/Stop/Enable/Start) is access-denied otherwise (HRESULT 0x80070005, found on the first
     real run attempt); the script checks `IsInRole(Administrator)` FIRST, before the ~1-min build, so
     a non-elevated run fails in a second with a clear message and never touches the live box. Claude's
     own tool shell is non-elevated and cannot run this — the publish is Jordan's to run.
   - **Publish-ready state:** the family server's `appsettings.json` already carries the Email
     section; the runbook trio (backup set, AdditiveSchema in-place migration, click-around after)
     applies; the publish brings v3.5→today (variety, expiration, Reports, counting, census, tour)
     along with the reset link. (Merge note: item 44 lives on `feature/family-cloudflare` — whichever
     branch merges second resolves CLAUDE.md by ordering 44 before 45.)

46. **Correct-a-brand + the dev quick-login that drive-tested it (2026-08-13; both ✅ MERGED to master,
   PRs #9 and #8, CI green).** Two small features from one thread — Jordan hit a real gap on the live
   family box (a misread receipt brand was uncorrectable after confirmation), and fixing it re-exposed
   the friction that the v3 auth wall broke automatic UI drive-testing, so the second feature exists to
   fix the first's testing.
   - **Brand correction on Product Detail** (`feature/edit-purchase-brand`): Recent purchases gained a
     **Brand column** with an inline ✏️ pencil parallel to the quantity one, backed by
     `IPantryStore.SetPurchaseBrandAsync`. Brand is per-purchase and **cosmetic** — the product is
     brand-agnostic and the cadence pools across every brand (the "usual brand" hint + Brands-bought
     breakdown are all it feeds), so unlike the quantity correction it moves NO count, fires no signal,
     and doesn't touch the attestation clock. Blank folds to null (the same "unbranded" the grouping
     already treats whitespace as); the receipt line is left alone (audit copy), same as the quantity
     edit. The brand and quantity editors are **mutually exclusive** (each clears the other's row id).
     No chat tool — deliberately matching the quantity pencil, which has none (correcting a specific
     past purchase is a click-this-row act). 8 tests (5 persistence incl. the household-scoped
     non-overwrite check + 3 bUnit page), all mutation-checked; live-verified in a browser (typed a
     brand, saved, persisted across a fresh nav).
   - ⚠️ **`DevAuth` — a Development-only quick sign-in, `GET /dev/login`** (`feature/dev-quick-login`).
     Signs in a **passwordless** dev account in a sample-seeded "Dev Sandbox" household and redirects
     home, so a dev server is one navigation past the auth wall (which is what makes automatic
     drive-testing of authenticated pages work again). **THE safety property: it can never activate on
     any real deployment.** `DevAuth.IsEnabled(env, config)` = `env.IsDevelopment() &&
     config.GetValue<bool>("Dev:QuickLogin")` — a Production box (family / droplet / tailnet all run
     `ASPNETCORE_ENVIRONMENT=Production`) fails the first conjunct unconditionally, so
     `MapDevQuickLogin` maps nothing there no matter where the flag comes from. The flag lives in
     `appsettings.Development.json` (only loaded under Development anyway), the handler re-checks
     `IsEnabled`, and the account is passwordless so it has NO sign-in path outside `/dev/login` even
     if a dev `auth.db` ever reached a real box. The code ships everywhere ("supports it") but only a
     dev machine "uses it" — Jordan's exact spec. `DevAuthTests` pins the gate truth table (incl. the
     load-bearing `Production + flag = true → false` row); mutation-checked (`&&`→`||` fails exactly 4
     rows). The endpoint itself is live-verified, not unit-pinned (no WebApplicationFactory harness
     here). Reuses the tested paths: `CreateAsync` (passwordless) + `HouseholdService.CreateForAsync`
     in one transaction like registration, `SignInAsync` after the household exists (cookie carries
     the household claim), `DemoDataSeeder.SeedAsync` (best-effort, guarded to empty) via
     `ICurrentHousehold.UseFixed`.
   - Both independently reviewed clean (each fix's one nit taken: the brand tenancy test proving
     non-overwrite; the dev-seed catch letting `OperationCanceledException` propagate). **1495 green,
     0 warnings** on a non-incremental Release build of merged master (both features compose cleanly;
     read off that run, not the per-branch numbers — item 21).
   - **Family box operational note (2026-08-13):** `Auth:AllowRegistration` is now **true** on the
     family server (Jordan's call — he wanted new sign-ups). Flipped in the live `appsettings.json`
     (backup kept), took effect on a manual elevated restart, verified open. ⚠️ It's carried across
     publishes by `publish-family.ps1`, so it stays true until changed back. A fresh self-registration
     there creates a NEW empty household (not the shared pantry) — rejoining the family pantry is an
     invite code, which works regardless.

47. **v4.7 — In-app problem reporting (2026-08-13/14; ✅ MERGED via PR #11, with the dashboard
   copy-name button via PR #10 — both CI green; merged master reads 1550 green / 0 warnings on a
   non-incremental Release build, read off that run per item 21).**
   Jordan's ask ("bug reporting in app… easily maintainable"), sharpened in discussion to two signal
   sources feeding one admin surface: **machines report ERRORS, humans report WRONGNESS.**
   - **The error log lives in auth.db because errors are OPERATOR data** — no household owns one,
     none reaches export or "delete my data", no tenancy machinery to punch through, no household
     attribution in rows (matching the logging discipline: what users said stays out of the log).
     An `ILoggerProvider` captures every Error/Critical event — handled ones included; the house
     catch-log-and-say-so convention is exactly what feeds it — into a bounded channel
     (`ErrorLogSink`); `ErrorLogWriter` (BackgroundService) persists deduped by fingerprint
     (category + exception type + message TEMPLATE, so "product 12" and "product 99" are one row
     counted twice); `ErrorLogStore.MaxRows` (500) bounds the table, quietest rows trimmed first.
   - ⚠️ **Capture-path rules:** the provider never throws (a failure becomes a COUNTED drop,
     disclosed on the admin page — no silent caps), and never captures its own
     `ShelfAware.Web.Diagnostics` categories — the recursion break the writer logs through. The
     sink is **Wait-mode + TryWrite deliberately**: the one bounded-channel shape where a drop is
     observable to count (DropOldest/DropWrite discard inside the channel, invisibly).
   - ⚠️ **SQLite refuses `DateTimeOffset` in a SQL ORDER BY** — found here because every previously
     date-ordered query used `DateOnly`. Bug reports order by Id (insert order IS chronological);
     the bounded error table orders and trims client-side (and carries NO LastSeenAt index — it
     could serve nothing).
   - **`BugReport` is an ordinary household-owned pantry table**, walked through the full drill:
     filter + stamping, isolation, export, delete-my-data, CountAll, `AdditiveSchema.EnsureTable` +
     schema-parity on BOTH DB files (the auth-side EnsureTable is the first; `TableSchemaAsync`
     widened to `DbContext` for it). Households file and see their own on `/bugs`; the footer link
     carries the current page as a VISIBLE, editable `?from=` pre-fill — path only (the live
     walkthrough caught the full-URI shape compounding from= into itself on /bugs), and only an
     app-relative path is accepted, since a query string is attacker-writable.
   - **The admin is config-designated** (`Admin:Emails`; unset = the feature does not exist
     anywhere — no footer, no form, /admin refuses everyone: the Google-OAuth posture).
     ⚠️ **`AdminOptions.IsAdmin` is THE one predicate behind every gate** — the app's first
     authorization policy, its first policy-based `AuthorizeView`, the reader's check, and the
     component's own check. It reads `Identity.Name` because in this app the username IS the email
     (Register/ExternalLogin/DevAuth all pair them; `RequireUniqueEmail` pins it) and Identity puts
     no separate email claim in the cookie.
   - ⚠️ **`AdminReportReader` is the app's FIRST production `IgnoreQueryFilters`** — the pre-push
     gate's standing question, answered in advance: admin-gated INSIDE the service (defense in
     depth, and the layer a directly-rendered bUnit component can pin, since routing authorization
     doesn't apply there), AsNoTracking, read-only, and the sole reader of cross-household reports.
     Anything else wanting cross-household data makes its own case at review — don't reuse this.
   - **The viewer is read-only v1 deliberately** — "mark resolved" would be the app's first admin
     WRITE into foreign household data, a separate focused change if ever wanted. **[It was wanted:
     item 49 built exactly that change — the reader itself is still read-only.]** And the demo
     seeder deliberately seeds NO bug report: a seeded report would file fake noise into the
     operator's real inbox.
   - Dev quick-login's sandbox account doubles as the admin in `appsettings.Development.json`, so
     every surface is drive-testable one navigation past `/dev/login`.
   - **The independent gate (code + security) came back: security PASS** — the tenancy boundary,
     all three admin-gate layers, the username==email invariant (no path ever changes either
     post-registration), and every claimed number verified; the reviewer's own mutations (incl.
     one per gate METHOD, which the author's list hadn't covered) each killed exactly one test.
     Eight findings, all fixed the same session:
     - ⚠️ **F1, probed 1:1 — the category exclusion alone is NOT a complete recursion break.** A
       FAILING persist makes EF itself log at Error under `Microsoft.EntityFrameworkCore.
       Database.Command` — a category the capture must keep watching — so a persistently failing
       auth.db became a self-feeding busy loop. `ErrorLogSink.BeginPersist` (static AsyncLocal
       scope, checked in the provider's Log) now suppresses the pipeline's OWN persist flow only;
       the echo is skipped UNCOUNTED, deliberately — the writer already counts and logs that
       failure once. Pinned by a scope unit test and a real-broken-table integration test.
     - **F2 — bounds are server-side now**: /bugs clamps Body/PageUrl to the form's own 4000/300
       (maxlength is browser-enforced only), and `AdminReportReader.MaxReports` (500) caps the
       admin list, DISCLOSED on the page when hit. F3/F4: the Bugs page's two failure points give
       opposite advice (item 27's rule) and the initial load is self-catching. F6: the from= guard
       refuses `//host` and `/\` shapes. F7: a null entry in `Admin:Emails` can't throw inside the
       footer's AuthorizeView. F8: ErrorLogEntry's docstring owns that exception TEXT can carry
       household-derived strings (admin-only, never exported — acceptable, now stated).
     - ⚠️ **F5 became a real find: this runtime starts `ExecuteAsync` through a CANCELLABLE hop.**
       The new shutdown-drain test flaked 4/4 under the full parallel suite and 0/anything alone;
       diagnosis proved the loop task ended **Canceled without its body ever running** — a stop
       racing a just-started BackgroundService cancels work the pool hasn't dequeued yet. StopAsync
       completes the channel (drain), then SWEEPS anything still unread into the drop count, so
       even the never-ran edge loses nothing silently; the drain test waits for the loop's
       aliveness (first event landing) before stopping. ⚠️ Don't "simplify" that wait away — the
       race is the runtime's scheduling, not the test's imagination.
   - **The fix commit's own re-review (same reviewer, fresh skepticism, item 39's rule): NO
     regressions — the first fix pass of this arc's series it could not fault**, crediting the
     fixes being structural (a flow-local scope, drain-then-sweep, a shared clamp) rather than
     patched guards. Its cross-flow probe confirmed the AsyncLocal scope suppresses only the
     pipeline's own flow; the sweep cannot double-count (both consumers dequeue, an event reaches
     exactly one). Two PLAUSIBLE residuals, both confined to forced-timeout teardown (the process
     being killed mid-drain), are DOCUMENTED on StopAsync rather than fixed: the sweep's TryRead
     beside a still-live loop leans on the BCL bounded channel tolerating a second reader despite
     SingleReader, and the loop's one in-flight event dies uncounted.
   - **1542 tests green, 0 warnings** on a non-incremental Release build (master 1495; +47).
     Eighteen author mutations across six batches over the arc, plus seven reviewer mutations
     across the two gate rounds — every one failing exactly the tests it should and nothing else.
     **Live-verified end to end** (dev server on the alt port, sandbox household): the footer's
     from= follows navigation; a report filed on /bugs lands on /admin with reporter + household
     name; and the error pipeline proved itself on a REAL failure — a junk PNG upload hit Upload's
     extraction timeout, the fail log line was followed in the server log by the fingerprint
     UPDATE + INSERT into auth.db's ErrorLog, the row rendered on /admin, and a second junk upload
     made it **2× on one row** (dedupe, live). Zero console errors — the strict CSP holds.
   - **The dashboard copy-name branch** (`feature/dashboard-copy-name`, 3 commits, 1503 green on
     the branch): a
     📋 icon-btn per Running Low card copies the BARE item name (what pastes into a store search).
     Its independent review found the two PRE-EXISTING copy sites (grocery list, invite code) had
     no catch at all — a refused clipboard tore down their circuits — so
     **`ClipboardCopy.TryWriteAsync` (Web/Services) is THE clipboard posture**, all three sites
     converted in the same change per the top directive. ⚠️ And a `role="status"` region inserted
     WITH its text is not reliably announced — the dashboard and grocery list keep a PERSISTENT
     visually-hidden announcer mounted before any copy (Settings' multi-action note slot kept its
     page-wide pattern deliberately: converting one action's announcements piecemeal is the
     partial-conversion trap from the other direction).

48. **v4.8 — Snap a photo: camera capture + staged uploads (2026-08-14, branch `feature/snap-photo-upload`).**
   Born from Jordan's wife being unable to FIND her photos in the iOS picker — Files doesn't show the
   camera roll, and the receipt accept list grayed HEIC out of Files on top of it. (Her earlier
   "photos never upload" was almost certainly the pre-`e3fe589` blob:-CSP infinite spinner: the family
   box ran mid-July code from 7/15 until the 8/13 publish, so photo uploads hung its whole life while
   Jordan's PDFs worked. Diagnosed from the deployed binaries' dates, not the code.) The fix deletes
   the finding step: a 📷 Snap button on `/receipt` and `/pantry-photo` opens the camera itself —
   `capture="environment"` on a second InputFile behind a label-button, NOT getUserMedia, so
   `Permissions-Policy: camera=()` stands. Direct captures arrive as JPEG on iOS: the snap path never
   meets HEIC.
   - ⚠️ **Appending across snaps forces eager reads, and that is the whole design.** Re-activating a
     file input replaces the element's JS-side file map (`_blazorFilesById`), so an `IBrowserFile`
     held from the previous change event is DEAD by the second snap. Every picked file is read into
     memory inside its own change event; extraction and the census read touch no browser handle, so
     the old "InputFile must stay mounted while extracting" constraint died with it (a circuit blip
     mid-extract can no longer break a file read either).
   - **`PickedFileReader` (Web/Services) is the one classification of a picked file's read** —
     Loaded / Refused / Failed / TornDown / ConnectionLost — carrying the census teardown-vs-alive
     discipline (captured token, only the page's own token means teardown, never throws). Both
     pages' handlers are a loop over it plus page wording; each keeps a teardown-silent belt catch.
   - **Upload's image path goes through `IShelfPhotoLoader` now** — item 37's gap closed: one
     picked-photo→bytes definition, and the receipt IMAGE path is bUnit-testable for the first time
     (`StubPhotoLoader` moved to UiFakes and serves both test classes; it honors cancellation like
     the real loader, or the teardown tests pin nothing). The loader's refusal sentence went
     page-neutral ("Take or pick a picture instead"); Upload's accept widened to
     `image/*,application/pdf` (the census rule — never name image/heic); PDFs branch before the
     loader into a raw read under the same classification.
   - ⚠️ **`@key`-per-ingest on both file inputs is load-bearing, and untestable.** A browser fires no
     change event when the "same" file is re-picked, and every iOS camera capture is named image.jpg
     — without a fresh element per finished ingest, the second snap of a multi-page receipt is
     silently ignored. bUnit's fake upload can't reproduce the suppression, so this is
     review-verified only. The counter bumps in the ingest's finally, AFTER the reads, so no element
     is torn down under a live stream.
   - **Retention asymmetry, deliberate:** a failed census read KEEPS the staged photos ("try again"
     is one tap on the same bytes — a census has no audit copy to retry from), cleared only on
     success or Start over; the receipt page still clears on extraction either way, because its
     pending queue owns recovery and a second staged road to the same receipt would double-record.
     **Memory profile, accepted:** staging holds every picked file's bytes from selection to
     extract — the BATCH path's peak moved from one-file-at-a-time to all-at-once (typically
     ~1 MB/resized photo; adversarially MaxFiles × 25 MB with PDFs, the same ceiling the combined
     path always had).
     Read()'s catalog-overlap + finally-observe machinery came out with the photo loop it overlapped,
     as did its now-dead JSDisconnected/NotSupported clauses — PickedFileReader owns those cases at
     selection time.
   - **Upload gained the Extract double-run guard it never had** — a queued second click behind the
     `hidden` render could persist the same staged list twice (the exact class item 37's census
     Read guard closed) — plus ingest↔extract mutual exclusion and pageCts + Dispose. Per-file
     failures NAME the file; a bad file doesn't unstage its neighbours (the batch rule, applied at
     staging).
   - ⚠️ **A mutation exposed a vacuous test, resolved honestly:** the success-path `stagedPhotos = []`
     is shadowed by Reset() on every road back to the picker, so no markup can observe it — it stays
     as memory hygiene with a comment saying exactly that, and the surviving test pins the PAIR
     (deleting both clears fails it). Eight mutations total across both pages and the reader, each
     killing exactly its tests.
   - **The gate's fix pass (same day, 7 findings):** `RemoveStaged` clears the one-long-receipt
     tick when the staged list EMPTIES (appending keeps it — adding pages is the flagship flow; an
     empty list is the only fresh-start boundary left, and without the clear a stale tick silently
     merged the next unrelated pick into one receipt); the pending review queue renders in
     Phase.Error too (reads-at-selection made Error one wrong file away, and hiding the queue
     behind it read as lost receipts); a pick during a stuck ingest gets a busy note instead of a
     silent drop (item 41's class — and the note leaves with the ingest that made it true; dropping
     rather than queueing stays right, since a queued event's handles reference the replaced
     input); the batch failed-save advice stopped saying "try selecting it again" for a failure
     selection can't fix; the census cap message names the ✕ instead of a Start-over button that
     phase doesn't have; the Environment-gotchas stay-mounted bullet was rewritten (it contradicted
     this item 300 lines apart — the doc-drift class).
   - **1570 tests green (+20 total), 0 warnings** on a non-incremental Release build, read off the
     final run. Three fix-pass mutations, each killing exactly its tests. Live-verified on the dev
     server via `/dev/login`: real canvas photos through the real browser resize (blob: CSP
     included) on both pages, append + ✕ removal, a text file refused instantly by name beside a
     surviving good photo, dark mode, zero console errors. ⚠️ NOT yet verified on a real iPhone —
     the capture attribute and the same-name re-snap are exactly what only her device can prove;
     first thing to check after the next family publish.
   - **The pre-merge max-effort `/code-review` (2026-08-15): two findings, both fixed.** The two
     refusal paths in each page's OnFilesSelected (the >Max single-selection catch and the
     append-cap check) returned before the finally, so the refused pick stayed as the input
     element's VALUE — and by this item's own suppression premise, the cap refusal's documented
     recovery ("remove one with its ✕, snap again") re-produced image.jpg into the same element
     and fired nothing. Both paths bump pickerGeneration now (the busy-drop deliberately does
     NOT — the in-flight ingest owns the elements, and its comment says so); pinned per page and
     per path by component-instance identity (`Assert.NotSame` across the refusal — the observable
     half of a suppression bUnit can't reproduce), four mutations killing exactly one test each.
     And the loader-side wording pin covered only "isn't a photo" while its comment claimed the
     sentence couldn't drift — the advice tail the page fixtures carry verbatim is asserted too
     now (mutating the tail fails all four theory rows; before, it sailed through green). Security
     half of the gate: CLEAN — the branch adds no IgnoreQueryFilters, no endpoint, no settings
     key, no per-household disk write; staged bytes are memory-only and `capture` is not
     getUserMedia, so `Permissions-Policy: camera=()` stands. **1574 tests green (+4), 0
     warnings** on a non-incremental Release build, read off the final run.

49. **v4.9 — Resolve for bugs and errors: the app's first cross-household write (2026-08-14, branch
   `feature/resolve-reports`).** Item 47 shipped the admin viewer read-only, deferring "mark resolved"
   as its own focused change; Jordan called for it, for both halves. The halves are deliberately
   different problems:
   - **Errors aren't a tenancy matter at all** (ErrorLog is operator data in auth.db), and their
     resolution is **DERIVED, not stored**: `ErrorLogEntry.Resolved` = ResolvedAt is set AND
     LastSeenAt ≤ ResolvedAt. A resolved error that recurs re-enters the open list purely because
     `RecordAsync` bumped LastSeenAt — the capture pipeline (the recursion-guarded machinery item 47
     hardened) never learns the field exists, so a recurring error can never sit hidden behind a
     stale resolve. The open row wears "⚠ seen again after being resolved <date>" — recurrence is
     the news, and a plain "open" would undersell a row the admin already dealt with once.
     ⚠️ **The resolve stamps the LastSeenAt the admin was LOOKING AT, never the click's clock**
     (the gate's sharpest find): resolution means "handled through what I saw", so an occurrence
     from the render-to-click window — or one captured earlier but persisted later by the
     background writer — lands past the mark and keeps the row open. A now-stamp silently
     swallowed exactly those, which falsified the paragraph above; ResolvedAt's doc carries the
     rule, and both a service test and a page test pin the window.
   - **`ReportResolutionService` is the ONE cross-household write**, the mirror of the reader's one
     IgnoreQueryFilters read — and the tenancy guard is what FORCED the safe shape: EnforceHousehold
     (item 12) refuses tracked cross-household writes outright, so load-flip-SaveChanges was
     impossible by construction. The write is `IgnoreQueryFilters().Where(Id).ExecuteUpdateAsync`
     setting exactly ResolvedAt — no tracked entity, no SaveChanges, structurally unable to touch any
     other column — admin-gated inside the service (the same RequireAdmin shape and the same ONE
     `AdminOptions.IsAdmin` predicate the reader carries; the reader's "read-only by design" claim
     stays true because this class exists). Mutation-proven both ways: dropping IgnoreQueryFilters
     fails the foreign-write test (the filter silently scopes the WHERE to the admin's own household
     and the row reads "gone"), dropping the gate fails the refusal test.
   - **The reporter sees the resolve** — /bugs shows "✔ resolved <date>" on their own row (an
     ordinary scoped read), so filing a report stops being a one-way letterbox. /admin splits both
     tables into an open table (the to-do list) plus a collapsed "Resolved (N)" drawer with Reopen.
   - **`ResolvedAt` rides AdditiveSchema on BOTH files, each placed AFTER its table's EnsureTable**:
     a table the EnsureTable just created already carries the column (current model), and the
     EnsureColumn reaches deployments whose table predates it. ⚠️ This bullet originally claimed
     "the schema-parity tests cover both" — FALSE (item 21's class, caught by the gate): those
     tests DROP TABLE, so EnsureTable rebuilds with the column present and the ALTER branch — the
     path every live deployment takes — never ran. Two drop-COLUMN tests
     (`Adds_the_resolved_at_column_to_…`) now pin it against EF's own DDL, the
     quantity-columns shape; `ColumnTypesAsync` was widened to `DbContext` for the auth-side twin.
   - The action handler's advice splits by failure point (item 27's rule): a failed WRITE says
     "nothing changed — try again" (safe, the write is idempotent); a refresh failure after a
     successful write speaks through loadError's own sentence, so the two can never share advice. A
     row deleted or trimmed between render and click answers false — "isn't there any more" — rather
     than throwing.
   - **The gate's fix pass (same day; 15 findings, all addressed):** the `.field` unscoping had broken
     the SETTINGS page (checkbox rows stacked box-above-caption; two heading-labels bolded — the pages
     wear `.checkbox-field`, now unscoped itself, and plain labels; found by SIX independent review
     angles because the live check had only looked at /bugs) and the bug form's real layout problem
     was a level higher anyway: `.panel form` is a flex ROW by design, so the fields sat side by side
     shrink-wrapped — `form.stacked` is the column opt-in (the `.auth-card form` precedent), and
     `.field` controls got the `width:100%` half that never transferred. The dropdown's picked-page
     arm now ALLOWLISTS against SiteNav (a select's @bind takes whatever the circuit sends — the raw
     arm skipped the clamp entirely); the where selection resets on submit (a retained pick silently
     mis-filed the next report); the reader takes the cap OPEN-FIRST (resolved rows starved the to-do
     list and "Nothing open" could be a lie) with the disclosure reworded; ActAsync states a
     successful write independently of the refresh (RefreshAsync returns bool, clears the action
     slots so a manual Refresh retires stale alerts, and gate refusals are logged); the resolved
     drawers keep Who/Where and the full error detail; `@key` + per-row aria-labels + a persistent
     status announcer went onto the churning tables; `BugReport.Resolved` exists (three inline
     spellings collapsed) and both derived properties carry the house `.Ignore()`; ✔ became ✓ and
     resolve stamps carry the year. **Residuals, accepted and stated:** focus is not restored after a
     row's action (the announcer covers the silence; restoring focus needs per-section FocusAsync
     plumbing); `ErrorLogStore.SetResolvedAsync` stays gate-by-convention-with-tripwire (a store-level
     gate would put a circuit-scoped AuthenticationStateProvider inside a singleton — a captive
     dependency); and the SiteNav "both or neither" claim's nav half is still untestable (no harness
     renders MainLayout) — `SiteNavTests` pins the href↔route half instead.
   - **The /bugs form got its polish pass in the same arc** (Jordan's live feedback): `class="field"`
     had styling only under `.ai-settings .field`, so the bug form's labels sat BESIDE their boxes —
     the `.linkish` undefined-class shape again; the rule is unscoped `.field` now (label stacked
     above, 34rem cap), serving Settings and Bugs from one definition. And "Where" is a DROPDOWN
     built from **`SiteNav` (Components/Layout) — THE page list, rendered by the header nav and the
     dropdown both**, so a new page appears in both or neither. A "Somewhere else…" escape hatch
     keeps the free-text path: a footer-link `from=` matching a nav page preselects it, while a
     more specific path (/product/12) lands VERBATIM in the escape box — the admin wants the exact
     page, and the footer's specificity must survive the dropdown. ⚠️ Razor reads an inline
     `@page.` as the @page DIRECTIVE (the lowercase-`<text>` reserved-word class) — loop variables
     over SiteNav must not be named `page`.
   - **1575 tests green (+25 total on the branch), 0 warnings** on a non-incremental Release build,
     read off the final run. Twelve mutations across the arc and its fix pass (the fix pass's five:
     the now-stamp, the cap ordering, the allowlist, the where-reset, and a second SetProperty that
     the REFLECTION pin — not a named-field list — caught widening the cross-household write), each
     killing exactly its tests. Live-verified on the dev sandbox across both passes: resolve/reopen
     round trips, the /bugs chip, the recurrence flow — and after the fix pass, /settings measured
     restored (checkboxes inline beside their captions at weight 400) and the bug form measured as a
     genuinely stacked column, both fields at the full 34rem. ⚠️ The first live check verified only
     labels-above-boxes on /bugs and missed both the Settings breakage and the flex-row fields —
     per-element measurements are not a layout check; measure the CONTAINER's axis too. (Branch
     note: independent of `feature/snap-photo-upload` — whichever merges second resolves CLAUDE.md
     by ordering item 48 before 49.)
   - **A further max-effort `/code-review` over the finished branch (2026-08-15): eight findings,
     none serious — five fixed, three skipped with reasons.** Fixed: the recurred note's
     `class="error small"` was the THIRD undefined-class instance (`.linkish`, `.field`, now
     `.small`) — and the fix-time grep found the same latent bug at FIVE pre-existing sites
     (Upload ×2, PantryPhoto ×2, and the cook-along reply's bare `class="small"`), so the fix is
     one unscoped `.small` rule replacing `.muted.small`, serving every consumer. ⚠️ It has equal
     specificity with `.muted` and must stay AFTER it in the sheet — the old two-class rule won on
     specificity, this one wins on order (live-probed: `small`, `error small` and `muted small`
     all compute 0.8rem; bare `.muted`/`.error` keep 0.9rem/1rem — that pair staying put IS the
     regression check). The cap disclosure says only "more may exist beyond this
     cap" now — "older resolved reports" went false the moment OPEN reports alone exceed the cap
     (opens starve opens with the copy blaming resolved; naming WHICH rows are missing can't be
     made true in every case, so the copy says less — item 40's copy lesson). `MaxReports`' doc no
     longer claims "(newest first)" (item 21's class, seventeen lines above the OrderBy that
     contradicted it). `ErrorLogStore.SetResolvedAsync` dropped its CancellationToken — the
     uncancellable-write posture is pinned by SIGNATURE at both layers now, not just the
     service's. And error Reopen — the one unpinned half — got its test (resolve → reopen(null) →
     open again), mutation-checked to a clean kill: `resolvedAt ?? Now` failed exactly the new
     test and nothing else, which also proves the gap was real. **Skipped, deliberately:** the
     duplicated RequireAdmin ceremony (the predicate itself already has its one definition), the
     open-vs-drawer row markup (refactoring live-verified markup buys drift protection an
     admin-only page doesn't need yet), and the top-of-page failure message (adjacent to the
     accepted focus residual; the cheap fix — render the same slot per section — is known if it
     bites). **1576 tests green, 0 warnings** on a non-incremental Release build, read off the
     final run.

Mid-session polish (committed): **safe-side rounding** — predicted run-out interval
floors (due a touch early), buy-quantity ceils for whole-unit items (no more "1.5"
on the list; weight items stay fractional); **out-now shows "due today"** — an active
OutNow sets the effective due date to the outage date so the card no longer says
"Overdue" next to "due in 21 days".

Deferred / backlog: **the Phase-5 cloud deploy is LIVE — a DigitalOcean droplet, not Azure**
(Jordan's calls: target 2026-08-09, deployed end to end 2026-08-11) via the committed kit —
`docs/deploy-droplet.md` + `deploy/` (systemd unit, Caddyfile, env template, droplet-side
`install.sh`, and `deploy.ps1` to publish/ship from Windows). Still pending: `docs/accuracy.png`
(the README's one remaining TODO, line ~190). NOT pending despite what this note long claimed:
`docs/demo.gif` has existed since 2026-07-12 (`5f34b24`, which also deleted its storyboard per that
file's own lifecycle note) — but it was captured before v3.5+, so it shows none of variety /
expiration / Reports / the counting arc / the census / the tour; re-recording is optional polish
and needs a NEW capture plan first (the old storyboard is gone). The README live-demo link points
at https://demo.shelfaware.net. **Deploy gotchas — timezone and
locale, same root:** every "today" in the app (purchases, signals, predictions) is server-local
`DateTime.Today`/`DateTimeOffset.Now`, deliberately consistent, and every price formats on the
server's culture — so a box with no timezone/locale set files evening purchases on tomorrow's
date and renders `¤3.99` (invariant culture; a systemd service starts with NO `LANG` — found on
the first live deploy). Set the droplet's timezone (`timedatectl set-timezone`, or `TZ` in the
service env) and keep `LANG` in the env file — runbook step 2 covers both.
Also backlog: **CSV history importer — PARKED** (Walmart won't export to Jordan's state; needs another
itemized source); a tiny "dapper blob" mascot for the header; a per-size Trends price chart.
(Shipped since this note: the double-scroll fix; the **two-stream cadence model** — rebuy rhythm +
burn rate, hybrid, restock is status-only (§6); and the whole **production-hardening pass** —
logging, the SQLite CVE patch, the `IChatClient` migration, and faked-client tests.)

## Voice: the built-in cook-along (v3.3, branch `feature/voice-engine`)

**The reader is ours; the ElevenLabs agent is an alternative.** `Recipes.razor`'s split button leads with
the built-in hands-free reader (`RecipeReadAloud` with `HandsFree="true"`); the caret holds "Read it to me"
(no mic) and "Live agent" (the EL realtime agent — only when `ElevenLabs:AgentId` is set, billed per minute,
kept because interrupting mid-sentence is the one thing our loop can't do). `read_recipe` lands in ours.
No settings toggle — the caret IS the choice, made per recipe. The agent's connect failure falls back to
the built-in reader.

- **`SpeechText` (Core) spells text out before TTS.** Not a nicety: ElevenLabs disable normalization on
  Flash v2.5 for latency and gate `apply_text_normalization` behind Enterprise, and their own docs show
  Flash reading "$1,000,000" as "one thousand thousand dollars". On our plan, doing it ourselves is the
  ONLY option. Gated by `ElevenLabs:NormalizeText`. It deliberately won't guess: "2 C flour" stays cups,
  not Celsius. **`SpeechText.Version` rides in the TTS fingerprint — bump it when the rules change** or
  cached clips keep yesterday's pronunciation.
- **Narration streams.** `readaloud.js` plays the intro while the steps synthesize behind it and append
  as they land; when playback outruns synthesis the player PARKS on `wantIndex` rather than mistaking an
  empty queue for the end of the recipe. `load(..., auto)` picks the mode: the button reader runs on,
  hands-free stops after each step and calls `OnStepFinished` so .NET can listen.
- **`CachingTextToSpeech` (Web/Services) decorates `ITextToSpeech`** — content-addressed, under
  `app-data/tts-cache`, keyed on text + neighbouring segments (they change the audio) +
  `ITextToSpeech.OutputFingerprint`. **A cache hit needs no API key**, which is what lets seeded/demo
  recipes talk for a keyless visitor. Registered via `SpeechRegistration.AddSpeech` so a test can prove
  nothing bypasses it. Bounded by `Speech:CacheMegabytes` (default 256), trimmed at startup.
- **`CookAlongCommands` (Core) is the fast path, NOT a gate.** Whole-utterance matching (same discipline
  as `VoiceCommands.IsStop`) resolves next/back/repeat/step N/start over/hold/stop for free. Anything it
  misses goes to `IPantryChat` — with the recipe as `screenContext` — which can ANSWER or MOVE us
  (`go_to_step`). That's deliberate: before `go_to_step` a grammar miss was *wrong*, so the phrase list
  had to enumerate every way a human says "next" through a cough. Now it's just slower. **Don't
  re-tighten the grammar into a gate.**
- **Half-duplex on purpose.** We listen BETWEEN steps only. Listening over our own voice needs echo
  cancellation good enough to hear "stop" under the voice saying "stop"; a step boundary is where a cook
  actually talks. Cost: no mid-sentence interruption (that's what the Live agent is for). Consequence:
  **"hold on" can't pause anything** — by the time you can say it the step has ended and the reader is
  already waiting. Its job is to stop us reacting to the room (no brain calls while held).
- **`pause`/`resume` must ignore an ENDED clip.** An ended element reports `paused === true` and
  `play()`ing one rewinds it — which re-read the step every time Jordan held. `resume()` returns whether
  anything actually resumed, because "I'm back" with nothing to resume must keep LISTENING, not hand off
  to a playback that will never call back.
- **`ListeningSettings` (Core) + the Settings calibration wizard.** The browser measures (`measureFloor`,
  `measureUtterance`); Core decides. The gate sits at the GEOMETRIC mean of room and voice (loudness is a
  ratio scale). Calibration listens with a 2.5s end-silence — a shorter one couldn't observe a pause it
  would then cut off, i.e. it would confirm its own guess. **Per DEVICE**, own localStorage key
  (`shelfaware.listening`, NOT `shelfaware.ai` — that store has a session-only mode and a calibration
  isn't a secret). A run that heard nobody changes nothing and says so.
- **Scribe gotchas (both cost real bugs):** `tag_audio_events` defaults TRUE and tags events into the
  TEXT ("Next (coughing)") — we turn it off AND strip annotations in `Utterance`; and a clean one-word
  "Next." comes back with `language_probability` 0.33, so `ElevenLabs:SpeechLanguage` (default `eng`)
  names the language rather than letting it guess.
- **`VoiceCoordinator.StandDownRequested`** is the mirror of `ResumeRequested`: there's one microphone,
  and `read_recipe`'s `HandsOff` only covered the agent STARTING a reader. This covers a user opening one
  while the roaming agent is already listening. The agent stands down but keeps its conversation.
- **Privacy:** the reader logs what it RESOLVED at Information but what it HEARD only at Debug — a
  microphone in someone's kitchen shouldn't record their speech to disk on a real deployment.
  Development turns it on for `ShelfAware.Web.Components.RecipeReadAloud`.
- **Open:** an intermittent bug where jumping to a step left "next" advancing from the old index, then
  wouldn't reproduce. Every static path says it can't happen (the player was proven correct in a browser),
  so it's timing. The logging above exists to catch it.

## Tags & Recipes (feature arc beyond the original spec)

**Two-layer categories** — one primary store-aisle `Category` (enum, unchanged, drives
grocery-list order) PLUS free-form `ProductTag`s (a many-per-product child table; `Product.Tags`).
The `category` was re-framed in the extraction prompt to STORE AISLE (first-aid→PersonalCare,
canned/condiment/shelf-stable→Pantry, cleaners/paper→Household); brand-defined items keep their
brand (the Velveeta over-strip fix). **Two-stage tag dedup:** plain-code
`TagVocabulary.FindNearDuplicate` (near-dup guard, Core, unit-tested) → then, only if that finds
nothing, `ITagAdvisor.FindSynonymAsync` (`AnthropicTagAdvisor`, Haiku synonym check, **fails open**).
Extraction is fed the **live tag vocabulary** (seed ∪ stored) via `ExtractAsync(…, knownTags)` so
the model reuses tags instead of coining near-dupes (dedup-at-source). UI: per-line tag editor on
Upload review (chips + datalist), tag chips on Product Detail linking to `/products?tag=`, and a
clickable **tag cloud** on Products that filters the grid (deep-linkable `[SupplyParameterFromQuery]
?tag=`) + per-row mini chips.

**Recipes (`/recipes`)** — an inventory-aware recipe helper (P1 `ff1fd83`, P2 `612fcbd`).
`IRecipeAdvisor`/`AnthropicRecipeAdvisor` (structured output, ChatModel/Haiku) takes an NL request
("what can I make?"), reasons over on-hand products (tracked & not-Overdue) and hard-excludes a
persistent **won't-eat** list (`ExcludedFood`). Suggestions list main ingredients vs. seasonings
separately and are saveable. **Key learning:** the LLM can't self-report availability, so the advisor
returns a grounded `matched_product` per ingredient (exact on-hand product name or null), captured
**once at save time** and persisted on `RecipeIngredient.MatchedProduct`. Makeability = **plain-code**
check that all MAIN ingredients' matched product is currently on-hand ("Ready to make"/"Missing items"
badges). Also: **"Ate it"** (`Recipe.TimesEaten`), **"Pick for me"** (random from saved + eaten +
makeable), and **"Add missing to list"** → the new `GroceryExtra` **Extras** section on `/list` (which
also filled a real gap — the list had no manual-add before). A label-check disclaimer is shown (not
allergy-safe medical advice). Entities: `ExcludedFood`, `Recipe`, `RecipeIngredient(IsMain,
MatchedProduct)`, `GroceryExtra`.

**Makeability by food family (v2.2).** Recipes stay SPECIFIC ("chicken breast", real cook times); the
flexibility lives on products. Each product has an **"Also works as"** list (`ProductSubstitute` child
rows, `Product.Substitutes`) — the recipe ingredients it can stand in for ("Chicken Breast Tenderloins"
also works as "chicken breast", "chicken cutlet"). `IngredientMatcher` (Core, unit-tested, replaces the
old exact-`MatchedProduct` check) covers a main ingredient when its core words (only trivial modifiers —
fresh/frozen/boneless/size/unit — stripped; cut/form words KEPT) appear in an on-hand product's **name OR
a substitute phrase** — so tenderloins cover "chicken breast" but "Whole Chicken" and "Chicken Broth" do
NOT. Recipe on-hand is **edible only** (excludes Household/PetCare/PersonalCare, so "Chicken Jerky Dog
Treats" can't count as chicken). Substitutes are **AI-seeded** (`IProductSubstituteAdvisor` →
`AnthropicProductSubstituteAdvisor`, Haiku, fails soft) + user-curated: an ✨ Suggest button on Product
Detail, and the **`suggest_substitutes` chat/voice tool** (`IPantryStore.AddSubstitutesAsync`) so the
assistant fills them in from anywhere. `ProductSubstitutes` is an additive table (CREATE TABLE IF NOT
EXISTS in Program.cs for existing DBs).

**Adapt to what you have (v2.2).** A saved recipe can be rewritten to use on-hand ingredients: the AI swaps
missing main(s) for ones you have and **rewrites the steps + cook times** (thighs cook longer than breast),
saved as a **variant** (`Recipe.ParentRecipeId`, additive column) grouped under the original on the Recipes
page. On-demand only (no AI calls on load). One orchestration path — `IRecipeAdapter` (Core) →
`RecipeAdapter` (Web, scoped; loads the recipe + on-hand + excluded, calls `IRecipeAdvisor.AdaptAsync`,
saves the variant) — drives the "🔀 Adapt to what I have" button, the **`adapt_recipe` chat/voice tool**,
AND the per-ingredient bubble cloud. Adapt prompt is `recipe-adapt-system.txt`. On-hand = the shared
`PantryOnHand.EdibleInStock` (Core; `CategoryExtensions.IsEdible` + not-overdue). **Robustness:** re-adapting
**dedupes by main-ingredient content signature** (not the AI's title) so it updates in place; variants are
saved only when valid, and the adapter logs + re-throws cancellation (no swallowed errors). **As of
2026-07-12:** the advisor receives each on-hand product's also-works-as list (item 11), and adapting a
VARIANT is allowed — it re-roots under the original (see item 11) instead of refusing.

**Bubble-cloud ingredient picker (v2.2).** Each main ingredient on a saved recipe (originals AND, since
2026-07-12, variants) has a **⇄ swap** that opens a cloud of interchangeable forms
(`IIngredientAlternativesAdvisor`, Haiku; generated once and **cached** on
`RecipeIngredient.AlternativesJson`), colored green/red via `IngredientMatcher`. Since 2026-07-12 the cloud
is `SwapCloud.Merge(curated, generated)` — the user's own stand-in products lead, AI forms dedupe behind
them (item 11). Clicking a bubble runs a **targeted adapt** — a typed
`IngredientSwap(IngredientName, ChosenForm)` the adapter turns
into the prompt preference AND **guards**: if the model ignores the pick, `IngredientMatcher.IsMentionedIn`
catches it and the adapt is rejected (retry) rather than saving a mislabeled variant.

## Data model: brand-agnostic products, size as metadata (final, 2026-06-28)

A product is a brand-agnostic **item**; brand and size are tracked **per purchase**, so
the same item bought across brands/sizes rolls up into one product.

- `Product.Name` is the brand-stripped item ("Whole Milk", "Chicken Wrapped Cod Skin Dog
  Treats"). `Brand`, `Size`, and (since v3.5) `Variety` (all `string?`) live on `ReceiptLine`
  **and** `PurchaseEvent`; `ConfirmAll` copies the reviewed line's brand+size+variety onto both.
  Matching (ProductMatcher + aliases) keys on the item name only — so different brands/sizes/
  flavors merge, and the old store-brand collision is moot.
- Extraction prompt drives `normalized_name`=item, `brand`=brand, `size`=size. **Gotcha:
  keep the item's DISTINGUISHING words (variety/cut/flavor/form); strip ONLY brand + size.**
  An early prompt over-shortened "…Chicken Jerky Dog Treats" to bare "Dog Treats" and merged
  distinct products — the prompt now forbids bare-category names.
- **The DOMINANT size drives the prediction.** `Product.Size` was tried as identity then
  reversed (Jordan buys milk as half-gallon OR gallon at random; identity-by-size either made
  two products or split trivial sizes). `ReplenishmentPredictor` predicts cadence from the
  dominant size's purchases (most-bought; ties → most recent) and exposes `RecommendedSize` —
  one cadence, one recommended size, never "buy a gallon AND a half-gallon". **HYBRID:** use the
  dominant size's purchases only when that size has ≥2 buys, else fall back to ALL purchases (so a
  mixed-size item still predicts). NO unit arithmetic ("1 gal" = 2×"64 fl oz") — emergent approach
  chosen deliberately; trivial-vs-meaningful size (10.6 vs 11 oz) is undistinguished, revisit only
  if it bites. "bought N×" counts ALL purchases. UI: usual-brand + recommended-size hints across
  Products grid, Grocery List, dashboard cards, Product Detail.
- After the clean re-import: 56 products / 83 purchases / 73 branded; cross-brand rollups
  verified (bread = Nature's Own + Sara Lee, cod-skin dog treats = ASMPET + Pawmate); unbranded
  produce/meat (e.g. "93% Lean Ground Beef") stay null.
- **`Product.Tags`** (child `ProductTag` rows) is the descriptive second category layer added
  later — see the Tags & Recipes section above. The recipe feature adds `ExcludedFood`, `Recipe`,
  `RecipeIngredient`, and `GroceryExtra`. All are in the DbContext; `EnsureCreated` builds them on a
  fresh DB, but existing dev DBs were migrated in place via the dbfix ALTER-TABLE pattern below.

## Decisions & deviations from the spec

- **Spec enum "ShelfAweed"** is a find/replace artifact (Restock→ShelfAware) — implemented as
  `SignalKind.Restocked`. Read §6/§7's "ShelfAweed" as "Restocked".
- **`ShelfAware.slnx`** not `.sln` — the .NET 10 CLI default.
- **Data dir is `app-data/`** (not `data/` — collides with the `Data/` source folder on
  case-insensitive FS). Resolves to `src/ShelfAware.Web/app-data/` locally (ContentRootPath);
  a cloud box points the `DataDir` config key somewhere real (the droplet runbook uses
  `/var/lib/shelfaware`).
- **Global InteractiveServer render mode (v2.2).** `App.razor` sets `@rendermode="InteractiveServer"` on
  `<Routes>` and `<HeadOutlet>`; pages **must not** re-declare a render mode (a page can't set one an
  ancestor already set — it throws). This replaced per-page `@rendermode` directives so the layout, and
  the `VoiceAgent` it hosts, is interactive and **persists across navigation** (the persistent listening
  agent needs this; a static layout re-creates its interactive islands on every page change). No static-SSR
  benefit was lost — every page was already interactive. Cross-component coordination goes through a
  **scoped** `VoiceCoordinator` (Web/Services): `PantryChanged` (a voice data change refreshes the page on
  screen, replacing the old per-page `OnApplied`), `ResumeRequested` ("Back to assistant" resumes the
  agent), and `ScreenContext` (the page publishes what's on screen for positional references).
- **Official Anthropic C# SDK (`Anthropic` NuGet) used directly** behind `IReceiptExtractor` /
  `IPantryChat`, not wrapped in `Microsoft.Extensions.AI` `IChatClient` (§2) and not Semantic
  Kernel (§7) — the interface seam already gives swappability + testability; revisit if a second
  provider appears. Chat = manual tool-call loop over `Messages.Create` (§7 Option B).
- **Structured outputs** (`OutputConfig`/`JsonOutputFormat`) enforce the §5 schema server-side,
  plus the spec's validate-and-retry-once in C#. Schema omits `minimum`/`maximum` on confidence
  (unsupported in strict mode) — clamped in code. Extraction model pinned `claude-haiku-4-5-20251001`.
- **`IPantryStore` (Core/Chat) is the chat data port** — Core defines it, Web implements
  `EfPantryStore`, so the chat layer touches no EF. Fuzzy name→product resolution in
  `ProductMatcher` (Core, unit-tested): exact → substring → IDF-weighted token-overlap ≥ 0.5
  (IDF so brand/qualifier words like "great","value" carry ~0 weight and don't false-merge).
- **Chat has grown well beyond §7's tool set.** Live tools (matches `MakeTool` calls in
  `AnthropicPantryChat` — this list had drifted): `record_signal`, `add_purchase`, `query_status`,
  `set_tracking` (start/stop tracking → `IPantryStore.SetTrackingAsync`), `set_expiration`,
  `create_product`, `add_tags`, `suggest_substitutes`, `adapt_recipe`, `add_recipe_to_list`,
  `open_page`, `read_recipe`, and `go_to_step`. (`import_receipts` was removed with the
  folder-import feature, item 17 — "import my receipts" now opens the Upload page via `open_page`.)
  The last three don't touch data — they write into a mutable
  `NavigationTarget` slot that rides out on `ChatResult` (`NavigateTo` / `HandsOff` / `StepTarget`) for
  the UI to carry out. **`go_to_step` is the safety net under the cook-along grammar** (see Voice below):
  it moves the hands-free reader, which is what lets that grammar stay conservative.
- **Two new AI interfaces beyond §2/§7:** `ITagAdvisor` (Core/Tagging → `AnthropicTagAdvisor`) and
  `IRecipeAdvisor` (Core/Recipes → `AnthropicRecipeAdvisor`), both registered in DI. Same interface-
  seam pattern (Core defines, Llm implements). Tag advisor uses `ExtractionModel`, recipe advisor
  uses `ChatModel` (both Haiku).
- **Prediction extras beyond §6.7:** `PredictionResult.Pinned` (OutNow forces Overdue + sorts to
  top); `SignalNote` (user's statement, surfaced separately from `Basis`); `RecommendedSize`;
  `RebuyIntervalDays` + `BurnRateDays` (the two-stream rhythms). A Restocked signal is **status-only**
  — it clears an earlier OutNow and re-anchors the due date (a "last stock-back"), but does NOT feed
  either cadence rhythm; only real purchases do (§6 two-stream model).
- **`ShoppingEstimator` (Core/Shopping) is pure + unit-tested** — combines the price-free Core
  prediction with median quantity and a unit price passed IN by Web (avg of confirmed
  `ReceiptLine.UnitPrice` for the recommended size), so Core stays EF-free and the engine stays
  pure timing stats. Exposes `ProductEstimate` (incl. `RecommendedSize`, `UsualBrand`).
- **LLM-assisted product matching (extends §4):** extraction also receives the existing product
  list and returns a per-line `existing_product` → `ExtractedLine.SuggestedProductName`. Upload
  review pre-fills by trust order: learned alias → model suggestion → `ProductMatcher` → create new.
- **Purchase date from the receipt, not upload date** — review screen has an editable "Purchase
  date" (defaults to extracted date, or today with a warning), written to every PurchaseEvent so a
  batch of old receipts keeps accurate intervals.

## Environment & workflow gotchas

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
