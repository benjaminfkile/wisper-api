# Wisper — API Contract

**Status:** Draft / v0 · **Companion to:** [`DESIGN.md`](./DESIGN.md), [`TUNNEL.md`](./TUNNEL.md), [`DATA_MODEL.md`](./DATA_MODEL.md), [`PAYMENTS.md`](./PAYMENTS.md)

The external surface of Wisper: what the two Next.js apps and third-party clients call, and how the consumer console reaches a leased container. The **agent tunnel** (`WS /agent`) is defined in `TUNNEL.md`; this doc is everything *else* — the human/consumer/host/admin surface — plus the consumer-side relay endpoints that bridge to the tunnel.

---

## 1. Conventions

- **Base URL:** `https://<wisper-host>/v1/…` (prod), with a separate host for dev. The service is deployed behind an API gateway / load balancer that terminates TLS and forwards to it; the service owns `/v1`.
- **Versioning:** path-prefixed `/v1`. Breaking changes bump to `/v2`; `/v1` is supported through a published deprecation window. Additive changes (new fields, new endpoints) never bump.
- **Media type:** `application/json; charset=utf-8` for request/response bodies. SSE endpoints emit `text/event-stream`; the console is a raw WebSocket.
- **Time:** ISO-8601 UTC (`2026-07-11T18:03:00Z`). **Money:** integer `cents` + `currency` (`"usd"`), never floats.
- **Request tracing:** every response carries `X-Request-Id` (an inbound `X-Request-Id` is echoed back, else one is generated); clients should log it. It is the `request_id` in every log line and error envelope for that request.
- **Idempotency:** money-mutating `POST`s **require** an `Idempotency-Key` header (§9).
- **Pagination:** cursor-based — `?limit=` (default 25, max 100) `&cursor=`; responses return `{ "data": [...], "next_cursor": "…"|null }` (§10).
- **Rate limits:** *designed, not yet implemented* — the intended shape is a per-user token bucket with `X-RateLimit-Limit/Remaining/Reset` headers and `429` + `Retry-After` on exhaustion (§11). Today no generic rate limiter runs; the only 429s are the deterministic fraud-guard caps (`limit_exceeded`, §3).

## 2. Authentication & roles

- **Human APIs (consumer/host/admin):** `Authorization: Bearer <Cognito JWT>`. Wisper validates the JWT against the pool's JWKS (issuer, audience, expiry, signature). Identity = the `sub` claim; the first authenticated call **bootstraps** the `users` row (`DATA_MODEL.md` §3).
- **Machine API keys (consumer surface):** `Authorization: Bearer wck_live_<64-hex>` — a long-lived key for a machine client (first: the orchestrator app) driving the `/v1` surface without the Cognito JWT flow (`DATA_MODEL.md` §3, `api_keys`). The auth layer tells a key from a JWT by its `wck_` prefix and, instead of JWT validation, does a **constant-time hashed lookup** (SHA-256 at rest, shown once at mint) against `api_keys`; an active key resolves to a principal for its **owning user** (same identity the JWT path would produce, so every downstream endpoint and role gate is unchanged). **Scopes, not Cognito groups:** a key's roles are exactly its stored `scopes` (⊆ `{consumer, host, admin}`) — there is **no** implicit `consumer`, so a key must be granted each role it needs (a key lacking the gate's role is `403`). **Fail-closed:** a key that is unknown, revoked (`revoked_at` set), or whose owner is suspended, and any empty/malformed bearer, is rejected `401 unauthenticated` — same envelope as a bad JWT. Best-effort `last_used_at` is stamped on use. Keys are minted/listed/revoked at `/v1/me/api-keys` (§5); **minting is JWT-only** — a key cannot mint more keys (privilege containment, `403`) — and requested scopes are **capped by the minter's own roles**.
- **Dev/bootstrap key config-map:** an `Auth:ApiKeys` config section maps a raw key string → `{ UserId, Email, Scopes[] }`, mirroring `Tunnel:HostTokens`. It is the fallback the key authenticator uses when the DB-backed store does not hold the presented key, letting an operator drive `/v1` locally with no Cognito. The configured `UserId` (matched against `users.cognito_sub`) must resolve to an active `users` row; on a fresh in-memory boot the authenticator **seeds that row on first sight** from the grant's `Email` (idempotent, config-map keys only, so the DB path is unchanged) so a single key drives the whole flow with no out-of-band seeding (task #185). An `Email` is therefore required to make the bootstrap work; a grant with no `Email` still fails `401` rather than a downstream `500` (task #36), a pre-existing suspended row also fails `401`, and the seeded row is a plain `active`, `connect_status='none'` account. A DB-backed key whose owner is missing or suspended likewise fails closed and never falls through to this map. **Empty by default and fail-closed**, so production, which never sets it, is unaffected. (The tunnel's `Tunnel:HostTokens` counterpart is additionally honoured only in the `Development` environment, `TUNNEL.md` §3.)
- **Roles are additive** (`DESIGN.md` §10), sourced from Cognito **groups** (`consumer`, `host`, `admin`). Every authenticated user is implicitly `consumer`; `host`/`admin` are added. Endpoint tables below mark the **minimum** role. (An API key instead carries its granted roles as explicit `scopes` — see above.) The **`host` gate additionally honors DB host-ownership**: a JWT caller who owns ≥1 host passes host-gated endpoints on their *current* token even if it predates the `host` group add (§6, §184) — becoming a host is effective immediately with **no re-login**. This is additive (it never removes a role), JWT-only (an API key authorizes purely by its scopes — ownership never overrides them), and consulted only by the host gate, at most once per request. The same ownership signal drives the `host` role reported by `GET /v1/me` (§5), so the two always agree.
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
| `unauthenticated` | 401 | missing/invalid/expired JWT or API key (including a key whose owner is missing/suspended) |
| `forbidden` | 403 | authenticated but lacks the role/ownership (also: an API key trying to mint keys, or requesting a scope its minter lacks) |
| `connect_incomplete` | 403 | host action requires `connect_status='enabled'` (`POST /v1/payouts`) |
| `not_found` | 404 | no such resource (or not owned by caller); also any malformed id in a path |
| `payment_required` | 402 | a top-up below `platform_policy.min_topup_cents`, or an on-demand payout below `Payouts:PayoutMinCents` |
| `insufficient_funds` | 402 | wallet can't cover a lease hold, a refund exceeds the unspent balance, or an admin adjustment would over-draw a wallet |
| `conflict` | 409 | idempotency mismatch or in-flight lock, email already taken on `PATCH /v1/me`, no top-up to refund against, an adjustment that would over-draw `lease_holds`, moderating a deleted user |
| `host_offline` | 409 | target host has no live tunnel; also returned by `POST /v1/leases` when the host's agent has reported itself `degraded` (`TUNNEL.md` §5), and by `PUT`/`PATCH …/images` when enabling an offer with no live tunnel to validate against |
| `lease_not_ready` | 409 | exec/shell before the lease is `active` |
| `at_capacity` | 409 | per-user **or** per-host concurrency limit reached (also accepted as an agent-reported error); the message distinguishes the two |
| `image_not_allowed` | 400 | requested image not in the host's priced allow-list (also returned when the host is unknown or admin-suspended, so a hidden host is not revealed) |
| `limit_exceeded` | 429 | a fraud-guard cap hit: first-top-up cap, new-account top-up velocity, or daily lease-spend cap (`PAYMENTS.md` §7) |
| `lease_failed` | 502 | the host/agent failed the lease operation (unrecognized agent error, wisp non-2xx) |
| `rate_limited` | 429 | *reserved* — declared for the planned token bucket (§1); nothing emits it today |
| `upstream_timeout` | 504 | tunnel op exceeded its deadline (`TUNNEL.md` §12) |
| `internal` | 500 | unexpected fault (already logged with `request_id`) |

