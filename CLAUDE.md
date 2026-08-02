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
| 5 — Azure deploy + README | ◑ README ✅ done + pushed (`4757839`); **Azure still deferred** (pending Jordan's account) |

Everything below is built, verified live, committed, and **pushed** (master, through the 2026-07-05
v2.3 full-site-audit + BYOK arc — see item 8 below and timeline.md).
Beyond the spec's 3 pages, the app now has Dashboard (`/`), Upload (`/receipt`),
Products (`/products`), Grocery List (`/list`, by aisle + copy/print + a manual **Extras**
section), Trends (`/trends`, price tickers + spend forecast — page component is
`SpendInsight.razor`), Product Detail (`/product/{id}`, rhythm + price-history chart),
Accuracy (`/accuracy`, renders `eval-results.json`), **Recipes (`/recipes`)**, and
Receipts (`/receipts`, added 7/12 — per-receipt line-item totals via `ReceiptTotals`, Core).
Extensive polish stretch done: design-system + dark mode (CSS vars) + site-wide a11y
pass; LLM-assisted product matching in extraction; GitHub Actions CI (restore + build
+ unit tests; Evals excluded — needs a live key). **1210 green xUnit tests across four
projects** (pure engine · faked-IChatClient AI layer · persistence on in-memory SQLite ·
bUnit pages/components — see item 31).

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
   `docs/demo.gif`, `docs/accuracy.png`.
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
   only enumerate which households exist; both API endpoints scope to the caller's claim; every tenant table
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

Mid-session polish (committed): **safe-side rounding** — predicted run-out interval
floors (due a touch early), buy-quantity ceils for whole-unit items (no more "1.5"
on the list; weight items stay fractional); **out-now shows "due today"** — an active
OutNow sets the effective due date to the outage date so the card no longer says
"Overdue" next to "due in 21 days".

Deferred / backlog: **Azure App Service deploy** (Phase 5 — then swap the README live-demo
URL + add `docs/demo.gif` + `docs/accuracy.png`). **Deploy gotcha — timezone:** every "today"
in the app (purchases, signals, predictions) is server-local `DateTime.Today`/`DateTimeOffset.Now`,
deliberately consistent; on Azure (UTC) an evening "Bought today" would land on tomorrow's date, so
set the App Service `WEBSITE_TIME_ZONE` (Linux: `TZ`) app setting to Jordan's timezone at deploy.
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
  Azure uses `/home/data` via the `DataDir` config key.
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
- **Blazor `<InputFile>` must stay mounted while `IBrowserFile` streams read** — unmounting (e.g.
  switching to a spinner) breaks reads with `_blazorFilesById` null. Upload.razor hides it with
  `hidden`. Don't "simplify" this.
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
