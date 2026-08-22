# GraphQL API — implementation plan

**Status:** ✅ IMPLEMENTED (2026-08-22, branch `feature/graphql-api`) — this is the historical
plan; the as-built API is documented in [`graphql-api.md`](graphql-api.md). All seven phases below
shipped; the arc followed them in order.

A read-only GraphQL API over the pantry
domain, so a household can query its own data with a token. Portfolio/showcase feature;
there is no real user demand for it, so it must ship as a **complete vertical slice** or
not at all — a half-built API reads worse than none.

**Two locked decisions (Jordan, 2026-08-18):**
- **Read-only.** Queries only. No `Mutation` type exists, so external clients can never
  write into a pantry. Read-only is enforced by *absence*, not by a guard.
- **Dev-only until done — as a deployment state, not an architectural one.** The design
  targets the strict production environment throughout; the *only* thing gated to
  Development is the embedded GraphQL IDE. Exposure is a single config flag (below).

**Suggested branch:** `feature/graphql-api`. Touches auth and adds a data-exposing
endpoint, so it ends on the `/pre-push` gate (code + security review) before any merge.
This is a mid-size arc — the weight is the auth/tenancy slice (phases 1–2), not GraphQL.

---

## 1. The key architectural insight — reuse the tenancy machinery, don't rebuild it

A plain HTTP request (no Blazor circuit) already resolves its household with **no new
code**: `CurrentHousehold` reads a `shelfaware:household` claim off `HttpContext.User`
([`CurrentHousehold.cs:43`](../src/ShelfAware.Web/Data/CurrentHousehold.cs), claim const
`HouseholdClaimsPrincipalFactory.HouseholdClaim = "shelfaware:household"` at
[`HouseholdClaimsPrincipalFactory.cs:14`](../src/ShelfAware.Web/Auth/HouseholdClaimsPrincipalFactory.cs)),
with **no DB round-trip**, and `IHouseholdDbFactory` stamps that id onto every pantry
`DbContext` ([`HouseholdDbFactory.cs:18`](../src/ShelfAware.Web/Data/HouseholdDbFactory.cs)).

**So the entire tenancy story is: the token-auth handler builds a principal carrying that
same claim, and every resolver is scoped for free through the exact code the UI uses.**
The token→household lookup *is* the authentication step; the existing global query filter
does the rest.

⚠️ **Security headline for the eventual review: no new `IgnoreQueryFilters`, no bypass.**
A token for household A produces a `DbContext` physically stamped A; a query for B's data
cannot even be expressed. The two existing production `IgnoreQueryFilters` sites
(`AdminReportReader` + its write mirror) are admin-gated and explicitly "don't reuse" — we
don't. Each token scopes to exactly one household, so cross-household read is never wanted.

### Request flow

```
Authorization: Bearer sa_xxx
  → ApiToken auth scheme: SHA-256 the token → look up in auth.db → not revoked / not expired
  → build ClaimsPrincipal with claim "shelfaware:household" = token.HouseholdId
  → CurrentHousehold reads that claim (existing resolution step 2)
  → IHouseholdDbFactory stamps DbContext.HouseholdId (existing)
  → resolver reads household-scoped data; runs the engine for computed fields
  → GraphQL JSON
```

Missing/invalid/revoked/expired token → **401 from the ApiToken scheme** (not the cookie's
HTML login redirect), because the endpoint requires that scheme specifically.

---

## 2. Production-compatibility decisions

The design is prod-targeted throughout. These are the choices that "compatible with
production" locks in — mostly a matter of *not* taking dev-only shortcuts:

- ⚠️ **Exposure = a pure config flag `GraphQL:Enabled` (default `false`).** NOT an
  `env.IsDevelopment()` lock. (Contrast `DevAuth`, which hard-codes the environment
  *because* it's a passwordless backdoor that must never run in prod — see item 46 in
  CLAUDE.md. GraphQL is meant to reach prod, so hard-coding the environment would make
  "enable in prod" a code change + re-review instead of a config flip.) Today the flag is
  set only in `appsettings.Development.json`; when the slice is done, flipping it on the
  droplet/family box is the whole deployment. `AddGraphQLServer`/`MapGraphQL`,
  the scheme, and the Settings UI all gate on this one flag.