Ownership failures return `404` (not `403`) so the API never reveals the existence of resources the caller can't see.

## 4. Public & health

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/healthz` | none | health `{ "status": "ok"\|"degraded"\|"error", "checks": {...} }`; **503** when unhealthy |
| GET | `/api/health` | none | same handler at the fleet gateway's conventional path |
| GET | `/v1/` | none | service banner `{ "service": "wisper-api", "version": "v1" }` |
| POST | `/stripe/webhook` | Stripe sig | webhook ingest (`PAYMENTS.md` §8) |
| WS | `/agent` | host agent token | the agent tunnel (`TUNNEL.md`) |
| * | `/dev/leases…` | none | **Development environment only** (and `Tunnel:EnableDevEndpoints`): the money-free Phase-1 harness (`POST /dev/leases`, `POST /dev/leases/{id}/exec[?stream=1]`, `DELETE /dev/leases/{id}`, `WS /dev/leases/{id}/shell?hostId=`). Unreachable in any deployed environment (`DESIGN.md` §16) |

The health report's `checks` carries a `database` entry (`healthy` with `latency_ms` when Postgres answers `SELECT 1`, `in-memory` on a DB-less boot, `unhealthy` when a configured database is unreachable). Any unmatched route returns the uniform `404 not_found` envelope.

## 5. Consumer API (min role: `consumer`)

### Account
| Method | Path | Body / notes |
|---|---|---|
| GET | `/v1/me` | identity + roles + `connect_status` |
| PATCH | `/v1/me` | mutable profile fields (today only `{ "email" }`; a taken email is `409 conflict`) |

Both return the same shape: `{ "id", "cognito_sub", "email", "status", "roles": ["consumer","host",…], "connect_status", "created_at", "updated_at" }`. `roles` adds `host` when the caller owns at least one host (§2, §6). Stripe/Connect ids are never returned here.

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
| GET | `/v1/catalog` | online hosts and their **priced, enabled** images; filter `?image=&max_price_cents_per_min=&network=&min_gpus=&gpu_class=`; paginated |
| GET | `/v1/hosts/:id` | one host's public detail + priced images + limits (`404` for an unknown or admin-suspended host) |

A host is listed only while its agent tunnel is live (the local registry, or the distributed presence store when the backplane is on) **and** its agent has not reported itself `degraded` (`TUNNEL.md` §5); a host that contributes no image surviving the filters is dropped from the page. `GET /v1/hosts/:id` reports a degraded host as `"online": false`. On the wire `label` is the host's display name and `region` its label/region string (`DATA_MODEL.md` §4).

`GET /v1/catalog` item shape:
```json
{ "host_id": "…", "label": "home-server-1", "region": "us",
  "images": [ { "host_image_id": "…", "image_ref": "…/wisp-base:latest",
    "price_cents_per_min": 5, "currency": "usd",
    "networks": ["none","open"], "max_ttl_seconds": 14400,
    "max_cpus": 4, "max_memory_mb": 8192, "max_pids": 1024,
    "cpus": 2, "memory_mb": 4096, "gpus": 2,
    "effective_cpus": 2, "effective_memory_mb": 4096, "resources_source": "offer" } ],
  "isolation_levels": ["shared","vm"], "default_isolation": "shared",
  "gpu_classes": ["nvidia-a100"], "gpu_count": 4,
  "os": "linux", "online": true,
  "at_capacity": false, "active_leases": 3, "max_leases": 8 }
