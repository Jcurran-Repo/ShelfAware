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
+ unit tests; Evals excluded — needs a live key). **200 green xUnit tests across three
projects** (pure engine · faked-IChatClient AI layer · persistence on in-memory SQLite).

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
     `StockUpFactor` (extend-only, ≤ 3×) stretches the due date after a bigger-than-usual buy;
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
   - **`Receipts:AllowedRoot`** (unset = today's behaviour, so the self-host is unchanged) confines the receipt
     folder. Unvalidated, it's an arbitrary-path read of every image/PDF the server can see. `ReceiptFolderPolicy`
     is asked by Settings (friendly refusal) **and** by the inbox (the real boundary — a stored setting can
     outlive the rules it was written under). GetFullPath first; trailing-separator compare so `<root>-old`
     isn't "inside" `<root>`; UNC refused when confined.
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
- **Chat has grown well beyond §7's tool set.** Live tools: `record_signal`, `add_purchase`,
  `query_status`, `create_product`, `set_tracking` (start/stop tracking → `IPantryStore.SetTrackingAsync`),
  `suggest_substitutes`, `adapt_recipe`, `add_missing_to_list`, `import_receipts`, `open_page`,
  `read_recipe`, and `go_to_step`. The last three don't touch data — they write into a mutable
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
