# Wisper — Design Doc

**Status:** Draft / v0 · **Stack:** C# / ASP.NET Core (Kestrel) manager · Go wisp agent · Next.js + MUI frontends · **raw WebSockets end-to-end** (no SignalR) · **Author:** Ben
**One-liner:** Wisper is a hosted marketplace and control-plane for [wisp](../../wisp/docs/DESIGN.md). Hosts run wisp on their own machines and dial out to Wisper; consumers buy metered, per-minute container leases from any host; hosts get paid for the compute they rent out.

---

## 1. The idea

[wisp](../../wisp/docs/DESIGN.md) leases a throwaway root container on **one machine**. **Wisper** turns that into a **two-sided marketplace**:

- **Hosts** run a wisp instance on their own hardware (home server, spare box, cloud VM) and run the **wisp agent**, which dials *out* to Wisper over a persistent WebSocket. The host advertises an image allow-list and a **per-minute price**.
- **Consumers** sign up on the Wisper website, browse hosts and their priced images, and buy a **lease** — a live, root container they drive (shell / exec / stream) exactly like wisp, except every byte flows through Wisper.
- **Wisper** is the broker-of-brokers and the **billing chokepoint**: it authenticates both sides, relays the wisp API over each host's tunnel, **meters** lease-minutes, charges the consumer, and pays the host (minus a platform fee).
- **Admin** (Ben) sets platform policy, adjusts pricing rules, and moderates hosts and users.

The host has **no public IP and no inbound ports** — the agent's *outbound* connection is the only channel, which sidesteps NAT/dynamic-IP entirely. Wisper always has a stable address; the host always knows it.

## 2. Core principles