- **The API is strict-CSP-clean in production, with no CSP change.** The GraphQL POST
  returns JSON to a same-origin caller (`connect-src 'self'` already covers it,
  [`Program.cs:416`](../src/ShelfAware.Web/Program.cs)); a non-browser client (curl/Altair)
  has no CSP at all. **Only the embedded IDE (Nitro) needs relaxed CSP, so it is served
  only outside Production** (IDE off in prod). CSP stops being a blocker.
- **Rate-limit per *token*, not per IP** — a correctness point in prod, not a nicety:
  production sits behind Caddy/Cloudflare/Tailscale and the app trusts only loopback-proxy
  forwarded headers ([`Program.cs:393`](../src/ShelfAware.Web/Program.cs)), so per-IP would
  bucket many callers under one proxy address. Partition the limiter on the authenticated
  token id. Model it on the existing named policy `"cookalong"`
  ([`Program.cs:333`](../src/ShelfAware.Web/Program.cs)).
- **CORS: default-deny.** Bearer-in-`Authorization` avoids cookie-CORS problems, but a
  browser app on another origin still needs an explicit allow-list. v1 = same-origin /
  non-browser clients only; cross-origin browser access is a later, deliberate add.
- **Errors masked in prod** (Hot Chocolate default outside dev) — matches the repo's
  "stop leaking `ex.Message`" audit rule (CLAUDE.md item 8). Exception detail in dev only.
- **Auth required on the whole endpoint, including introspection** — no anonymous schema
  exposure in prod. (Introspection stays *enabled*, just behind the token.)
- **Schema evolves additively** (add fields, `@deprecated`) — no `/v1` URL versioning.
  That's the stability contract if users build against it.
- **Metering: a conscious N/A.** Read-only over stored data makes zero AI calls, so
  `MeteredChatClient`/quotas don't apply (CLAUDE.md item 9). Stated so it's a decision.
- **Migration is already handled:** the `ApiTokens` table rides the existing
  `AdditiveSchema.Apply(authDb)` boot pass that migrates the live auth.db in place
  ([`AdditiveSchema.cs:84`](../src/ShelfAware.Web/Data/AdditiveSchema.cs), called at
  [`Program.cs:372`](../src/ShelfAware.Web/Program.cs)) — shipping to prod creates the
  table with no manual step.

### Deployment wrinkle specific to this topology (not a code change now)

The **family box is behind Cloudflare Access** (interactive One-time PIN — see
`docs/family-cloudflare.md`). A programmatic bearer client can't complete an interactive
PIN, so if GraphQL is ever enabled *there*, the edge would block the request before the
token auth runs. The app's token auth is **self-sufficient by design**, so this is purely
an edge-policy decision at graduation: either a Cloudflare Access *service token*, or
exclude the `/graphql` path from the Access policy and rely on the app's token auth alone.
The droplet demo (no Access) doesn't have this. Recorded so it's on the radar.

---

## 3. The `ApiToken` credential

⚠️ **Lives in auth.db (`AuthDbContext`), NOT as an `IHouseholdOwned` pantry table.** The
lookup happens *before* the household is known — it's the authentication step — so a
pantry query filter would hide the row (chicken-and-egg: you can't query the
household-scoped pantry to discover which household the token grants). auth.db is where
credentials/operator data already live (`AppUser`, `Household`, `ErrorLog`), and it has no
household filter, so the handler can look a token up by hash directly. This is why it does
**not** walk the pantry "full drill."

**Entity** (`Auth/ApiToken.cs`):

| Field | Notes |
|---|---|
| `Id` | PK |
| `HouseholdId` | plain string — the household this token grants (no cross-DB FK, by design) |
| `CreatedByUserId` | which `AppUser` minted it |
| `Name` | user-facing label ("my meal-planner script") |
| `TokenHash` | SHA-256 of the raw token — **the raw token is never stored** |
| `Prefix` | first ~8 chars (`sa_1a2b…`), shown in the UI to identify a token |
| `CreatedAt`, `LastUsedAt?`, `RevokedAt?`, `ExpiresAt?` | lifecycle |