```
`isolation_levels` are the sandbox levels this host offers and `default_isolation` the one it uses when a lease requests none, mirrored from the host's tunnel capability (`TUNNEL.md` §5, task #417). A host that advertises nothing (an older agent) surfaces `["shared"]` with default `"shared"`; the same two fields appear on `GET /v1/hosts/:id`. Levels are opaque strings, so a consumer can filter on the level it needs without the manager enumerating them.

An offer **sells a size** (task #569): `cpus`/`memory_mb` are the EXACT resources it provisions per lease (`null` = the host's own per-lease policy default applies downstream), and `gpus` is the EXACT whole exclusive GPU devices it provisions (`0` = no GPU access on this offer). These are the sized-offer profile — the legacy `max_cpus`/`max_memory_mb`/`max_pids` ceilings remain until the free-form lease knobs are removed (task #570). The former `max_gpus` ceiling is **gone**, renamed to the exact `gpus` count (breaking; wisper-web is updated separately).

`effective_cpus`/`effective_memory_mb` and `resources_source` (task #578) resolve the size the consumer would actually get so a NULL-profile offer never renders as a blank "host default": each effective value is the offer's own `cpus`/`memory_mb` when set (`resources_source: "offer"`), else the host's advertised **per-lease cap** (`limits.max_cpus`/`max_memory_mb` from its live capability, `TUNNEL.md` §5 — `resources_source: "host_cap"`), else `null` when the host advertises no cap either, e.g. it is offline (`resources_source: "unknown"`). The raw `cpus`/`memory_mb` are **kept as-is** (the host editor needs them); the effective fields are display-only resolution and appear on `GET /v1/hosts/:id` too. `effective_cpus` may be fractional (it mirrors the host's advertised cap); a lease provisioned from such an offer stamps it rounded to a whole vCPU (see Leases).

`gpu_classes`/`gpu_count` mirror the host's advertised GPU (the distinct opaque device classes and total device count, `TUNNEL.md` §5, task #521). Both appear on `GET /v1/hosts/:id` too. The catalog filters `?min_gpus=` (keep only offers whose `gpus` ≥ the floor — inclusive) and `?gpu_class=` (keep only hosts advertising that exact opaque class) compose with the price/network/image filters; a host advertising no GPU is dropped by `gpu_class` and an offer with `gpus: 0` is dropped by any `min_gpus ≥ 1` (task #523).

`at_capacity` (task #571) tells the frontend when to badge a host **full**: it is `true` when the host advertises a concurrent-contract ceiling (`capacity.max_contracts`, `TUNNEL.md` §5) and its live non-terminal lease count has reached it. When a ceiling is advertised the entry also carries `active_leases` (the host's current non-terminal lease count) and `max_leases` (that ceiling). A host with **no** advertised ceiling — or an offline host with no live capability — is never at capacity: `at_capacity` is always `false` and `active_leases`/`max_leases` are **omitted (null)**. Both fields are optional; treat their absence as "unlimited". The same three fields appear on `GET /v1/hosts/:id`. A `POST /v1/leases` against a full host fast-fails with `409 at_capacity` before any hold or provisioning (see Leases and §11).

### Leases
| Method | Path | Auth extras | Notes |
|---|---|---|---|
| POST | `/v1/leases` | `Idempotency-Key` | create + provision a lease; returns `201` once the container is **ready** |
| GET | `/v1/leases` | | caller's leases, newest first; filter `?status=` (a `lease_status` label; an unknown value is `validation_error`); paginated |
| GET | `/v1/leases/:id` | | status, timeline, running cost |
| DELETE | `/v1/leases/:id` | | release (idempotent; safe to retry); returns the lease view. A host with no live tunnel is treated as already released and the lease is ended locally |
| POST | `/v1/leases/:id/exec` | | body `{ "command": "…" }`; sync exec → `{stdout,stderr,exit_code}` |
| POST | `/v1/leases/:id/exec?stream=1` | | same body; SSE stream (`chunk`/`exit`/`error` events) |
| POST | `/v1/leases/:id/shell-ticket` | | mint a one-time WS ticket (§7) |
| WS | `/v1/leases/:id/shell?ticket=…` | ticket | interactive PTY console (§7) |

**`POST /v1/leases`** request → response:
```json
// request  (Idempotency-Key: <uuid>)
{ "host_id": "…", "host_image_id": "…",
  "network": "open",
  "ttl_seconds": 3600,
  "userdata": "apt-get install -y git && …",
  "isolation": "sandboxed",
  "env": { "API_TOKEN": "…", "REGION": "eu" } }
// 201 (the relay has already waited for lease.ready, so the lease is active)
{ "id": "lease_…", "status": "active",
  "price_cents_per_min": 5, "currency": "usd",
  "hold_cents": 300, "ttl_seconds": 3600, "created_at": "…Z",
  "os": "linux" }