1. **Wisp stays domain-blind.** Wisp knows nothing about money, accounts, or Wisper. The agent is a thin bridge that speaks wisp's existing HTTP/WS API locally and tunnels it out. Billing, identity, and the marketplace live entirely in Wisper.
2. **Wisper is the metering authority.** The clock that bills is Wisper's, not the host's. A host cannot under-report minutes to dodge charges or over-report to inflate payouts. The host reports lifecycle *events*; Wisper timestamps them.
3. **The tunnel is the only path.** Consumers never talk to a host directly (they can't route to it anyway). All lease traffic is `consumer → Wisper → tunnel → host agent → local wisp → container`. That makes Wisper an unavoidable chokepoint for auth and billing.
4. **Outbound-only hosts.** Hosts open connections; Wisper never connects in. This is what makes "run a host behind any home router" work.
5. **Ephemeral by inheritance.** Leases are wisp contracts: TTL-bounded, destroyed on release/expiry, enforced locally by wisp's own reaper even if the tunnel drops. Wisper adds nothing that survives a lease.
6. **Frontends on Vercel, API on your own host.** Wisper's public and admin sites are Vercel SPAs that call the Wisper API; the API runs behind an API gateway / load balancer.

## 3. What Wisper is / is NOT

**Is:** an identity + billing + relay layer over a fleet of wisp instances; a marketplace where compute supply (hosts) meets demand (consumers); the metering + payments authority.

**Is NOT:**
- Not a container runtime — wisp does that. Wisper never touches Docker.
- Not tool-aware — inherits wisp's "bare base + userdata" model; it never knows what runs inside a lease.
- Not a place hosts' or consumers' data persists — leases are ephemeral; Wisper stores accounts, pricing, and billing records only.
- Not a public-IP requirement for hosts — the tunnel is outbound-only.

## 4. System architecture

```
      ┌────────────── Vercel ──────────────┐        ┌──────── Host machine (any NAT) ────────┐
      │  public site        admin site      │        │  wisp agent ──local──> wisp (wispd)     │
      │  (consumers)        (admin/hosts)    │        │      │                    │             │
      └──────┬──────────────────┬───────────┘        │      │ outbound wss       └─ Docker ────┘
        HTTPS│            HTTPS  │                     │      │  (ping/pong)          containers  │
             ▼                   ▼                     └──────┼──────────────────────────────────┘
   ┌────────────────────── API gateway / LB (TLS) ───────────┼──────┐
   │                         reverse proxy                    │ wss  │
   │                                │                         ▼      │
   │                     ┌──────── Wisper API (C#) ──────────────────┐│
   │  consumer REST/WS ──┤ consumer API   host tunnel   admin API    ││
   │                     │      │              │            │        ││
   │                     │   Postgres     Redis (backplane)   Stripe ││
   │                     └──────────────────────────────────────────┘│
   └──────────────────────────────────────────────────────────────────┘
```

Three planes:
- **Consumer plane** — REST + WebSocket for browsing, leasing, and driving containers.
- **Host plane** — the persistent agent tunnel (one per connected host).
- **Admin plane** — REST for policy, pricing, moderation, and payouts.

State: **PostgreSQL** (accounts, hosts, pricing, leases, billing), **Redis** (the multi-instance backplane — §7), **Stripe** (money).

## 5. The host: wisp agent + tunnel

The host runs two processes:
- **wisp (`wispd`)** — unchanged; the local broker on `127.0.0.1`, with its image allow-list + limits config (`WISP_CONFIG`).
- **wisp agent** — new, a **standalone Go binary in its own `wisp-agent` repo** (not a mode of `wispd`). Run as `wisp-agent --manager wss://<wisper-host>/agent --host-token … --wisp http://127.0.0.1:8080 --wisp-token …`. It dials the Wisper tunnel and bridges frames to local wisp calls, holding a **host agent token** (for the tunnel, long-lived/revocable) and the local wisp app token (to talk to `wispd`). Keeping it separate keeps wisp itself fully marketplace-blind — the agent is just another wisp client.

The agent's job is small and mechanical:
1. Open the outbound WebSocket to Wisper, authenticate with the host token.
2. On connect, **advertise capability**: the wisp `GET /images` document (allow-list, default, limits, `os`, the effective `isolation_levels`/`default_isolation`, and the `gpu` block — devices with opaque class strings, per-lease `max_gpus`, `TUNNEL.md` §5) so Wisper knows what this host can offer. (Prices are set in Wisper, not by wisp — see §12.)
3. Receive tunnel frames and translate them to local wisp API calls (`POST /contracts`, `exec`, shell WS, `DELETE`), streaming results/bytes back up the tunnel with the same stream id.
4. Send periodic **heartbeat** (current leases, load) and keep the socket alive with **ping/pong** (§6).

Because the agent only speaks wisp's public API, **wisp itself needs no marketplace awareness** — the only genuinely new wisp-side work is the agent bridge.

## 6. The tunnel protocol

One persistent WebSocket per host, carrying **many multiplexed logical streams** (control, exec, shell) so Wisper never needs a second connection to a host.

**Transport decision — raw WebSockets end-to-end, no SignalR.** The Go agent is a custom client that speaks raw WebSockets (`System.Net.WebSockets` on the Kestrel side), so the host tunnel is raw regardless. The *consumer* side is the same shape — a dumb byte-relay of PTY/exec bytes into xterm.js — where SignalR's hub framing, RPC, and transport fallback add friction and little value. So both agent and consumer connections use raw WebSockets with the framing below, giving **one** transport, **one** framing protocol, and **one** backplane (§7) across the whole system. SignalR is reconsidered only if the frontend later grows rich non-console real-time features (marketplace presence, notifications) — and even then only for those, never as the tunnel transport.

### Keepalive: ping / pong (required)

- Both peers send **WebSocket ping frames every 30s**; the other replies **pong**. This keeps NAT bindings and the **load-balancer idle timeout (e.g. 900s)** alive with wide margin, and — more importantly — **detects dead peers fast**. (As built, dead-peer detection is an application **inactivity window** — no frame of any kind within ~2.5 × ping — rather than pong counting; `TUNNEL.md` §7 is authoritative.)
- Ping/pong are protocol-level (control frames), independent of application heartbeat. Application heartbeat (lease list, load) rides as a normal data frame on its own interval (e.g. 15s) and is *also* how Wisper reconciles lease state after a reconnect.
- On the agent side, a dropped tunnel triggers **exponential-backoff reconnect** (with jitter). On reconnect the agent re-advertises capability and re-syncs live leases; Wisper reconciles (a lease whose host vanished is ended — see §14).

### Framing

Every frame is JSON (control) or binary (shell/stream bytes) tagged with a small header:

```
{ "t": "<frame type>", "sid": "<stream id>", "cid": "<correlation id>", ... }
```

- `sid` (stream id) multiplexes concurrent operations over the one socket (e.g. two shells + a stream on the same lease).
- `cid` correlates a request with its response.
- Binary frames (shell keystrokes/output, streamed exec chunks) carry `sid` in a compact prefix so raw PTY bytes aren't JSON-wrapped.

Representative frame types:

| Direction | Type | Meaning |
|---|---|---|
| W→H | `lease.create` | boot a contract: `{image, network, isolation, resources (cpus/memory_mb/pids/gpus), ttl_seconds, userdata, env}` |
| H→W | `lease.ready` / `lease.failed` | contract provisioned (or not); carries the wisp `contract_id` |
| W→H | `exec` | run a command (sync or `stream:true`) on a lease's stream `sid` |
| H→W | `exec.chunk` / `exec.exit` | streamed output / exit code for `sid` |
| W↔H | `shell.open` / `shell.data` / `shell.close` | interactive PTY over `sid` (binary `shell.data`) |
| W→H | `lease.release` | destroy the contract |
| H→W | `lease.ended` | contract released/expired locally |
| H→W | `host.heartbeat` | live leases + load |
| both | (ws ping/pong) | keepalive + liveness |

The consumer-facing side of each of these is a normal REST/WS call to Wisper; Wisper is a **relay** that maps a consumer request to a tunnel frame on the right host and streams the reply back.

## 7. Multi-instance backplane (Redis)

A tunnel WebSocket is **pinned to one Wisper instance** (the TCP socket lives on that box). When Wisper autoscales behind the load balancer:

- Host H's agent connects to instance **A**; A records `host:H → instanceA` in Redis and subscribes to a channel for the tunnels it owns.
- A consumer request for a lease on H may land on instance **B**. B looks up H's owner (A) and **publishes** the frame to A's channel (with a correlation id); A writes it down H's socket. The reply flows back to B the same way.

So Redis is the **routing backplane** that lets any instance drive any host's tunnel. It also holds ephemeral presence (which hosts are online, on which instance) and short-lived lease routing. **Not required until >1 instance runs**, but the manager is built to use it from day one so autoscaling is transparent. (Uses a managed Redis; a dedicated cluster later for prod isolation.)

The **same** hand-rolled `StackExchange.Redis` pub/sub mechanism routes **both** agent tunnels and consumer connections — a consumer WS pinned to instance B and a host tunnel pinned to instance A are bridged through Redis identically. This is exactly the piece SignalR's backplane would have provided; since we need it for the agent side regardless (SignalR can't help there), we build it once and use it everywhere (§6).

