# Wisper — Design Doc

**Status:** Draft / v0 · **Stack:** C# / ASP.NET Core (Kestrel) manager · Go wisp agent · Next.js + MUI frontends · **raw WebSockets end-to-end** (no SignalR) · **Author:** Ben (with Claude)
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
2. On connect, **advertise capability**: the wisp `GET /images` document (allow-list, default, limits) so Wisper knows what this host can offer. (Prices are set in Wisper, not by wisp — see §12.)
3. Receive tunnel frames and translate them to local wisp API calls (`POST /contracts`, `exec`, shell WS, `DELETE`), streaming results/bytes back up the tunnel with the same stream id.
4. Send periodic **heartbeat** (current leases, load) and keep the socket alive with **ping/pong** (§6).

Because the agent only speaks wisp's public API, **wisp itself needs no marketplace awareness** — the only genuinely new wisp-side work is the agent bridge.

## 6. The tunnel protocol

One persistent WebSocket per host, carrying **many multiplexed logical streams** (control, exec, shell) so Wisper never needs a second connection to a host.

**Transport decision — raw WebSockets end-to-end, no SignalR.** The Go agent is a custom client that speaks raw WebSockets (`System.Net.WebSockets` on the Kestrel side), so the host tunnel is raw regardless. The *consumer* side is the same shape — a dumb byte-relay of PTY/exec bytes into xterm.js — where SignalR's hub framing, RPC, and transport fallback add friction and little value. So both agent and consumer connections use raw WebSockets with the framing below, giving **one** transport, **one** framing protocol, and **one** backplane (§7) across the whole system. SignalR is reconsidered only if the frontend later grows rich non-console real-time features (marketplace presence, notifications) — and even then only for those, never as the tunnel transport.

### Keepalive: ping / pong (required)

- Both peers send **WebSocket ping frames every 30s**; the other replies **pong**. This keeps NAT bindings and the **load-balancer idle timeout (e.g. 900s)** alive with wide margin, and — more importantly — **detects dead peers fast**: if a peer misses **2 consecutive pongs (~60–90s)**, the connection is considered dead, closed, and the host marked `offline`.
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
| W→H | `lease.create` | boot a contract: `{image, network, resources, ttl_seconds, userdata}` |
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

## 9. Data model (Postgres)

```
users            id, cognito_sub, email, kind(consumer|host|admin via groups),
                 stripe_customer_id, stripe_connect_account_id, status, created_at
hosts            id, owner_user_id, name, label/region, status(online|offline|suspended),
                 agent_token_hash, wisp_version, last_seen_at, created_at
host_images      id, host_id, image_ref, price_cents_per_min, networks[], max_ttl_seconds,
                 max_cpus, max_memory_mb, enabled            -- the priced allow-list
leases           id, consumer_user_id, host_id, image_ref, network, resources_json,
                 ttl_seconds, price_cents_per_min, status(pending|active|ended|failed),
                 wisp_contract_id, created_at, started_at, ended_at, end_reason
usage_records    id, lease_id, minute_bucket, amount_cents, platform_fee_cents,
                 host_payout_cents, stripe_usage_record_id, billed_bool
payouts          id, host_user_id, period, amount_cents, stripe_transfer_id, status
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
- **Charging (Stripe):** each lease accrues `usage_records`; these post to **Stripe metered billing** (usage records against a per-minute price), or a prepaid **wallet** is debited (see open questions). Consumer must have a valid payment method / sufficient balance before `POST /leases` succeeds (authorization hold or wallet check up front).
- **Paying hosts (Stripe Connect):** each billed minute splits into `platform_fee_cents` (Wisper's cut) + `host_payout_cents`; host payouts are **Stripe Connect transfers** to the host's connected account on a payout schedule. Connect also handles host KYC/tax.
- **Fraud/abuse guards:** per-consumer spend limits, max concurrent leases, and host-side wisp limits (`WISP_CONFIG`) cap blast radius regardless of billing.

## 12. Pricing model

- **Each host sets its own price** — `host_images.price_cents_per_min` per image (a host may price `wisp-base` differently from a warm `wisp-node` image). Hosts edit pricing on the admin/host site; changes apply to *new* leases only (a running lease keeps its `leases.price_cents_per_min` snapshot).
- **Platform fee** is admin-configured (a % or flat per-minute cut), taken from each billed minute before host payout.
- Consumers always see the effective price (host price shown; platform fee folded into the displayed rate) **before** confirming a lease.
- Wisp's `GET /images` provides the *allow-list + limits*; Wisper overlays the *price* (wisp never knows about money — principle §1).

## 13. End-to-end flows

**Host onboarding:** sign up as host → register a host (get an agent token) → install `wispd` + the `wisp-agent` binary, run `wisp-agent --manager wss://<wisper-host>/agent --host-token …` → agent connects, advertises images → host sets per-image prices → Stripe Connect onboarding/KYC for payouts → host goes `online`.

