# The read-only GraphQL API

A read-only GraphQL API over the pantry domain, so a household can query **its own** data
with a token — for a meal-planner script, a spreadsheet, a home dashboard, whatever. It is
read-only by design: there is no `Mutation` type, so a client can never write into a pantry.

Built with [Hot Chocolate](https://chillicream.com/docs/hotchocolate) (`HotChocolate.AspNetCore`)
in the Web layer; the plan and rationale are in [`graphql-api-plan.md`](graphql-api-plan.md).

---

## Turning it on

Exposure is a single config flag — **not** an environment lock, because the API is meant to
reach production; enabling it there is a config flip, not a code change.

```jsonc
// appsettings.json (or an environment variable GraphQL__Enabled=true)
"GraphQL": { "Enabled": true }
```

Default is **off** (unset = off). It's on in `appsettings.Development.json` for local work. When
the flag is off, no schema is built, no endpoint is mapped, and the Settings "API access"
section is hidden — the feature simply doesn't exist.

The endpoint is **`POST /graphql`**.

---

## Authenticating

The API authenticates by **bearer token**, never the app's session cookie. Each token grants
read access to exactly **one household**.

1. Sign in and open **Settings → API access**.
2. **Create a token**, give it a name, and copy the secret **immediately** — it's shown once
   (only a hash is stored; if you lose it, revoke it and make a new one). A secret looks like
   `sa_` followed by random characters.
3. Send it on every request:

```
Authorization: Bearer sa_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

A missing, malformed, revoked, or expired token gets a bare **401** with a small JSON error —
no HTML login redirect (it's an API, not a browser). You can **revoke** a token from the same
Settings section at any time; revocation is immediate.

### How the tenancy works

The token → household lookup *is* the authentication step. On success the auth handler builds a
principal carrying the household claim — the **same** claim the web app's cookie uses — so every
resolver is scoped through the exact query-filter machinery the UI relies on. A token for
household A physically cannot express a query for household B's data. There is no new query-filter
bypass anywhere in the API.

---

## The schema

### Roots (`Query`)

| Field | Returns |
|---|---|
| `products` | every tracked product in your household, name-ordered |
| `product(id: Int!)` | one product by id, or `null` (a foreign/nonexistent id is `null` — no existence oracle) |
| `recipes` | your saved recipes, name-ordered |

### Types

Object types map to the pantry domain, with the tenancy key and internal plumbing deliberately
hidden. The two most interesting fields are **computed on every read** by the pure prediction
engine — they are never stored:

- **`Product.prediction`** — the replenishment answer: `status`, `dueDate`, the rhythms
  (`medianIntervalDays`, `rebuyIntervalDays`, `burnRateDays`), `recommendedSize`, expiration
  state, count-suppression state, and a human `basis` ("bought 5×, ~every 12 days").
- **`Product.estimate`** — the shopping view: `recommendedQuantity`, `recommendedSize`,
  `unitPrice`, `expectedCost`, `usualBrand`, and the count-suppression note.

Both run the engine with the **same flags the app's own product screens use**, and share one
memoized result per request — so the API can never tell you something a screen wouldn't. Nested
`purchases`, `tags`, and `substitutes` are also available; tags and substitutes are
DataLoader-batched, so asking for them across a product list is one query, not N.

---

## Example

```graphql
query {
  products {
    name
    category
    prediction { status dueDate medianIntervalDays basis }
    estimate    { recommendedQuantity recommendedSize unitPrice expectedCost usualBrand }
    purchases   { purchasedAt quantity brand size }
    tags        { value }
  }
  recipes {
    name
    isVariant
    ingredients { name isMain matchedProduct }
  }
}
```

```bash
curl -s -X POST http://localhost:5179/graphql \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer sa_xxx" \
  -d '{"query":"{ products { name prediction { status dueDate } estimate { expectedCost } } }"}'
```

A slice of a real response:

```json
{ "data": { "products": [
  { "name": "Apples",
    "prediction": { "status": "OVERDUE", "dueDate": "2026-08-18", "medianIntervalDays": 9, "basis": "bought 4×, ~every 9 days" },
    "estimate":   { "recommendedQuantity": 6, "recommendedSize": "each", "unitPrice": 0.79, "expectedCost": 4.72, "usualBrand": "Orchard Fresh" } }
] } }
```

---

## Limits & security posture

- **Read-only.** No `Mutation` type — enforced by absence, not a guard.
- **Per-token rate limit** — 120 requests/minute per token (partitioned per token, not per IP,
  because production sits behind a proxy). Over the limit → **429** with a JSON body.
- **Query depth limit** and **cost analysis** — a pathologically deep or fanned-out query is
  rejected before execution, so one request can't become a DoS.
- **Introspection stays behind the token** — no anonymous schema exposure. (Introspection itself
  is enabled, just authenticated.)
- **Errors are masked outside Development** — a resolver exception never leaks a message or stack
  to a client.
- **CORS is default-deny.** v1 is for same-origin and non-browser (curl/script) clients; a token
  in the `Authorization` header sidesteps cookie-CORS concerns. Cross-origin browser access is a
  deliberate later addition.

## The GraphQL IDE

In **Development**, the embedded [Nitro](https://chillicream.com/docs/nitro) explorer is served at
`/graphql` (a browser `GET`). It's off in production — the strict production CSP would block its
inline scripts, and prod exposes no explorer UI. The endpoint requires the token in every
environment, so point the explorer's connection headers at a token (or just use `curl`/Postman).

## Deployment note (family box)

The family instance is behind **Cloudflare Access** (interactive one-time-PIN). A programmatic
bearer client can't complete an interactive PIN, so if the API is ever enabled there, either use
a Cloudflare Access **service token** or exclude `/graphql` from the Access policy and rely on the
app's own token auth (which is self-sufficient). The droplet demo has no Access and isn't affected.

---

## Your data

- **Export** ("Download my data") lists your tokens' *metadata* — name, prefix, and lifecycle
  dates. It never contains the secret or its hash.
- **Delete my data** removes your tokens along with the rest of your household's data.