## 8. API surface (v0 sketch)

All under the Wisper host: `https://<wisper-host>/…` (a separate host for dev).

**Consumer (Cognito JWT):**
```
GET  /hosts                      online hosts + their priced images
GET  /hosts/:id/images           image allow-list + per-minute price + limits
POST /leases                     buy a lease { host_id, image, network, resources, ttl_seconds, userdata }
GET  /leases/:id                 status, elapsed minutes, running cost
DELETE /leases/:id               release early
POST /leases/:id/exec            run a command (sync)  → relayed
POST /leases/:id/exec?stream=1   streamed output (SSE) → relayed
WS   /leases/:id/shell           interactive PTY       → relayed
GET  /billing                    current balance, invoices, usage
```

**Host (host account JWT + agent token):**
```
POST /hosts                      register a wisp host
GET  /hosts/mine                 my hosts, online state, earnings
PUT  /hosts/:id/pricing          set per-image per-minute prices, enable/disable images
POST /hosts/:id/agent-token      (re)issue the agent token
WS   /agent                      the tunnel (agent connects here with the host token)
GET  /earnings                   payouts, pending balance
```

**Admin (Cognito `admin` group):**
```
GET  /admin/overview             platform revenue, active leases, host/consumer counts
PUT  /admin/policy               platform fee %, global caps, allowed base images
POST /admin/hosts/:id/suspend    moderate a host
POST /admin/users/:id/suspend    moderate a consumer
GET  /admin/audit                audit log
```