**Token format:** `sa_` + CSPRNG bytes (base64url). Shown **once** at creation
(GitHub-PAT model); only the hash + display prefix persist. Never logged, never echoed.

**Schema:** add `DbSet<ApiToken> ApiTokens` to `AuthDbContext`; add
`EnsureTable(db, "ApiTokens")` to the `Apply(AuthDbContext)` overload in `AdditiveSchema`;
add an `ApiTokens` case to the auth-side schema-parity test (`AdditiveSchemaTests`).

⚠️ **auth.db tokens are outside the pantry `UserDataService` drill**, so two things must be
wired explicitly:
- **Delete-my-data** must also revoke/remove that household's `ApiTokens` (a token to a
  wiped pantry is harmless but untidy). Call `ApiTokenService` from the household-delete
  flow.
- **Export** includes token *metadata* (name, prefix, created, last-used) — **never** the
  secret or the hash.

---

## 4. The authentication scheme

`Auth/ApiTokenAuthenticationHandler.cs` (+ options), registered as scheme `"ApiToken"`
alongside the existing cookie/Identity auth (it does **not** become the default scheme):

1. Read `Authorization: Bearer sa_…` (no header → no result, let the challenge 401).
2. SHA-256 the presented token; look it up unscoped in auth.db by `TokenHash`.
3. Reject (401) if not found / `RevokedAt` set / `ExpiresAt` past.
4. On success, build a `ClaimsPrincipal` carrying `HouseholdClaimsPrincipalFactory.HouseholdClaim`
   = `token.HouseholdId` (+ a `NameIdentifier` = `CreatedByUserId`, + a token-id claim for
   per-token rate limiting), and stamp `LastUsedAt`.
5. `Challenge` writes a bare **401** (mirrors the `/api` convention — no HTML redirect).

Require the scheme on the endpoint via an authorization policy that calls
`AddAuthenticationSchemes("ApiToken")` + `RequireAuthenticatedUser()`. Because the
principal carries the household claim, `CurrentHousehold` (scoped) and `IHouseholdDbFactory`
(scoped) resolve the tenant with zero further wiring.

`Auth/ApiTokenService.cs` (scoped) owns mint / hash / list / revoke / validate, so the
Settings UI, the handler, and the delete-my-data flow share one definition.

---

## 5. The GraphQL schema

Hot Chocolate (ChilliCream `HotChocolate.AspNetCore`) in the **Web** layer — Core stays
EF/web-free. Map `/graphql`, require the ApiToken policy.

⚠️ **Confirm exact Hot Chocolate API names against the pinned version at build time** —
the cost/complexity and depth-rule APIs changed across v13→v14→v15. Pin the latest stable.

### Types

Root `Query`: `products(...)`, `product(id)`, `recipes`, optionally `receipts`. All roots
resolve through `IHouseholdDbFactory` (AsNoTracking).

Object types map to the domain (all in `src/ShelfAware.Core/Domain/`): `Product`,
`PurchaseEvent`, `ReceiptLine`/`Receipt`, `ProductTag`, `ProductSubstitute`, `Recipe`/
`RecipeIngredient`, `InventorySignal`. Plus two **computed** types: `Prediction`,
`Estimate`.

### ⚠️ Stored vs computed — the load-bearing distinction

Most fields are plain stored columns (trivial field-to-column). But **predictions and
estimates are never stored — they are computed on every read** by the pure Core engine.
A GraphQL type therefore **cannot** map them field-to-column; `Product.prediction` and
`Product.estimate` are **resolvers that run the engine**.

⚠️ **"One prediction, one story" (a hard directive — see CLAUDE.md).** The resolver must
run the engine **once, with the same flags the product surfaces use**, or the API silently
contradicts the UI:

```csharp
// today = server-local DateOnly.FromDateTime(DateTime.Today)  (matches the rest of the app)
// honorExpirations = the household's TrackExpirationDates setting (SettingsStore)
// honorQuantity   = true   (the §13 count block, as product/shopping surfaces use)
var prediction = ReplenishmentPredictor.Predict(product, today, honorExpirations, honorQuantity: true);

// Estimate needs a unit price Core can't derive: aggregate confirmed ReceiptLine.UnitPrice
// via ProductPriceIndex, then feed it in.
var unitPrice = priceIndex.PriceFor(product.Id, prediction.RecommendedSize);
var estimate  = ShoppingEstimator.For(product, prediction, today, unitPrice);
```

