# Wisper — API Contract

**Status:** Draft / v0 · **Companion to:** [`DESIGN.md`](./DESIGN.md), [`TUNNEL.md`](./TUNNEL.md), [`DATA_MODEL.md`](./DATA_MODEL.md), [`PAYMENTS.md`](./PAYMENTS.md)

The external surface of Wisper: what the two Next.js apps and third-party clients call, and how the consumer console reaches a leased container. The **agent tunnel** (`WS /agent`) is defined in `TUNNEL.md`; this doc is everything *else* — the human/consumer/host/admin surface — plus the consumer-side relay endpoints that bridge to the tunnel.

---

## 1. Conventions

- **Base URL:** `https://<wisper-host>/v1/…` (prod), with a separate host for dev. The service is deployed behind an API gateway / load balancer that terminates TLS and forwards to it; the service owns `/v1`.
- **Versioning:** path-prefixed `/v1`. Breaking changes bump to `/v2`; `/v1` is supported through a published deprecation window. Additive changes (new fields, new endpoints) never bump.
- **Media type:** `application/json; charset=utf-8` for request/response bodies. SSE endpoints emit `text/event-stream`; the console is a raw WebSocket.
- **Time:** ISO-8601 UTC (`2026-07-11T18:03:00Z`). **Money:** integer `cents` + `currency` (`"usd"`), never floats.
- **Request tracing:** every response carries `X-Request-Id`; clients should log it. It correlates with tunnel/ledger logs server-side.
- **Idempotency:** money-mutating `POST`s **require** an `Idempotency-Key` header (§9).
- **Pagination:** cursor-based — `?limit=` (default 25, max 100) `&cursor=`; responses return `{ "data": [...], "next_cursor": "…"|null }` (§10).
- **Rate limits:** per-user token bucket; `X-RateLimit-Limit/Remaining/Reset` headers; `429` with `Retry-After` on exhaustion (§11).

## 2. Authentication & roles

- **Human APIs (consumer/host/admin):** `Authorization: Bearer <Cognito JWT>`. Wisper validates the JWT against the pool's JWKS (issuer, audience, expiry, signature). Identity = the `sub` claim; the first authenticated call **bootstraps** the `users` row (`DATA_MODEL.md` §3).
- **Machine API keys (consumer surface):** `Authorization: Bearer wck_live_<64-hex>` — a long-lived key for a machine client (first: the orchestrator app) driving the `/v1` surface without the Cognito JWT flow (`DATA_MODEL.md` §3, `api_keys`). The auth layer tells a key from a JWT by its `wck_` prefix and, instead of JWT validation, does a **constant-time hashed lookup** (SHA-256 at rest, shown once at mint) against `api_keys`; an active key resolves to a principal for its **owning user** (same identity the JWT path would produce, so every downstream endpoint and role gate is unchanged). **Scopes, not Cognito groups:** a key's roles are exactly its stored `scopes` (⊆ `{consumer, host, admin}`) — there is **no** implicit `consumer`, so a key must be granted each role it needs (a key lacking the gate's role is `403`). **Fail-closed:** a key that is unknown, revoked (`revoked_at` set), or whose owner is suspended, and any empty/malformed bearer, is rejected `401 unauthenticated` — same envelope as a bad JWT. Best-effort `last_used_at` is stamped on use. Keys are minted/listed/revoked at `/v1/me/api-keys` (§5); **minting is JWT-only** — a key cannot mint more keys (privilege containment, `403`) — and requested scopes are **capped by the minter's own roles**.
- **Dev/bootstrap key config-map:** an `Auth:ApiKeys` config section maps a raw key string → `{ userId, email?, scopes[] }`, mirroring `Tunnel:HostTokens`. It is the fallback the key authenticator uses when the DB-backed store does not hold the presented key, letting an operator drive `/v1` locally with no Cognito — including the whole self-hosted flow on the **in-memory persistence dev mode** (`DATA_MODEL.md` §1): register a host, price its images, and place leases. The optional `email` seeds the principal's `email` claim so the key can **bootstrap** the operator's `users` row (`users.email` is NOT NULL, `DATA_MODEL.md` §3); without it, host registration / lease creation fail `validation_error`. **Empty by default and fail-closed**, so production — which never sets it — is unaffected.
- **Roles are additive** (`DESIGN.md` §10), sourced from Cognito **groups** (`consumer`, `host`, `admin`). Every authenticated user is implicitly `consumer`; `host`/`admin` are added. Endpoint tables below mark the **minimum** role. (An API key instead carries its granted roles as explicit `scopes` — see above.)
- **Agent tunnel** (`WS /agent`): authenticated by the **host agent token** (`Authorization: Bearer`), *not* a JWT — see `TUNNEL.md` §3.
- **Stripe webhook** (`POST /stripe/webhook`): authenticated by **signature**, no bearer (`PAYMENTS.md` §8).
- **Browser WebSocket auth uses a one-time ticket, never the JWT in a URL** (§7) — a deliberate security choice, since a browser cannot set headers on a WS handshake and JWTs must not land in URLs/logs.