**Public:** `GET /healthz`, `GET /catalog` (unauth marketplace browse).

*(This section was the v0 sketch; the shipped surface is `/v1`-prefixed with some renames — catalog browse is `GET /v1/catalog` + `GET /v1/hosts/:id`, the shell needs a `shell-ticket` first, and there are additional shipped surfaces: `/v1/me`, `/v1/me/api-keys`, `/v1/hosts/connect`, `/v1/billing/*`, `/v1/earnings/*`, `/stripe/webhook`. `API.md` is authoritative.)*

## 9. Data model (Postgres)

*(Sketch — `DATA_MODEL.md` and the migrations are authoritative. Notable deltas from this sketch as shipped: `users` has no `kind` column — roles come from Cognito groups/api-key scopes — and the Connect columns are `connect_account_id` + a `connect_status` enum; `usage_records` shipped as **`lease_usage`** with `period_start/period_end/billable_seconds/charge_txn_id` and no Stripe usage-record linkage — charging is internal-ledger, §11; `hosts` also carries `isolation_levels`/`default_isolation` and `gpu_classes`/`gpu_count`; `host_images` also `max_pids`/`max_gpus`/`min_isolation`; `leases` also `isolation` and `gpus`. Money truth lives in the **double-entry ledger** — `ledger_accounts`/`ledger_transactions`/`ledger_entries` — plus `api_keys`, `stripe_events`, `idempotency_keys`, and versioned `platform_policy`, all absent from this sketch.)*

```
users            id, cognito_sub, email, roles-via-groups,
                 stripe_customer_id, connect_account_id, connect_status, status, created_at
hosts            id, owner_user_id, name, label/region, status(online|offline|suspended),
                 agent_token_hash, wisp_version, isolation_levels[], default_isolation,
                 gpu_classes[], gpu_count, last_seen_at, created_at
host_images      id, host_id, image_ref, price_cents_per_min, networks[], max_ttl_seconds,
                 max_cpus, max_memory_mb, max_pids, max_gpus, min_isolation, enabled
leases           id, consumer_user_id, host_id, image_ref, network, resources_json, gpus,
                 isolation, ttl_seconds, price_cents_per_min, status(pending|active|ended|failed),
                 wisp_contract_id, created_at, started_at, ended_at, end_reason
lease_usage      id, lease_id, period_start/end, billable_seconds, amount_cents,
                 platform_fee_cents, host_payout_cents, charge_txn_id
ledger_*         accounts / transactions / entries — the double-entry money truth (DATA_MODEL.md §7)
payouts          id, host_user_id, period, amount_cents, currency, stripe_transfer_id, status, error
audit_log        id, actor_user_id, action, target, meta_json, created_at
```

Key rule: **Wisper holds the wisp per-contract token** (in `leases` / in-memory routing), never the consumer. The consumer authenticates to Wisper; Wisper authenticates to the host tunnel. This preserves the billing chokepoint (§2).

## 10. Auth & roles