```
**Resources are fixed by the selected offer (task #570, breaking change).** An offer sells a size (task #569), so the consumer no longer chooses resources at lease time: a request that still carries a `resources` object **or** a top-level `gpus` count is rejected with `validation_error` ("resources are fixed by the selected offer"). The lease provisions **exactly** the offer's sized profile — its `cpus`/`memory_mb` (`null` = the host's own per-lease policy default applies downstream) and its exact `gpus` count. The former `disk_gb` knob is **gone entirely** (it was never enforced downstream). `network`/`ttl_seconds`/`userdata`/`isolation`/`env` are unchanged request inputs. *(wisper-web is updated separately.)*

**Per-host capacity (task #571).** Before the wallet gate posts a hold and before any tunnel frame, create checks the target host's advertised concurrent-contract ceiling (`capacity.max_contracts`, `TUNNEL.md` §5): if the host's live (non-terminal) lease count has reached it, the create fast-fails with `409 at_capacity` ("The host has reached its maximum number of concurrent leases.") — no hold is posted, no `lease.create` is sent. A host that advertises no ceiling is unlimited (the pre-#571 behavior). This is only the cheap manager-side guard; wisp stays authoritative, so if it rejects a create in the admit→provision race the agent reports `at_capacity` and the same `409 at_capacity` is returned to the caller (the failed-create teardown still runs). The host's live counts are surfaced as `at_capacity`/`active_leases`/`max_leases` in the catalog (§5).

`isolation` is the **optional** requested sandbox level, ordered `shared` < `sandboxed` < `vm` (`TUNNEL.md` §5, task #418). Omitted → the target host's advertised `default_isolation` (the tier the host operator chose to lead with, `§5`; `shared` for a host that declares none, so a single-tier host is unchanged); `confidential` or any unknown value → `validation_error`. It is resolved and validated server-side — against the admin-tunable `platform_policy.min_isolation` floor and, when the target host advertises isolation levels (task #417), against the levels that host can provide (a host with none recorded passes through, since wisp re-validates as the real security boundary) — then snapshotted immutably on the lease, returned on `GET /v1/leases/:id`, and forwarded on the `lease.create` frame.

The provisioned profile is snapshotted immutably on the lease and surfaced under `resources` on `GET /v1/leases/:id`; the `lease.create` frame carries the offer's `cpus`/`memory_mb`/`gpus` (each omitted from the frame when the offer left it unset / `0`, so wisp's own defaults apply). wisp enforces the real isolation/allocation.

`env` is an **optional, opaque `{string:string}` map** of create-time environment variables forwarded down the host tunnel for secret injection (mirrors `POST /dev/leases`; `lease.create` frame, `TUNNEL.md` §5). Capped like wisp's own limits — at most **128** entries and **256 KiB** serialized, else `validation_error`. Its **values are secrets-in-transit**: never logged, never echoed in errors, and **never persisted** on the lease row (the lease snapshot keeps everything *except* `env`) — and it is **plaintext v1**, so treat it as trusted-network only (`TUNNEL.md` §13). `os` echoes the host's advertised container OS (`"linux"` | `"windows"`, or `null` when the host is offline / its agent advertised none) like `GET /v1/leases/:id` (task #316).

Server flow, in order: validate ids/network/TTL/env/isolation against the host's priced allow-list (resources come from the offer, not the request; an unknown or admin-suspended host is `400 image_not_allowed`; a `ttl_seconds` above the offer's `max_ttl_seconds` or above the active `platform_policy.max_ttl_seconds_cap` global ceiling (task #181) is `validation_error`, never silently clamped) → per-host capacity check (`409 at_capacity`) → degraded-host check (`409 host_offline`) → the **wallet gate** (`PAYMENTS.md` §4: per-user concurrency cap `409 at_capacity`, daily spend cap `429 limit_exceeded`, then the balance check, `402 insufficient_funds`; nothing is posted yet and **no** `lease.create` frame is sent on failure) → `lease.create` (carrying the offer's sized profile) down the host tunnel, waiting for `lease.accepted` and then `lease.ready` (`host_offline`/`upstream_timeout`/`lease_failed` on failure) → the `leases` row is inserted directly as `active` with the meter started → the `lease_hold` is posted (if it fails, e.g. the wallet was drained in the race, the container is torn down with `lease.release`, the row is marked `failed` with `end_reason = payment_failed`, and the error surfaces) → `201`. Because the create waits for readiness, the returned lease is already `active`; `GET /v1/leases/:id` is for later transitions (suspend/resume/end), since the events stream below is not implemented.

**`GET /v1/leases/:id`**:
```json
{ "id":"lease_…","status":"active","host_id":"…","image_ref":"…",
  "network":"open",
  "resources":{"cpus":2,"memory_mb":4096,"gpus":1,
    "effective_cpus":2,"effective_memory_mb":4096,"resources_source":"offer"},
  "ttl_seconds":3600,
  "price_cents_per_min":5,"currency":"usd","created_at":"…Z",
  "started_at":"…Z","billable_seconds":742,"cost_cents_so_far":62,
  "expires_at":"…Z","ended_at":null,"end_reason":null,"isolation":"sandboxed","os":"linux" }