**Consumer lease lifecycle:**
1. Browse `/hosts`, pick host H + image + ttl; see the per-minute price.
2. `POST /leases` — Wisper checks payment/balance, creates a `pending` lease, sends `lease.create` down H's tunnel.
3. Agent calls local wisp `POST /contracts`; wisp pulls the image if needed and boots it; agent returns `lease.ready` with `contract_id`.
4. Wisper marks the lease `active`, **starts the meter**, returns lease info to the consumer.
5. Consumer drives it: `WS /leases/:id/shell` (xterm) and `POST /exec` relay through Wisper → tunnel → wisp → container; output streams back.
6. Consumer `DELETE`s (or TTL expires, or host drops) → Wisper **stops the meter**, sends `lease.release`, finalizes `usage_records`, posts to Stripe. Container is destroyed by wisp; nothing persists.

**Frontend:** a **brand-new Next.js app** (not a reuse of wisp-dashboard). The console *component* (xterm + the exec/stream panels) can be lifted from wisp-dashboard, but repointed from wisp's endpoints to Wisper's relayed `WS /leases/:id/shell` and `/exec` (§16).

## 14. Failure modes & edge cases

- **Tunnel drops mid-lease** → leases go `suspended` and **billing pauses**; a **grace window** (default 90s) lets the agent reconnect and **resume** the same lease (billing restarts), or, if it expires, the lease ends at last-healthy time (`TUNNEL.md` §8). Wisp's local reaper destroys the container on TTL regardless, so no orphaned paid compute. Reconciliation on reconnect is an idempotent set-diff.
- **Agent reconnects to a different instance** → Redis presence updated; in-flight consumer streams for that lease are torn down (consumer must reopen). Acceptable for v0.
- **Consumer disconnects but lease runs** → lease keeps running and **billing continues** until TTL/release (it's a held lease, like wisp). Surface running cost prominently so consumers don't leak money.
- **Host lies about lifecycle** → irrelevant to billing (Wisper's clock); worst case the host wastes its own resources.
- **Clock skew** → only Wisper's clock is authoritative; host timestamps ignored.
- **Payment fails mid-lease** → grace + auto-release policy (admin-configured); lease force-ended.
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

## 17. Open questions / decisions to confirm

- ~~**Marketplace now vs first-party-first**~~ **DECIDED: marketplace now.** Build the *full* two-sided marketplace up front — third-party host onboarding, **Stripe Connect payouts, and KYC** are all in scope from the start, not deferred. Rollout is a **soft launch**: everything is built and hardened in the **dev** environment first and only *advertised publicly* once the kinks are worked out. Scope ≠ launch timing — the code is complete; the marketing waits.
- **Prepaid wallet vs postpaid metered billing?** *Working default: prepaid **wallet*** (buy credits, debit per lease-minute) — caps abuse risk and is simple to reason about; a postpaid/auto-recharge option can layer on later. (Architect may override.)
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