- **Consumers** — Cognito user pool (signup/login), JWT on every consumer call. `stripe_customer_id` created on first lease.
- **Hosts** — a host account (Cognito) *plus* a per-host **agent token** (opaque, hashed at rest, revocable) that the wisp agent presents on the `WS /agent` handshake (via `?token=` or a `bearer.<token>` subprotocol, mirroring wisp). Token identifies + authorizes the tunnel; rotating it forces the agent to re-auth.
- **Admin** — Cognito `admin` group; gates `/admin/*`. Ben is the first admin.
- **Roles are additive.** A single account can hold `consumer` and `host` at once (and `admin` too); there are not separate consumer/host accounts. A consumer "becomes a host" by completing host onboarding, which just adds the `host` group + capabilities to the same user. Roles map to Cognito **groups** (`consumer`, `host`, `admin`); the API authorizes per-group. Reuses the dev/prod Cognito pool pattern already in the fleet.
- **Machine API keys** — a consumer may also drive `/v1` with a long-lived `wck_live_…` key (hashed at rest, revocable, shown once) that authenticates as its owner but carries its **own scopes** rather than Cognito groups; a dev config-map (`Auth:ApiKeys`, empty/fail-closed in prod) mirrors the host-token bootstrap (`API.md` §2).

## 11. Metering & billing