## 3. Error model

Uniform envelope on every non-2xx:

```json
{ "error": {
    "code": "insufficient_funds",
    "message": "Wallet balance is below the required hold.",
    "request_id": "req_9f2c…",
    "details": { "required_cents": 500, "available_cents": 120 }
} }
```

| `code` | HTTP | When |
|---|---|---|
| `validation_error` | 400 | malformed/invalid body or params (`details` lists fields) |
| `unauthenticated` | 401 | missing/invalid/expired JWT |
| `forbidden` | 403 | authenticated but lacks the role/ownership |
| `connect_incomplete` | 403 | host action requires `connect_status='enabled'` |
| `not_found` | 404 | no such resource (or not owned by caller) |
| `payment_required` / `insufficient_funds` | 402 | wallet can't cover a hold/top-up minimum |
| `conflict` | 409 | idempotency mismatch, illegal state transition |
| `host_offline` | 409 | target host has no live tunnel |
| `lease_not_ready` | 409 | exec/shell before the lease is `active` |
| `at_capacity` | 409 | host or per-user concurrency limit reached |
| `image_not_allowed` | 400 | requested image not in the host's priced allow-list |
| `rate_limited` | 429 | token bucket exhausted (`Retry-After`) |
| `upstream_timeout` | 504 | tunnel op exceeded its deadline (`TUNNEL.md` §12) |
| `internal` | 500 | unexpected fault (already logged with `request_id`) |

Ownership failures return `404` (not `403`) so the API never reveals the existence of resources the caller can't see.

## 4. Public & health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/healthz` | none | liveness `{ "status": "ok" }` |
| POST | `/stripe/webhook` | Stripe sig | webhook ingest (`PAYMENTS.md` §8) |

## 5. Consumer API (min role: `consumer`)

### Account
| Method | Path | Body / notes |
|---|---|---|
| GET | `/v1/me` | identity + roles + `connect_status` |
| PATCH | `/v1/me` | mutable profile fields |