```
`resources` is the **provisioned profile stamped from the offer** (task #570): `cpus`/`memory_mb` are the raw stamped snapshot, and `gpus` is the booked whole-device count (`0` = none). It is not a consumer input; it reflects exactly what the flat per-offer price bought. `status` is one of `pending`, `provisioning`, `active`, `suspended`, `ended`, `failed` (as built, a create lands directly in `active`, `DATA_MODEL.md` §5); `end_reason` is one of `released`, `expired`, `host_disconnect`, `container_lost`, `admin`, `payment_failed`. `cost_cents_so_far` is `⌊billable_seconds × price_cents_per_min / 60⌋`, the same integer formula the meter posts (`DATA_MODEL.md` §14), and `expires_at` is `started_at + ttl_seconds`.

Create now **resolves and stamps** the profile so a lease never records an unknown size (task #578): when the offer left `cpus`/`memory_mb` NULL, the row is stamped from the host's advertised **per-lease cap** (`limits.max_cpus`/`max_memory_mb`, `TUNNEL.md` §5) rounded to whole vCPUs, leaving NULL only when the host advertises no cap either (e.g. offline). The `lease.create` frame is **unchanged** — it still omits what the offer left unset so wisp's own defaults keep applying; the stamp is bookkeeping/display, not a provisioning change. The read `resources` mirrors the catalog: `effective_cpus`/`effective_memory_mb` are the stamped value when present, else the host's live per-lease cap (so an existing NULL-stamped row still resolves on read — no migration), else `null`, and `resources_source` is `"offer"` | `"host_cap"` | `"unknown"` accordingly.

### Billing
| Method | Path | Auth extras | Notes |
|---|---|---|---|
| GET | `/v1/billing` | | `{ balance_cents, currency, usage: { lease_count, active_lease_count, billable_seconds, spent_cents } }` (balance is the ledger-derived `user_wallet` balance) |
| GET | `/v1/billing/transactions` | | the caller's wallet statement (top-ups, holds, releases, refunds, chargebacks); items `{ id, kind, amount_cents (signed on the wallet), currency, lease_id, external_ref, memo, created_at }`; paginated |
| POST | `/v1/billing/topup` | `Idempotency-Key` | body `{ "amount_cents" }` (≥ `platform_policy.min_topup_cents`, else `402 payment_required`; fraud caps `429 limit_exceeded`) → `200 { client_secret }` (`PAYMENTS.md` §3) |
| POST | `/v1/billing/payment-methods` | | SetupIntent to save a method → `{ client_secret }` |
| GET/PUT | `/v1/billing/auto-recharge` | | *planned, not implemented*: threshold + amount + on/off (`PAYMENTS.md` §3) |
| POST | `/v1/billing/refund` | `Idempotency-Key` | body `{ "amount_cents", "payment_intent"? }`; refund unspent wallet credits against a top-up (the named `pi_…`, else the most recent) → `200 { refund_id, amount_cents, currency, balance_cents }`; `402 insufficient_funds` above the unspent balance, `409 conflict` when there is no top-up to refund (`PAYMENTS.md` §3, §7) |

## 6. Host API (min role: `host`)

Becoming a host is additive — a `consumer` gains the `host` group on first host action; existing consumer/lease access is unchanged. The grant takes effect on the caller's *current* token: the `host` gate (and `GET /v1/me`) derive the `host` role from DB host-ownership, so a consumer who has registered a host passes these host-gated endpoints immediately, with no re-login, even though their in-flight token was minted before the `host` group landed (§2). The Cognito group add still happens so future fresh tokens carry `host` independently. API-key callers are unaffected — they authorize by explicit scopes, not ownership (§2).

| Method | Path | Auth extras | Notes |
|---|---|---|---|
| POST | `/v1/hosts` | min role **`consumer`** (registering is how a consumer becomes a host) | body `{ "name"?, "label"? }`; register a wisp host → `201` with the **agent token once** (never retrievable again) |
| GET | `/v1/hosts/mine` | | `{ "data": [host summaries], "earnings": {…} }`: the caller's hosts with live `online` state, advertised `gpu_classes`/`gpu_count`, `host_max_cpus`/`host_max_memory_mb`, `agent_token_prefix`, plus the owner-scoped earnings summary (same shape as `GET /v1/earnings`) |
| GET | `/v1/hosts/:id` | | the same public detail as the catalog route (§5); there is no separate owner-scoped variant today |
| DELETE | `/v1/hosts/:id` | | *planned, not implemented*: deregister (drains pending earnings first) |
| POST | `/v1/hosts/:id/agent-token` | | rotate the agent token (old one revoked, tunnel closed `4402`) → `{ id, agent_token, agent_token_prefix, manager_ws, tunnel_closed }` |
| GET | `/v1/hosts/:id/images` | | `{ "data": [offers], "host_max_cpus", "host_max_memory_mb" }`: the full priced allow-list including disabled entries |
| PUT | `/v1/hosts/:id/images` | `validation_error` if a priced image is enabled without Connect, or an offer exceeds a host per-lease cap; `409 host_offline` when an enabled entry has no live tunnel to validate against | body `{ "images": [ { image_ref, price_cents_per_min, networks[], max_ttl_seconds, max_cpus?, max_memory_mb?, max_pids?, enabled?, cpus?, memory_mb?, gpus? } ] }`; whole-list replace validated live against the host's advertised wisp capability. Omitted refs are removed (soft-disabled instead when a lease ever referenced them); disabled entries skip capability validation so a stale offer can always be retracted |
| PATCH | `/v1/hosts/:id/images/:imageId` | same rules as PUT | price/enable/limits/networks + the sized profile (`cpus`/`memory_mb`/`gpus`) for one image; every field optional, `image_ref` immutable |
| POST | `/v1/hosts/connect` | | create/continue Stripe **Connect Express** onboarding → `{ onboarding_url, connect_status }` (needs `Stripe:ConnectRefreshUrl`/`ConnectReturnUrl` configured, else `500 internal`) |
| GET | `/v1/hosts/connect/status` | | `{ connect_status, charges_enabled, payouts_enabled, details_submitted, can_go_online, requirements: { disabled_reason, currently_due[], past_due[], eventually_due[], pending_verification[] } }`; reads the live Stripe account and reconciles the stored status opportunistically |
| GET | `/v1/earnings` | | `{ currency, accrued_cents, paid_cents, payout_min_cents, connect_status, can_receive_payouts }` (lifetime totals, not by period) |
| GET | `/v1/earnings/payouts` | | `{ "data": [ { id, amount_cents, currency, status, stripe_transfer_id, error, period_start, period_end, created_at } ] }`, newest first |
| POST | `/v1/payouts` | `Idempotency-Key`; `403 connect_incomplete`; `402 payment_required` below `payout_min_cents` | on-demand payout of the whole accrued balance → `200` with the payout row (which may itself be `failed` if the transfer could not be made) (`PAYMENTS.md` §6) |

**`POST /v1/hosts`** response (token shown once):
```json
{ "id":"<uuid>","name":"home-server-1",
  "agent_token":"wht_live_<64-hex>","agent_token_prefix":"wht_live_a1b2",
  "manager_ws":"wss://<wisper-host>/agent",
  "status":"offline" }