- **The meter starts** when Wisper receives `lease.ready` (Wisper stamps the time) and accrues only over intervals bracketed by **healthy tunnel liveness** — so a disconnect **pauses** billing rather than billing blind (`TUNNEL.md` §8). It **stops** at the first of: consumer `DELETE`, wisp TTL expiry (`lease.ended`), or a tunnel loss that **exceeds the grace window** (finalized at the last healthy time). Billing is on **wall-clock lease-minutes**, rounded per the pricing rule (per-minute, 1-minute minimum).
- **Wisper is the only clock that counts.** Host-reported times are advisory; disputes resolve in Wisper's favor. This is why the relay must be mandatory.
- **Charging (DECIDED: prepaid wallet, internal ledger — not Stripe metered billing):** the consumer pre-funds a wallet (Stripe top-up); `POST /leases` places a `⌈ttl/60⌉ × price` **hold** out of the wallet (`402` if it can't cover it — no compute is provisioned that can't be paid for), each metered tick posts a `lease_charge` out of the hold, and the unused remainder releases at lease end. Stripe never touches the per-lease path; the double-entry ledger is the money truth (`PAYMENTS.md` §4, `DATA_MODEL.md` §7-8).
- **Paying hosts (Stripe Connect):** each billed minute splits into `platform_fee_cents` (Wisper's cut) + `host_payout_cents`; host payouts are **Stripe Connect transfers** to the host's connected account on a payout schedule. Connect also handles host KYC/tax.
- **Fraud/abuse guards:** per-consumer spend limits, max concurrent leases, and host-side wisp limits (`WISP_CONFIG`) cap blast radius regardless of billing.

## 12. Pricing model

- **Each host sets its own price** — `host_images.price_cents_per_min` per image (a host may price `wisp-base` differently from a warm `wisp-node` image). Hosts edit pricing on the admin/host site; changes apply to *new* leases only (a running lease keeps its `leases.price_cents_per_min` snapshot).
- **Platform fee** is admin-configured (a % or flat per-minute cut), taken from each billed minute before host payout.
- Consumers always see the effective price (host price shown; platform fee folded into the displayed rate) **before** confirming a lease.
- Wisp's `GET /images` provides the *allow-list + limits*; Wisper overlays the *price* (wisp never knows about money — principle §1).
- **GPUs are priced into the offer, not metered separately** (task #522): an offer's `host_images.max_gpus` is the whole-device ceiling a lease may book (`resources.gpus`, exclusive devices, over-ask rejected rather than clamped), and a host prices a GPU-bearing offer accordingly — there is no per-resource rate table. Device classes are opaque strings surfaced for catalog filtering (`min_gpus`/`gpu_class`); wisp allocates and enforces.

## 13. End-to-end flows

**Host onboarding:** sign up as host → register a host (get an agent token) → install `wispd` + the `wisp-agent` binary, run `wisp-agent --manager wss://<wisper-host>/agent --host-token …` → agent connects, advertises images → host sets per-image prices → Stripe Connect onboarding/KYC for payouts → host goes `online`.

**Consumer lease lifecycle:**
1. Browse `/hosts`, pick host H + image + ttl; see the per-minute price.
2. `POST /leases` — Wisper checks payment/balance, creates a `pending` lease, sends `lease.create` down H's tunnel.
3. Agent calls local wisp `POST /contracts`; wisp pulls the image if needed and boots it; agent returns `lease.ready` with `contract_id`.
4. Wisper marks the lease `active`, **starts the meter**, returns lease info to the consumer.
5. Consumer drives it: `WS /leases/:id/shell` (xterm) and `POST /exec` relay through Wisper → tunnel → wisp → container; output streams back.
6. Consumer `DELETE`s (or TTL expires, or host drops) → Wisper **stops the meter**, sends `lease.release`, finalizes `lease_usage`, and posts the closing ledger transactions (final `lease_charge` + `hold_release`, §11). Container is destroyed by wisp; nothing persists.

**Frontend:** a **brand-new Next.js app** (not a reuse of wisp-dashboard). The console *component* (xterm + the exec/stream panels) can be lifted from wisp-dashboard, but repointed from wisp's endpoints to Wisper's relayed `WS /leases/:id/shell` and `/exec` (§16).

## 14. Failure modes & edge cases

- **Tunnel drops mid-lease** → leases go `suspended` and **billing pauses**; a **grace window** (default 90s) lets the agent reconnect and **resume** the same lease (billing restarts), or, if it expires, the lease ends at last-healthy time (`TUNNEL.md` §8). Wisp's local reaper destroys the container on TTL regardless, so no orphaned paid compute. Reconciliation on reconnect is an idempotent set-diff.
- **Agent reconnects to a different instance** → Redis presence updated; in-flight consumer streams for that lease are torn down (consumer must reopen). Acceptable for v0.
- **Consumer disconnects but lease runs** → lease keeps running and **billing continues** until TTL/release (it's a held lease, like wisp). Surface running cost prominently so consumers don't leak money.
- **Host lies about lifecycle** → irrelevant to billing (Wisper's clock); worst case the host wastes its own resources.
- **Clock skew** → only Wisper's clock is authoritative; host timestamps ignored.
- **Payment fails mid-lease** → structurally impossible under the wallet+hold model: the full estimated maximum is held up front, so there is no mid-lease charge to fail (`PAYMENTS.md` §7).
- **LB idle timeout** → covered by 30s ping/pong; never reached on a healthy tunnel.

## 15. Security

- Outbound-only hosts; no inbound host ports. The agent token is the host's credential (hashed at rest, revocable, rotatable).
- Consumers never receive wisp contract tokens or a host address — the relay is mandatory.
- TLS end to end (terminated at the load balancer; Redis in-transit encryption on).
- Wisp `WISP_CONFIG` caps each lease (image allow-list, network, resources, TTL) on the host regardless of what Wisper requests — defense in depth.
- Per-consumer spend + concurrency limits; admin moderation (suspend host/consumer); audit log on all admin/policy actions.
- Stripe holds card data (PCI scope stays with Stripe); Connect holds host banking + KYC.

## 16. Dev / prod & deployment

- Two manager deployments, one per environment (**prod** and **dev**), each a container behind the API gateway / load balancer at its own host.
- Separate **Cognito pools** (`wisper-prod`, `wisper-dev`) and separate **Postgres databases** per environment (dedicated instances later).
- **Vercel** hosts two **Next.js (App Router) + MUI** apps, each with dev/prod (no Vite; client-heavy Next — a "pure web interface"):
  1. **Unified app** — one site for **consumers and hosts**. Roles are additive (§10): a signed-in user sees consumer features (browse, lease, drive, billing) and, once they onboard as a host, host features (register a wisp host, set image pricing, view earnings) — same account, both at once.
  2. **Admin app** — a separate, standalone site gated to the `admin` group (platform policy, pricing rules, moderation, payouts).
  Both call the respective Wisper API (prod / dev); frontends are hosted on Vercel, not on the API infrastructure.
- Deploy as a container image (from a container registry) behind the gateway / load balancer.

### Configuration, secrets & cloud portability

The manager is deliberately **cloud-agnostic** — it can run on any platform that can inject environment variables, and "supporting a cloud" is a deploy definition, not code:

- **Pure env-var configuration (12-factor).** Stock ASP.NET Core config: `appsettings.json` → `appsettings.{Environment}.json` → environment variables, last wins. The checked-in `appsettings.json` contains **no secrets** (the connection string is an empty placeholder). Everything sensitive arrives at process start as env vars: `ConnectionStrings__Wisper`, the `Stripe` section (API key, webhook signing secret), `Auth__Issuer`, `Auth__UserPoolId` / `Auth__Region` (the Cognito pool the runtime adds the `host` group to on first host action, `API.md` §184 — the pool must have the `consumer`/`host`/`admin` groups and the runtime needs `cognito-idp:AdminAddUserToGroup`; unset falls back to a no-op group write), `Tunnel__HostTokens` / `Auth__ApiKeys` bootstrap maps, the Redis backplane connection.
- **No cloud SDKs in the app.** There is no Secrets Manager / Key Vault / Parameter Store client and none is planned *in the app*. The platform's injector does that work: on AWS, an ECS task definition (or equivalent) maps `secrets` → `valueFrom` Secrets Manager/SSM entries to these env var names; on Azure, Container Apps / App Service maps Key Vault references to the same names. Same image, same names, different injector — swapping clouds changes zero application code. Those deploy definitions name real infrastructure and therefore live **outside this public repo**.
- **Identity is standard OIDC, one claim shy of provider-neutral.** Token validation is issuer-configured (`Auth:Issuer`, JWKS from `Auth:JwksUri`, defaulting to `{issuer}/.well-known/jwks.json`, fail-closed when unset) — any OIDC IdP (Cognito, Entra ID, Keycloak, Auth0) passes it unchanged. The one Cognito-specific coupling is **role mapping**, which reads the `cognito:groups` claim; running on a different IdP means making the groups-claim name (and value mapping) configurable alongside the issuer. The **API-key auth path is fully IdP-free** already. Host agents and Stripe are provider-independent by construction (outbound WebSockets; plain HTTPS).
- **Portable dependencies.** Postgres and Redis are commodity managed services on every cloud; the Redis backplane is only required multi-instance (`Tunnel:Backplane` is the per-environment deploy switch — §7 describes the mechanism, this flag turns it on).
- **Deploy-time hazards to hold the line on:** `Tunnel:EnableDevEndpoints` maps **unauthenticated** `/dev/leases` surfaces and is on by default only in Development — production config must never set it. Health: `GET /healthz` and `GET /api/health` (same handler, gateway convention) return 503 when unhealthy — point the platform's health checks there.

### In-memory persistence mode (DB-less dev boot)

- **Trigger:** when `ConnectionStrings:Wisper` is unset/empty, the manager boots in **in-memory persistence mode** — it registers the in-memory doubles for *every* repository (users, hosts, host images, api keys, leases, lease usage, ledger, stripe events, payouts, idempotency, platform policy, audit) instead of the Postgres repositories. With a connection string present, the Postgres path is used and production behaviour is **unchanged**.
- **Loud & unambiguous:** exactly one startup line is logged — `persistence: in-memory (no connection string) — state resets on restart` (at **warning** level, so an accidental production boot with no connection string is unmissable) — and the health report's `database` entry reads `in-memory` rather than pretending a database is healthy. Migrations are a no-op.
- **What works:** the `/v1` request path runs in-process with no Postgres — with two exceptions: the **metering flush loop and the payout runner do not start** in this mode (both gate on a configured database), so leases run and hold but usage/ledger charges never accrue. A single config API key (`Auth:ApiKeys`) with `consumer`+`host` scopes drives the full self-hosted flow end-to-end: `POST /v1/hosts` (issues a real `wht_live_` agent token the DB-backed validator resolves against the in-memory store) → `PUT /v1/hosts/:id/images` (0-cent pricing allowed for self-hosted operators) → `GET /v1/catalog` (once the agent tunnel is live) → `POST /v1/leases` (with `env`, placing a 0-cent hold). The config key grant carries an optional `Email` so it can bootstrap the operator's account (`users.email` is NOT NULL) with no Cognito.
- **What resets:** *all state* lives in process memory and is gone on every restart — hosts, leases, wallet/ledger balances, everything.
- **Never for production.** It exists for local dev and self-hosted single-operator experimentation only; there is no durability, no cross-instance sharing, and no backups.

## 17. Open questions / decisions to confirm

- ~~**Marketplace now vs first-party-first**~~ **DECIDED: marketplace now.** Build the *full* two-sided marketplace up front — third-party host onboarding, **Stripe Connect payouts, and KYC** are all in scope from the start, not deferred. Rollout is a **soft launch**: everything is built and hardened in the **dev** environment first and only *advertised publicly* once the kinks are worked out. Scope ≠ launch timing — the code is complete; the marketing waits.
- ~~**Prepaid wallet vs postpaid metered billing?**~~ **DECIDED & SHIPPED: prepaid wallet** — `⌈ttl/60⌉·price` hold at `POST /v1/leases`, per-tick ledger charge, remainder released at end (§11, `PAYMENTS.md` §4). Auto-recharge remains the deferred layer-on.
- **Billing granularity/minimum:** per-minute with a 1-minute minimum, or per-second? Rounding rule affects host payouts.
- ~~**Manager language**~~ **DECIDED:** manager is **C# / ASP.NET Core (Kestrel)** — the fleet already runs .NET (`3gixhub`), and its multi-core async concurrency + throughput suit a connection-heavy byte-relay better than Node's single-threaded loop. Transport is **raw WebSockets end-to-end (no SignalR)** with a **`StackExchange.Redis`** backplane (§6, §7). The **agent** is Go (lives with wisp).
- ~~**Agent packaging**~~ **DECIDED:** a **separate `wisp-agent` Go binary** in its own repo (keeps wisp marketplace-blind; the agent is just another wisp client).
- **Reconnect stream continuity:** tear down consumer streams on host reconnect (v0) vs seamless resume (later).
- **Multi-host scheduling:** consumer picks a specific host (v0) vs Wisper auto-places on the cheapest/closest available host (later).

## 18. Phased build plan

1. **Agent + tunnel (no money):** wisp agent dials Wisper; Wisper relays create/exec/shell over the tunnel with ping/pong; one first-party host; drive a lease end-to-end from a test client. *Proves the hard part.*
2. **Accounts + catalog:** Cognito consumers/hosts/admin; host registration + priced image allow-list; public catalog browse.
3. **Metering:** Wisper-authoritative lease-minute metering + running-cost display; no charging yet.
4. **Billing (full marketplace):** Stripe **prepaid wallet** + metered debit; payment gate on lease creation; **Stripe Connect** connected accounts, host payouts, and platform-fee split; invoices/statements. Third-party economics are in from the start (decision §17).
5. **Host onboarding + KYC:** self-serve host registration, agent-token issuance, **Stripe Connect onboarding/KYC**, container-registry credential guidance for hosts (so allow-listed images pull reliably). A consumer becomes a host on the same account (additive roles, §10).
6. **Unified consumer/host UI:** the Next.js + MUI app on Vercel — browse/lease/drive (console lifted from wisp-dashboard) + host tools (register host, price images, earnings).
7. **Admin UI:** separate Next.js + MUI admin app — platform policy, pricing rules, moderation, payouts, revenue.
8. **Multi-instance hardening + soft launch:** Redis-backplane hardening and manager autoscale; full dev-environment hardening of the marketplace; **then** flip on public advertising (the marketplace code is already complete — this step is timing, not scope).

---

*Wisper is deliberately thin: wisp runs the compute and stays money-blind; Wisper owns identity, metering, and payments; the tunnel makes any home machine a sellable host. If a decision needs to know what the work inside a lease means, it belongs in neither — that's the consumer's business.*