Match the **product surfaces** (`Home`/`Products`/`ProductDetail`/`GroceryList` — all use
`honorQuantity: true`), **not** the reports/backtest sites (which deliberately pass
`honorQuantity: false`). Reference:
[`ReplenishmentPredictor.cs:44`](../src/ShelfAware.Core/Prediction/ReplenishmentPredictor.cs),
[`ShoppingEstimator.cs:93`](../src/ShelfAware.Core/Shopping/ShoppingEstimator.cs),
[`ProductPriceIndex.cs`](../src/ShelfAware.Core/Shopping/ProductPriceIndex.cs).

### ⚠️ DataLoaders (the N+1 fix — and a headline résumé point)

`EfPantryStore.GetProductsAsync` eager-loads `Purchases` + `Signals` but **not** `Tags` /
`Substitutes` ([`EfPantryStore.cs:14`](../src/ShelfAware.Web/Data/EfPantryStore.cs)), so
nested `tags`/`substitutes` across a product list would N+1. Batch them with DataLoaders
keyed by `ProductId` (each over a scoped, AsNoTracking `IHouseholdDbFactory` context) —
`TagsByProductDataLoader`, `SubstitutesByProductDataLoader`. "Eliminated N+1 with
DataLoader batching" is one of the strongest lines this feature buys, so do it properly
rather than blanket-`Include`.

Prefer **explicit resolvers over auto-projection** (`[UseProjection]`/`[UseFiltering]`) for
the computed and tenancy-sensitive fields — auto-projection over the raw context would
bypass both the engine and the household stamping.

### Example

```graphql
query {
  products(runningLow: true) {
    name
    category
    prediction { status dueDate pinned medianIntervalDays }
    estimate    { recommendedQuantity recommendedSize usualBrand expectedCost }
    purchases(last: 3) { purchasedAt quantity brand size unitPrice }
    tags { value }
  }
}
```

```bash
curl -H "Authorization: Bearer sa_xxx" \
     -H "Content-Type: application/json" \
     -d '{"query":"{ products { name prediction { status dueDate } } }"}' \
     http://localhost:5179/graphql
```

---

## 6. Security limits

- **No `Mutation` type** — read-only by absence.
- **Query depth limit** — reject deeply nested recursive queries (product → recipes →
  ingredients → product → …).
- **Cost / complexity analysis** — cap total field cost so one query can't fan out into a
  DoS.
- **Named rate-limit policy**, partitioned per token (see §2).
- **Introspection behind auth; errors masked in prod** (see §2).

---

## 7. Settings UI (needed to be a usable slice)

An "API access" section (gated on `GraphQL:Enabled`): **create** a token (shows the secret
once, then only the prefix), **list** tokens (name · prefix · created · last-used),
**revoke**. Static-SSR or interactive per the page's existing pattern; writes go through
`ApiTokenService`.

---

## 8. Build arc — phases (one atomic commit each)

Each phase is independently reviewable and leaves the app green.

1. **`ApiToken` credential + schema.** Entity, `DbSet`, `AdditiveSchema.EnsureTable`,
   schema-parity test. No behaviour yet. Tests: parity, `ApiTokenService` (hash, mint-once,
   revoke, expiry).
2. **The ApiToken authentication scheme.** Handler + options + policy; register alongside
   cookie auth. This is the whole tenancy integration. Tests: valid → principal with the
   right household claim; revoked/expired/unknown → 401.
3. **Hot Chocolate + root resolvers.** `AddGraphQLServer` (gated on `GraphQL:Enabled`),
   `MapGraphQL`, require the policy, IDE off outside Development. `Product`/`Purchase`/
   `Receipt`/`Recipe` types, `products`/`product`/`recipes` roots via `IHouseholdDbFactory`.
   ⚠️ **Tenancy headline test here:** a token for household A cannot read B's products.
4. **Computed fields + DataLoaders.** `Product.prediction` / `Product.estimate` resolvers
   running the engine with the product-surface flags; DataLoaders for tags/substitutes.
   ⚠️ **"One prediction, one story" test:** the API's prediction equals `Predict(...)` with
   the app's flags (e.g. an expiration-tracking household sees the capped due date; a
   suppressed count shows suppressed).