```
`manager_ws` is `Tunnel:ManagerWebSocketUrl` from config. The agent token is stored as a SHA-256 hash (`DATA_MODEL.md` §4). On success a JWT caller is also added to the Cognito `host` group (best-effort, when `Auth:UserPoolId`/`Auth:Region` are configured). Pricing/earning is inert until `connect_status='enabled'` (or every enabled image is free) and the agent has connected and advertised capability (`TUNNEL.md` §3).

**Presence follows the tunnel.** A host's `status` is driven by its agent tunnel, not set by hand: when the handshake completes Wisper flips it `online` if it clears the earning gate — the owner is Connect-enabled **or** every enabled `host_image` is priced at `0` cents/min (the self-hosted / zero-price posture, `PAYMENTS.md` §5) — and a durable tunnel loss (grace expiry, or a close with no leases to protect) flips it back `offline` (`TUNNEL.md` §8). An admin-suspended host never comes back `online` on reconnect.

**Charging requires Connect (images surface).** Because the online gate has a zero-earn arm, `PUT`/`PATCH …/images` **reject enabling a non-zero-priced image while the owner's `connect_status` is not `enabled`** (`validation_error` explaining Connect onboarding is required to charge; price at `0` to self-host for free). This is the single pricing mutation point, so a live host is never knocked `offline` mid-tunnel — it simply cannot move into the earning arm without Connect (`PAYMENTS.md` §5).

**Offer honesty — an offer cannot exceed the host's per-lease cap.** `PUT`/`PATCH …/images` validate every sized offer against the host's advertised per-lease caps (wisp `limits.max_cpus`/`max_memory_mb`, `TUNNEL.md` §5): an offer whose `cpus` exceeds `max_cpus`, or whose `memory_mb` exceeds `max_memory_mb`, is **rejected** with `validation_error` naming the field and the cap (`details: { field, max }`) — never clamped, the same discipline as `gpus ≤ gpu_count`. This closes the gap where the catalog could advertise resources the host would silently clamp at provision time; a host that advertises no cap for a dimension imposes no bound on it. The live caps are surfaced to the owner as `host_max_cpus`/`host_max_memory_mb` on `GET /v1/hosts/mine` **and** on the images responses so the editor prefills real numbers in one fetch (each is `null` when the host is offline or advertises no cap). PUT is a whole-list replace, so one over-cap entry fails the entire save; an over-cap row already saved (bad data) is not migrated — it simply fails the next save until corrected, while `#578`'s effective-resolution already caps what the catalog claims.

## 7. WebSocket & streaming (the console relay)

The consumer console and live output flow **through** Wisper to the host tunnel (`TUNNEL.md` §11) — consumers never touch a host directly.

- **Sync exec** — `POST /v1/leases/:id/exec {command}` → `{stdout,stderr,exit_code}`. Normal JWT header auth. `409 lease_not_ready` unless `active`.
- **Streamed exec** — `POST /v1/leases/:id/exec?stream=1` → **SSE** (`event: chunk` `{stream,data}`, `event: exit` `{exit_code}`, `event: error`). Uses `fetch`+reader on the client (JWT header, like wisp-dashboard) — *not* `EventSource`, which can't set headers.
- **Interactive shell** — a raw **WebSocket** at `/v1/leases/:id/shell`:
  1. **Ticket first:** the client calls `POST /v1/leases/:id/shell-ticket` (JWT header) → `{ "ticket":"tkt_…", "expires_in": 30 }`. Single-use, ~30s TTL, bound to `(user, lease)`.
  2. **Connect:** `WS /v1/leases/:id/shell?ticket=tkt_…&cols=120&rows=32` (`cols`/`rows` optional, default 80x24). Wisper redeems the ticket (one-time; any presentation, valid or not, consumes it), re-checks ownership and readiness against the ticket's user, then bridges to a tunnel `shell` stream. **The JWT never appears in a URL**; the ticket does, and it is single-use and short-lived. Handshake failures are plain HTTP statuses before the upgrade: `400` (not a WebSocket request), `401` (unknown/expired/used/wrong-lease ticket), `404` (malformed id or lease no longer visible), `409` (lease no longer `active`). If the tunnel shell cannot be opened after the upgrade the socket closes with code `1011` and the error code (`host_offline`, `upstream_timeout`, …) as the reason.
  3. **Frames:** **binary** frames carry terminal bytes (client→PTY stdin, PTY→client stdout); a small **JSON text** control frame `{ "t":"resize", "cols":120, "rows":32 }` sends window size (malformed control text is dropped). This mirrors the tunnel's shell framing so the relay is a passthrough. When the PTY ends the consumer socket closes `1000`; a `flow_violation` or `host_offline` closes it `1011`.
  4. **Backpressure:** the relay honors the tunnel's per-stream credit flow control (`TUNNEL.md` §9) end-to-end to the browser socket.
  5. **Multi-instance:** tickets live in Redis when the backplane is enabled with a Redis connection (`{prefix}:ticket:<tkt>`, 30 s TTL, redeemed with `GETDEL`), so a ticket minted on one instance redeems on another (`DESIGN.md` §7).