### API keys
| Method | Path | Body / notes |
|---|---|---|
| POST | `/v1/me/api-keys` | mint a machine key → the **full key once** (never retrievable again) |
| GET | `/v1/me/api-keys` | the caller's keys (prefix + scopes + lifecycle; never the hash/key) |
| DELETE | `/v1/me/api-keys/:id` | revoke (idempotent; `404` for another user's key) |

Self-serve machine credentials (§2, `DATA_MODEL.md` §3, `api_keys`). **Minting requires a JWT principal, not an API key** — a key must not be able to mint more keys (privilege containment), so a key-authenticated caller gets `403 forbidden`. Requested `scopes` default to `["consumer"]` and are **capped by the roles the calling JWT holds** — a consumer-only user cannot mint a `host`-scoped key (`403`). The full key is returned exactly once at mint and is never logged or retrievable again; only its hash + non-secret prefix are stored, and a revoked key fails auth on the next request (§2).

**`POST /v1/me/api-keys`** request → response:
```json
// request
{ "name": "orchestrator-prod", "scopes": ["consumer"] }
// 201 — `key` shown once
{ "id": "…", "name": "orchestrator-prod", "key": "wck_live_<64-hex>",
  "token_prefix": "wck_live_ab12", "scopes": ["consumer"], "created_at": "…Z" }
```

**`GET /v1/me/api-keys`** item shape:
```json
{ "id": "…", "name": "orchestrator-prod", "token_prefix": "wck_live_ab12",
  "scopes": ["consumer"], "created_at": "…Z", "last_used_at": "…Z"|null,
  "revoked_at": "…Z"|null }
```

### Catalog
| Method | Path | Notes |
|---|---|---|
| GET | `/v1/catalog` | online hosts and their **priced, enabled** images; filter `?image=&max_price_cents_per_min=&network=`; paginated |
| GET | `/v1/hosts/:id` | one host's public detail + priced images + limits |

`GET /v1/catalog` item shape:
```json
{ "host_id": "…", "label": "home-server-1", "region": "us",
  "images": [ { "host_image_id": "…", "image_ref": "…/wisp-base:latest",
    "price_cents_per_min": 5, "currency": "usd",
    "networks": ["none","open"], "max_ttl_seconds": 14400,
    "max_cpus": 4, "max_memory_mb": 8192 } ],
  "isolation_levels": ["shared","vm"], "default_isolation": "shared",
  "online": true }
```
`isolation_levels` are the sandbox levels this host offers and `default_isolation` the one it uses when a lease requests none, mirrored from the host's tunnel capability (`TUNNEL.md` §5, task #417). A host that advertises nothing (an older agent) surfaces `["shared"]` with default `"shared"`; the same two fields appear on `GET /v1/hosts/:id`. Levels are opaque strings, so a consumer can filter on the level it needs without the manager enumerating them.

### Leases
| Method | Path | Auth extras | Notes |
|---|---|---|---|
| POST | `/v1/leases` | `Idempotency-Key` | create + provision a lease |
| GET | `/v1/leases` | | caller's leases; filter `?status=`; paginated |
| GET | `/v1/leases/:id` | | status, timeline, running cost |
| DELETE | `/v1/leases/:id` | | release (idempotent; safe to retry) |
| POST | `/v1/leases/:id/exec` | | sync exec → `{stdout,stderr,exit_code}` |
| POST | `/v1/leases/:id/exec?stream=1` | | SSE stream (`chunk`/`exit`/`error` events, `PAYMENTS.md`-free) |
| POST | `/v1/leases/:id/shell-ticket` | | mint a one-time WS ticket (§7) |
| WS | `/v1/leases/:id/shell?ticket=…` | ticket | interactive PTY console (§7) |

**`POST /v1/leases`** request → response:
```json
// request  (Idempotency-Key: <uuid>)
{ "host_id": "…", "host_image_id": "…",
  "network": "open",
  "resources": { "cpus": 2, "memory_mb": 4096, "pids": 1024 },
  "ttl_seconds": 3600,
  "userdata": "apt-get install -y git && …",
  "isolation": "sandboxed",
  "env": { "API_TOKEN": "…", "REGION": "eu" } }
// 201
{ "id": "lease_…", "status": "provisioning",
  "price_cents_per_min": 5, "currency": "usd",
  "hold_cents": 300, "ttl_seconds": 3600, "created_at": "…Z",
  "os": "linux" }
```
`isolation` is the **optional** requested sandbox level, ordered `shared` < `sandboxed` < `vm` (`TUNNEL.md` §5, task #418). Omitted → `shared`; `confidential` or any unknown value → `validation_error`. It is resolved and validated server-side — against the admin-tunable `platform_policy.min_isolation` floor and, when the target host advertises isolation levels (task #417), against the levels that host can provide (a host with none recorded passes through, since wisp re-validates as the real security boundary) — then snapshotted immutably on the lease, returned on `GET /v1/leases/:id`, and forwarded on the `lease.create` frame.

`env` is an **optional, opaque `{string:string}` map** of create-time environment variables forwarded down the host tunnel for secret injection (mirrors `POST /dev/leases`; `lease.create` frame, `TUNNEL.md` §5). Capped like wisp's own limits — at most **128** entries and **256 KiB** serialized, else `validation_error`. Its **values are secrets-in-transit**: never logged, never echoed in errors, and **never persisted** on the lease row (the lease snapshot keeps everything *except* `env`) — and it is **plaintext v1**, so treat it as trusted-network only (`TUNNEL.md` §13). `os` echoes the host's advertised container OS (`"linux"` | `"windows"`, or `null` when the host is offline / its agent advertised none) like `GET /v1/leases/:id` (task #316).

Server flow: validate image/network/resources/isolation/env against the host's priced allow-list → **place the wallet hold** (`PAYMENTS.md` §4; `402` if insufficient, and **no** `lease.create` frame is sent) → `lease.create` down the host tunnel → `201`. The client polls `GET /v1/leases/:id` (or subscribes via the events stream, below) until `status:"active"`.

**`GET /v1/leases/:id`**:
```json
{ "id":"lease_…","status":"active","host_id":"…","image_ref":"…",
  "network":"open","resources":{…},"ttl_seconds":3600,
  "price_cents_per_min":5,"currency":"usd",
  "started_at":"…Z","billable_seconds":742,"cost_cents_so_far":62,
  "expires_at":"…Z","end_reason":null,"isolation":"sandboxed" }
```

### Billing
| Method | Path | Auth extras | Notes |
|---|---|---|---|
| GET | `/v1/billing` | | wallet balance + usage summary |
| GET | `/v1/billing/transactions` | | the caller's ledger view (top-ups, charges, refunds); paginated |
| POST | `/v1/billing/topup` | `Idempotency-Key` | create a PaymentIntent → `{ client_secret }` (`PAYMENTS.md` §3) |
| POST | `/v1/billing/payment-methods` | | SetupIntent to save a method → `{ client_secret }` |
| GET/PUT | `/v1/billing/auto-recharge` | | threshold + amount + on/off |
| POST | `/v1/billing/refund` | `Idempotency-Key` | refund unspent wallet credits |

## 6. Host API (min role: `host`)

Becoming a host is additive — a `consumer` gains the `host` group on first host action; existing consumer/lease access is unchanged.

| Method | Path | Auth extras | Notes |
|---|---|---|---|
| POST | `/v1/hosts` | | register a wisp host → returns the **agent token once** (never retrievable again) |
| GET | `/v1/hosts/mine` | | caller's hosts, online state, capacity, earnings summary |
| GET | `/v1/hosts/:id` (owned) | | full host detail |
| DELETE | `/v1/hosts/:id` | | deregister (drains pending earnings first) |
| POST | `/v1/hosts/:id/agent-token` | | rotate the agent token (old one revoked, tunnel closed `4402`) |
| GET | `/v1/hosts/:id/images` | | priced allow-list |
| PUT | `/v1/hosts/:id/images` | `validation_error` if a priced image is enabled without Connect | replace the priced allow-list (validated live against the host's advertised wisp capability) |
| PATCH | `/v1/hosts/:id/images/:imageId` | `validation_error` if a priced image is enabled without Connect | price/enable/limits for one image |
| POST | `/v1/hosts/connect` | | create/continue Stripe **Connect Express** onboarding → `{ onboarding_url }` |
| GET | `/v1/hosts/connect/status` | | `connect_status` + what's still required |
| GET | `/v1/earnings` | | accrued + paid, by period |
| GET | `/v1/earnings/payouts` | | payout history (→ Stripe transfer ids) |
| POST | `/v1/payouts` | `Idempotency-Key`, `connect_incomplete`→403 | on-demand payout of accrued earnings (`PAYMENTS.md` §6) |

**`POST /v1/hosts`** response (token shown once):
```json
{ "id":"host_…","name":"home-server-1",
  "agent_token":"wht_live_…","agent_token_prefix":"wht_live_a1b2",
  "manager_ws":"wss://<wisper-host>/agent",
  "status":"offline" }
```
Pricing/earning is inert until `connect_status='enabled'` and the agent has connected and advertised capability (`TUNNEL.md` §3).

**Presence follows the tunnel.** A host's `status` is driven by its agent tunnel, not set by hand: when the handshake completes Wisper flips it `online` if it clears the earning gate — the owner is Connect-enabled **or** every enabled `host_image` is priced at `0` cents/min (the self-hosted / zero-price posture, `PAYMENTS.md` §5) — and a durable tunnel loss (grace expiry, or a close with no leases to protect) flips it back `offline` (`TUNNEL.md` §8). An admin-suspended host never comes back `online` on reconnect.

**Charging requires Connect (images surface).** Because the online gate has a zero-earn arm, `PUT`/`PATCH …/images` **reject enabling a non-zero-priced image while the owner's `connect_status` is not `enabled`** (`validation_error` explaining Connect onboarding is required to charge; price at `0` to self-host for free). This is the single pricing mutation point, so a live host is never knocked `offline` mid-tunnel — it simply cannot move into the earning arm without Connect (`PAYMENTS.md` §5).

## 7. WebSocket & streaming (the console relay)

The consumer console and live output flow **through** Wisper to the host tunnel (`TUNNEL.md` §11) — consumers never touch a host directly.

- **Sync exec** — `POST /v1/leases/:id/exec {command}` → `{stdout,stderr,exit_code}`. Normal JWT header auth. `409 lease_not_ready` unless `active`.
- **Streamed exec** — `POST /v1/leases/:id/exec?stream=1` → **SSE** (`event: chunk` `{stream,data}`, `event: exit` `{exit_code}`, `event: error`). Uses `fetch`+reader on the client (JWT header, like wisp-dashboard) — *not* `EventSource`, which can't set headers.
- **Interactive shell** — a raw **WebSocket** at `/v1/leases/:id/shell`:
  1. **Ticket first:** the client calls `POST /v1/leases/:id/shell-ticket` (JWT header) → `{ "ticket":"tkt_…", "expires_in": 30 }`. Single-use, ~30s TTL, bound to `(user, lease)`.
  2. **Connect:** `WS /v1/leases/:id/shell?ticket=tkt_…`. Wisper redeems the ticket (one-time), then bridges to a tunnel `shell` stream. **The JWT never appears in a URL** — the ticket does, and it's single-use and short-lived.
  3. **Frames:** **binary** frames carry terminal bytes (client→PTY stdin, PTY→client stdout); a small **JSON text** control frame `{ "t":"resize", "cols":120, "rows":32 }` sends window size. This mirrors the tunnel's shell framing so the relay is a passthrough.
  4. **Backpressure:** the relay honors the tunnel's per-stream credit flow control (`TUNNEL.md` §9) end-to-end to the browser socket.
- **Lease events (optional live status):** `WS /v1/leases/:id/events?ticket=…` (same ticket scheme) streams lifecycle JSON (`provisioning→active→…`), so the UI needn't poll. Backed by the Redis backplane so it works across manager instances.

## 8. Admin API (min role: `admin`)

| Method | Path | Notes |
|---|---|---|
| GET | `/v1/admin/overview` | revenue, active leases, host/consumer counts, health |
| GET | `/v1/admin/policy` | current + version history |
| PUT | `/v1/admin/policy` | new **versioned** policy row (fee_bps, caps, min top-up) — audited |
| GET | `/v1/admin/hosts` · `/v1/admin/users` | search/list |
| POST | `/v1/admin/hosts/:id/suspend` · `/unsuspend` | moderation (audited) |
| POST | `/v1/admin/users/:id/suspend` · `/unsuspend` | moderation (audited) |
| POST | `/v1/admin/refunds` | `Idempotency-Key` | manual refund (audited) |
| POST | `/v1/admin/adjustments` | `Idempotency-Key` | ledger `adjustment` txn — the *only* way to hand-correct money, always balanced + audited (`DATA_MODEL.md` §7,§12) |
| GET | `/v1/admin/audit` | audit log; filter by actor/target/action; paginated |
| GET | `/v1/admin/ledger/accounts/:id` | ledger account + entries (read-only forensics) |

Every admin write records an `audit_log` row with actor + before/after (`DATA_MODEL.md` §12).

## 9. Idempotency

- **Required** on `POST /v1/leases`, `/v1/billing/topup`, `/v1/billing/refund`, `/v1/payouts`, `/v1/admin/refunds`, `/v1/admin/adjustments` via the `Idempotency-Key` header (client-generated UUID).
- Semantics (`DATA_MODEL.md` §10): first request runs and stores its response under the key; a **replay with the same key + same body** returns the stored response verbatim; **same key + different body** → `409 conflict`; a replay while the first is still in-flight → `409` (in-progress lock). Keys are user-scoped and TTL'd.
- Below the API, `ledger_transactions.idempotency_key` provides a second, DB-level guarantee, so even a bug in the API layer cannot double-post money.

## 10. Pagination, filtering, sorting

- Cursor-based: `?limit=&cursor=`; opaque `next_cursor` (null at end). Cursors encode a stable sort key (usually `created_at,id` desc), so inserts during paging don't duplicate/skip.
- Documented filters per endpoint (e.g. leases `?status=`, catalog `?image=&network=&max_price_cents_per_min=`). Unknown query params are ignored, not errored.

## 11. Rate limiting & quotas

- Per-user token bucket (tighter anonymous/`/healthz` excluded). Money endpoints and lease creation get stricter buckets. `429 rate_limited` + `Retry-After`; `X-RateLimit-*` on every response.
- **Business quotas** (distinct from rate limits): `max_concurrent_leases_per_user` and per-user spend caps come from `platform_policy` and return `409 at_capacity` / `402` respectively — enforced server-side regardless of client behavior.

## 12. Observability & correlation

- `X-Request-Id` on every response; the same id threads through the lease's tunnel frames and ledger transactions, so one id ties an API call → tunnel op → money movement in logs.
- Structured logs + metrics (request rate/latency/error by route, active tunnels, active leases, ledger write latency, webhook lag). Health of downstreams (Postgres, Redis, Stripe) surfaces in `/v1/admin/overview`.

## 13. Deliberate scope boundaries

- **No public/unauthenticated catalog** in v0 — browsing requires a (free) consumer account. A marketing-only anonymized catalog can be added as a separate public route later without touching the authed surface.
- **No third-party OAuth-app model** yet. First-party **machine API keys** (§2, `wck_live_…`) exist for a machine client driving the *consumer* surface as its owning user; a broader external-integrator model (OAuth apps, per-app service principals, finer scopes) is additive when there's demand.
- **No GraphQL** — a typed REST surface is sufficient and simpler to secure, cache, and rate-limit for these access patterns.
- **Webhooks *out* to hosts/consumers** (e.g. "lease.ended" callbacks) are out of scope — the live WS/SSE surface covers real-time needs; outbound webhooks attach additively if integrators ask.
```