5. **Security limits.** Depth + cost analysis, per-token rate-limit policy, error masking,
   introspection-behind-auth. Tests: an over-deep query is rejected; no `Mutation` type.
6. **Settings UI** to mint/list/revoke, gated on the flag. Plus wire the household-delete
   flow to revoke tokens and export to list token metadata.
7. **Docs + `/pre-push` gate.** `docs/graphql-api.md` (schema, auth, example queries,
   limits), then the code + security review gate before any merge. Push is Jordan's call.

---

## 9. File inventory

**New (Web):**
- `Auth/ApiToken.cs`, `Auth/ApiTokenService.cs`,
  `Auth/ApiTokenAuthenticationHandler.cs` (+ options)
- `GraphQL/Query.cs`, `GraphQL/ProductType.cs`, `GraphQL/PredictionType.cs`,
  `GraphQL/EstimateType.cs`, `GraphQL/PurchaseType.cs`, `GraphQL/ReceiptTypes.cs`,
  `GraphQL/RecipeType.cs`, `GraphQL/TagType.cs`
- `GraphQL/DataLoaders/TagsByProductDataLoader.cs`,
  `GraphQL/DataLoaders/SubstitutesByProductDataLoader.cs`
- Settings "API access" component (new or a section on the existing Settings page)
- `docs/graphql-api.md`

**Touched (Web):**
- `Auth/AuthDbContext.cs` (add `DbSet<ApiToken>`)
- `Data/AdditiveSchema.cs` (auth overload: `EnsureTable("ApiTokens")`)
- `Program.cs` (register the scheme; `AddGraphQLServer`/`MapGraphQL` gated on
  `GraphQL:Enabled`; per-token rate-limit policy; CORS default; IDE-off-in-prod)
- `Data/UserDataService.cs` (delete-my-data revokes tokens; export lists token metadata)
- `appsettings.Development.json` (`GraphQL:Enabled: true`)

**Core:** none — Core stays EF/web-free; the predictor/estimator already exist.

**Tests:** `ApiTokenServiceTests`, `ApiTokenAuthenticationHandlerTests`,
`GraphQLTenancyTests` (headline), `GraphQLPredictionConsistencyTests`,
`GraphQLSecurityLimitsTests` (depth + no-mutation), `AdditiveSchemaTests` (ApiTokens
parity). Hot Chocolate's in-memory `IRequestExecutor` runs queries without a server.

---

## 10. Small calls defaulted (revisit at build)

- **Endpoint `/graphql`**, ApiToken policy required (→ 401, not redirect). Alternative:
  `/api/graphql` to inherit the `/api` cookie-event conventions.
- **Delete-my-data revokes the household's tokens; export lists token metadata (never the
  secret).**
- **Rate limit partitioned per token**, not per IP (the prod-correct choice, §2).
- **`GraphQL:Enabled` default `false`**, set only in `appsettings.Development.json` for now.

---

## References (verified plug-points, 2026-08-18)

- Household claim resolution for plain HTTP: `CurrentHousehold.cs:39-63`; claim const
  `HouseholdClaimsPrincipalFactory.cs:14` (`"shelfaware:household"`).
- Scoped context stamping: `HouseholdDbFactory.cs:18-23`; read filter + write guard
  (`EnforceHousehold`) `ShelfAwareDbContext.cs:96-100, 125-143`.
- auth.db owner: `AuthDbContext.cs`; migration overload `AdditiveSchema.cs:84-99`, called
  `Program.cs:372`. `IHouseholdOwned` `src/ShelfAware.Core/Domain/IHouseholdOwned.cs`.
- Pipeline / `/api` 401-not-redirect: `Program.cs` cookie events `:115-135`, no-household
  guard `:460-496`, rate limiter `:330-347`, CSP `:413-440`.
- Engine (computed, never stored): `ReplenishmentPredictor.cs:44`; `PredictionResult.cs`;
  `ShoppingEstimator.cs:93`; `ProductPriceIndex.cs`; `EfPantryStore.cs:14, 25`.