- **Lease events (optional live status):** *planned, not implemented* — `WS /v1/leases/:id/events?ticket=…` (same ticket scheme) streaming lifecycle JSON (`provisioning→active→…`) so the UI needn't poll. Today clients poll `GET /v1/leases/:id`.

## 8. Admin API (min role: `admin`)

| Method | Path | Notes |
|---|---|---|
| GET | `/v1/admin/overview` | `{ currency, revenue_cents (platform_revenue balance), wallet_liability_cents, host_earnings_cents, active_lease_count, host_count, online_host_count, user_count, health, ledger_reconcile }`; `ledger_reconcile` is `{ ran_at, accounts_checked, drift_account_count, total_absolute_drift_cents, has_drift }` from the most recent scheduled reconciliation pass on this instance (`DATA_MODEL.md` §7e; `ran_at` is null until the first pass completes). `health` reads `"ok"` normally and flips to `"ledger_drift"` when the last observed reconcile pass saw non-zero drift; no other downstream is probed today |
| GET | `/v1/admin/policy` | `{ "active": policy\|null, "versions": [policies newest first] }` |
| PUT | `/v1/admin/policy` | body `{ fee_bps (required, 0..10000), min_topup_cents?, max_concurrent_leases_per_user?, max_ttl_seconds_cap?, min_isolation?, first_topup_max_cents?, new_account_window_hours?, new_account_max_topup_cents_per_day?, max_spend_cents_per_day?, effective_from? }`; appends a new **versioned** policy row (never edits), audited as `policy.update`. `max_ttl_seconds_cap` is enforced at lease create as a global ceiling over the per-image `max_ttl_seconds` (task #181): a request whose `ttl_seconds` exceeds it is rejected with `validation_error` (never silently clamped); NULL / no active policy = no global ceiling |
| GET | `/v1/admin/hosts` · `/v1/admin/users` | `?query=&limit=&offset=` search (name/label/email substring or exact id) → `{ "data": [...], "next_offset": n\|null }`. Each admin host row carries `{ id, owner_user_id, name, label, status, isolation_levels, default_isolation, gpu_classes, gpu_count, wisp_version, agent_version, max_leases, max_streams, last_seen_at, created_at }`; `wisp_version` / `agent_version` / `max_leases` / `max_streams` (all nullable) are what the connected agent advertised at hello and are persisted on the `hosts` row (task #182, `DATA_MODEL.md` §4). They are advisory surfacing only: per-host admission uses the live `capability.capacity.max_contracts` snapshot (task #571), never the persisted `max_leases`. The agent token hash is never exposed |
| POST | `/v1/admin/hosts/:id/suspend` · `/unsuspend` | moderation (audited `host.suspend`/`host.unsuspend`); unsuspend returns the host to `offline` and the tunnel lifecycle brings it back online |
| POST | `/v1/admin/users/:id/suspend` · `/unsuspend` | moderation (audited `user.suspend`/`user.unsuspend`); a `deleted` user is `409 conflict` |
| POST | `/v1/admin/refunds` | `Idempotency-Key`; body `{ user_id, amount_cents, payment_intent?, reason? }` | manual refund of a user's unspent credits, same path as `POST /v1/billing/refund` (audited `admin.refund`) |
| POST | `/v1/admin/adjustments` | `Idempotency-Key`; body `{ debit_account_id, credit_account_id, amount_cents, reason? }` | ledger `adjustment` txn, the *only* way to hand-correct money, always balanced + audited (`ledger.adjustment`, `DATA_MODEL.md` §7,§12) → `{ transaction_id, amount_cents, debit_account_id, credit_account_id, debit_balance_cents, credit_balance_cents }` |
| GET | `/v1/admin/leases` | `?status=&past_ttl=&limit=&offset=`: every **non-terminal** lease (`active` + `suspended`), oldest first, so an operator can find stuck leases; items carry the timeline incl. `last_metered_at`, `suspended_at`, `expires_at` |
| POST | `/v1/admin/leases/:id/end` | force-end a stuck lease from any non-terminal state (`lease_<hex>` or plain uuid id): finalize billing, CAS transition to `ended` (`end_reason = admin`), release the hold, best-effort `lease.release` down the tunnel; idempotent on a terminal lease; audited `lease.admin_end` |
| GET | `/v1/admin/audit` | `?actor=&target_type=&target_id=&action=&limit=(default 50, max 200)&cursor=`; newest first, keyset cursor on the row id |
| GET | `/v1/admin/ledger/accounts` | `?kind=&owner_user_id=&limit=&offset=`: paged ledger-account listing (task #194) → `{ "data": [{ id, kind, owner_user_id, owner_email, currency, balance_cents }], "next_offset": n\|null }`; `kind` is snake_case (`user_wallet`, `platform_revenue`, `lease_holds`, `host_earnings`, `platform_cash`, `stripe_fees`) and unknown values are `validation_error`; `owner_email` is the joined `users.email` when the account has an owner, `null` for platform singletons (owner NULL). This is the lookup an operator uses to find the two account ids `POST /v1/admin/adjustments` requires (a `platform_revenue` singleton and a user's wallet) without opening the database |
| GET | `/v1/admin/ledger/accounts/:id` | `{ account, entries }`: ledger account + its journal entries, newest first (read-only forensics) |

Every admin write records an `audit_log` row with actor + before/after (`DATA_MODEL.md` §12). All admin routes bootstrap the admin's own `users` row on first call.

## 9. Idempotency

- **Required** on `POST /v1/leases`, `/v1/billing/topup`, `/v1/billing/refund`, `/v1/payouts`, `/v1/admin/refunds`, `/v1/admin/adjustments` via the `Idempotency-Key` header (client-generated UUID).
- A missing header is `400 validation_error` (`details.header = "Idempotency-Key"`).
- Semantics (`DATA_MODEL.md` §10): first request runs and stores its response under the key; a **replay with the same key + same body** returns the stored response verbatim; **same key + different body** (or the same key presented by a different user) → `409 conflict`; a replay while the first is still in-flight → `409` (in-progress lock). A request that **fails** releases the lock, so the same key can be retried once the condition clears. Records live 24 h; an expired record is swept both lazily (when its key is presented again) and proactively by a scheduled background loop (`IdempotencySweep:Enabled` / `IdempotencySweep:IntervalMinutes`, default hourly; off in the in-memory persistence mode, multi-instance safe via a Postgres advisory lock).
- Below the API, `ledger_transactions.idempotency_key` provides a second, DB-level guarantee, so even a bug in the API layer cannot double-post money.

## 10. Pagination, filtering, sorting

- Cursor-based: `?limit=&cursor=`; opaque `next_cursor` (null at end). Cursors encode a stable sort key (`created_at,id` desc for catalog, leases and billing transactions; the row id for the audit log), so inserts during paging don't duplicate/skip. A malformed `cursor` or an out-of-range `limit` is `400 validation_error`. (Exception: `GET /v1/admin/hosts`, `/v1/admin/users` and `/v1/admin/leases` use plain `?limit=&offset=` paging and return `next_offset`.)
- Documented filters per endpoint (e.g. leases `?status=`, catalog `?image=&network=&max_price_cents_per_min=&min_gpus=&gpu_class=`). Unknown query params are ignored, not errored; a malformed value of a known filter is `400 validation_error`.

## 11. Rate limiting & quotas

- *Planned, not implemented:* per-user token bucket (anonymous/`/healthz` excluded), stricter buckets on money endpoints and lease creation, `429 rate_limited` + `Retry-After`, `X-RateLimit-*` on every response. No generic rate limiter runs today.
- **Business quotas** (distinct from rate limits, and fully implemented): `max_concurrent_leases_per_user` returns `409 at_capacity`; the fraud-guard caps (first-top-up, new-account top-up velocity, daily lease spend — `PAYMENTS.md` §7) return `429 limit_exceeded`; an unaffordable hold returns `402 insufficient_funds`. All come from `platform_policy` and are enforced server-side regardless of client behavior.
- **Per-host admission** (task #571): a host advertises its concurrent-contract ceiling on the tunnel (`capacity.max_contracts`, `TUNNEL.md` §5). `POST /v1/leases` counts the host's live (non-terminal) leases and, when the ceiling is reached, fast-fails with `409 at_capacity` **before** posting any wallet hold or sending a tunnel frame — the message names the *host* so it is distinguishable from the per-user `at_capacity` above. A host that advertises no ceiling is unlimited. wisp remains the authoritative enforcer: if it rejects a create in the admit→provision race, the agent reports `at_capacity` and the same `409 at_capacity` surfaces to the caller.

## 12. Observability & correlation

- `X-Request-Id` on every response and as `request_id` in every structured log line and error envelope for that request. It is **not** carried on tunnel frames or ledger rows today; those are correlated by lease id / idempotency key in the logs.
- Structured JSON logs to stdout (one line per event, UTC). Metrics and downstream health (Postgres, Redis, Stripe) are not exposed yet: `/v1/admin/overview.health` reads `"ok"` normally and flips to `"ledger_drift"` when the last scheduled ledger reconciliation pass saw non-zero drift (the pass and its `ledger_reconcile` payload are described in §8 and `DATA_MODEL.md` §7e). Only `/healthz` probes the database.

## 13. Deliberate scope boundaries

- **No public/unauthenticated catalog** in v0 — browsing requires a (free) consumer account. A marketing-only anonymized catalog can be added as a separate public route later without touching the authed surface.
- **No third-party OAuth-app model** yet. First-party **machine API keys** (§2, `wck_live_…`) exist for a machine client driving the *consumer* surface as its owning user; a broader external-integrator model (OAuth apps, per-app service principals, finer scopes) is additive when there's demand.
- **No GraphQL** — a typed REST surface is sufficient and simpler to secure, cache, and rate-limit for these access patterns.
- **Webhooks *out* to hosts/consumers** (e.g. "lease.ended" callbacks) are out of scope — the live WS/SSE surface covers real-time needs; outbound webhooks attach additively if integrators ask.
```
