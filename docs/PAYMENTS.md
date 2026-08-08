# Wisper — Payments & Money Operations

**Status:** Draft / v0 · **Companion to:** [`DESIGN.md`](./DESIGN.md), [`DATA_MODEL.md`](./DATA_MODEL.md) · **Processor:** Stripe (+ Stripe Connect)

This spec is the runtime choreography of money — how funds enter, are held and metered, reach hosts, and are reconciled. It pairs with `DATA_MODEL.md` §7–§9 (the ledger is the source of truth; this doc is *how Stripe events drive ledger transactions*). Every rule here is written for correctness under retries, out-of-order webhooks, and partial failure.

---

## 1. Stripe setup & posture

- **Two Stripe modes = two environments.** `wisper_dev` uses Stripe **test** keys; `wisper_prod` uses **live** keys. Keys live in a secrets manager, never in code. Separate webhook endpoints + signing secrets per env.
- **Connect flavor = Express.** Host payouts use **Stripe Connect *Express*** accounts. Rationale: Stripe hosts the KYC/onboarding UI and owns identity/AML compliance and the connected-account dashboard, while the *platform* keeps control of when and how much to pay out. **Standard** would hand hosts a full Stripe account (less platform control, host-managed); **Custom** would force us to build all onboarding UI and own compliance liability. Express is the marketplace-standard middle and the right call.
- **Fund flow = separate charges & transfers.** Consumers fund a **wallet** (their money sits in the platform Stripe balance); leases are metered against that wallet via the *internal ledger*; hosts are paid by **Transfers** from the platform balance to their connected account. We deliberately do **not** use destination/direct charges — there is no per-lease card charge to attach a destination to; the wallet decouples "consumer pays" from "host earns."
- **PCI posture = SAQ-A.** Card data never touches Wisper. The browser tokenizes via **Stripe Elements / Payment Element**; Wisper only ever sees Stripe ids (`pi_…`, `pm_…`, `tr_…`). No PAN, ever.
- **Every money-moving Stripe write (PaymentIntent, Refund, Transfer) carries an idempotency key** (non-money setup calls — customer/SetupIntent/Express-account/Account-Link creation — currently don't), and every Stripe *read of truth* is mirrored by a webhook. Nothing about our money state depends on an HTTP response we might miss.

## 2. Where Stripe touches vs. where the ledger rules

| Step | Stripe? | Ledger txn (`DATA_MODEL.md` §8) |
|---|---|---|
| Consumer buys credits | **yes** — PaymentIntent | `topup` (on `payment_intent.succeeded`) |
| Lease starts | no | `lease_hold` (earmark from wallet) |
| Metered minute | no | `lease_charge` (hold → host_earnings + platform_revenue) |
| Lease ends | no | `hold_release` (unused hold → wallet) |
| Host gets paid | **yes** — Transfer | `payout` (host_earnings → platform_cash) |
| Refund / dispute | **yes** | `refund` / `chargeback` |

The lease's *inner loop* (hold → charge → release) touches Stripe **zero times** — it's pure internal ledger against pre-funded wallet money. That's what makes per-minute billing cheap and safe: no card round-trip per minute, and the money is already collected.

## 3. Consumer top-up (funding the wallet)

```
browser                     Wisper                         Stripe
  │ POST /billing/topup  ──▶ │ Idempotency-Key header       │
  │   {amount_cents}         │ create/lookup customer       │
  │                          │ PaymentIntent(amount, cust,  │
  │                          │   idempotency_key) ─────────▶ │
  │ ◀── client_secret ────── │ ◀── pi.client_secret ─────── │
  │ confirm w/ Payment Element (SCA/3DS handled by Stripe)  │
  │                                                          │
  │            (async, source of truth)                     │
  │        Stripe ── webhook payment_intent.succeeded ─────▶ │ verify sig, dedupe,
  │                                                          │  post `topup` ledger txn
```

- **Create:** `POST /billing/topup {amount_cents}` (≥ `platform_policy.min_topup_cents`). Wisper ensures a `stripe_customer_id` (creates on first use), then creates a **PaymentIntent** with an idempotency key = the API idempotency key (`DATA_MODEL.md` §10), returns `client_secret`. The browser confirms with the Payment Element; **SCA/3-D Secure is handled entirely by Stripe** on the client.
- **Credit is webhook-driven, never response-driven.** The wallet is credited **only** on the `payment_intent.succeeded` webhook (§8), posting a `topup` ledger transaction keyed by the *Stripe event id* — so a duplicate webhook or a client that never returned cannot double-credit or drop a credit. Platform absorbs the Stripe processing fee (`stripe_fees`, `DATA_MODEL.md` §8) — with the caveat that the fee leg only posts when Stripe expanded `latest_charge.balance_transaction` on the event; otherwise the fee is recorded as `0` and `platform_cash` is debited the gross (refunds currently always record fee `0`).
- **Failure:** `payment_intent.payment_failed` → surface to the user, no ledger effect. `requires_action` is resolved client-side by Stripe.
- **Saved methods (half-built) & auto-recharge (planned):** the **SetupIntent** flow to save a payment method exists (`POST /v1/billing/payment-methods` returns the `client_secret`), but the `setup_intent.succeeded` webhook is currently a logged no-op — marking the saved method usable is deferred saved-method work. **Auto-recharge is designed but not implemented** (no threshold setting, no off-session PaymentIntent path): when built, a spendable balance dropping below a user-set threshold triggers an off-session PaymentIntent against the saved method; a failure notifies the user, and **existing leases are unaffected** because their funds are already held (§4) — only *new* leases are gated.
- **Refund of unused credits:** `POST /billing/refund` (or admin) issues a Stripe **Refund** for wallet funds not yet spent; the `refund` ledger txn requires sufficient wallet balance. Refunding *spent* credits is a clawback question (§7).

## 4. Lease billing (the metered inner loop)

Pure ledger; see `DATA_MODEL.md` §8 for the debit/credit of each step.

1. **Authorize at `POST /leases`.** Compute `estimated_max = ⌈ttl_seconds/60⌉ × price_cents_per_min`. The pre-frame gate — enforced before any `lease.create` frame is sent (`TUNNEL.md` §11) — is the concurrency cap (`max_concurrent_leases_per_user`), the fraud-guard daily-spend cap (§7), and a **wallet balance check** against the hold (`402 insufficient_funds` on failure): we never provision compute that can't be paid for. The `lease_hold` ledger transaction (wallet → `lease_holds`) then posts once the lease exists, with the **non-negative-wallet trigger** as the race backstop (a wallet drained between check and post fails the posting, and the lease is torn down). *Known ordering defect:* today the hold posts before the lease **row** is persisted, which violates the ledger's `lease_id` FK on the SQL store and leaks the host-side contract on failure — the fix (persist the lease row before/atomically with the hold, tear down the downstream contract on any post-frame failure) is tracked and lands with the paid-lease path hardening.
2. **Meter.** From `lease.ready`, Wisper accrues `billable_seconds` over **healthy-liveness intervals only** (`TUNNEL.md` §8 — suspended gaps don't count). On each **flush tick (60s)** and at lease end, post a `lease_charge` (hold → host_earnings + platform_revenue), splitting by `platform_policy.fee_bps`, and write the `lease_usage` row (idempotent on `(lease_id, period_start)`).
3. **Release.** At lease end, post `hold_release` for the unused remainder (hold → wallet). Because the hold covered the *entire* max lease, the hold can never be exhausted mid-lease — no "insufficient funds mid-run" failure exists in this model.
4. **Grace/suspend.** A tunnel drop pauses metering (no `lease_charge` accrues during the gap); on grace expiry, the lease ends and the remainder releases. Billing integrity comes for free from "meter only over healthy intervals."

**Zero-priced images (free tier / self-hosted).** `price_cents_per_min = 0` is a **valid price** (`≥ 0`; only a negative price is rejected — `API.md` §6), so an operator who is both host and consumer of their own box — or anyone offering a free image — needs no wallet balance to lease it. The whole loop degenerates cleanly: the estimated hold is `⌈ttl/60⌉ × 0 = 0`, so the authorize gate **passes with an empty wallet** and **no `lease_hold` is posted**; each meter tick still accrues `billable_seconds` (so timeline/liveness are unaffected) but the `lease_charge` amount is `0`, and a **zero-amount transaction is skipped, never posted** — a `0`-debit/`0`-credit txn would be vacuous and the balanced-entry trigger (`DATA_MODEL.md` §7,§8) is reserved for real money movement, so no `lease_charge`/`lease_usage` row is written; at end there is no hold to release, so `hold_release` is a no-op. The result: a free lease moves the ledger **zero times** and can never drive a balance negative.

## 5. Host onboarding — Connect Express + KYC

```
host UI                    Wisper                        Stripe Connect
  │ POST /hosts/connect ──▶ │ create Express account ───▶ │ acct_…
  │                         │ create Account Link ──────▶ │ onboarding URL
  │ ◀── onboarding URL ──── │                             │
  │ ── redirect to Stripe-hosted KYC/bank setup ────────▶ │ (identity, bank, TOS)
  │                              webhook account.updated ─▶ │ set connect_status
  │                                                         │  from charges/payouts_enabled
```

- **Create account + link:** Wisper creates a Connect **Express** account (`connect_account_id` on the user) and an **Account Link** (hosted onboarding). The host completes identity + bank + ToS on **Stripe's** UI — Wisper stores no KYC data.
- **Gate `online` on earning eligibility:** presence follows the tunnel (`TUNNEL.md` §3, §8) — when the agent handshake completes Wisper evaluates a gate and, if it passes, flips the host `online`; a durable tunnel loss flips it back `offline`. The gate passes when the owner is Connect-enabled (`account.updated` reports `charges_enabled && payouts_enabled` → `connect_status = 'enabled'`) **or** the host **earns nothing** — every enabled `host_image` is priced at `0` cents/min (the self-hosted / zero-price posture, §4). A host that charges nothing has no earning for Connect to gate, so it may advertise un-onboarded; a Connect-incomplete host that lists a *priced* image stays `offline` (agent can still connect and test) until Connect is enabled. The one rule lives next to the payout gate in `ConnectGate` (`CanHostGoOnline`).
- **Charging requires Connect (enforced at pricing):** because the online gate has a zero-earn arm, the *only* way a Connect-incomplete host could slip into the earning arm is by pricing a non-zero image, so the images surface (`PUT`/`PATCH /v1/hosts/:id/images`, `API.md` §6) **rejects enabling a non-zero-priced image for a non-Connect-enabled owner** (`validation_error`, "complete Connect to charge, or price at 0"). That keeps the gate consistent at the single mutation point and means a live host is never knocked `offline` mid-tunnel by a pricing change.
- **Restricted/again:** Stripe may set an account `restricted` (needs more info) as volume grows. `account.updated` → `connect_status = 'restricted'`; Wisper surfaces a re-onboarding Account Link and **holds payouts** (earnings keep accruing in the ledger, none are lost) until re-enabled.
- **Offboarding** (*planned, not implemented*): if a host disables, pending `host_earnings` are paid out on the next run; the account can be deauthorized after balance reaches zero. No offboarding/deauthorize path exists in code today.

## 6. Host payouts (paying earnings out)

Earnings accrue continuously in each host's `host_earnings` ledger account (§4). A scheduled **payout run** turns accrued balance into Stripe **Transfers**.

- **Schedule + threshold:** a job runs on a fixed cadence (default **daily**) and, per host with `host_earnings ≥ payout_min` (and `connect_status='enabled'`), creates a **Stripe Transfer** platform-balance → connected account for the accrued amount. Both the cadence and `payout_min` (default 100¢) are **service config** (`Payouts` options in appsettings/env), not `platform_policy` columns. It writes a `payouts` row and posts the `payout` ledger txn (`host_earnings → platform_cash`). On-demand payout (host-triggered) uses the same path with the same guard.
- **Idempotency:** the Transfer's Stripe idempotency key = the `payouts.id`, so a retried run can't double-pay. `payouts.stripe_transfer_id` is unique.
- **Transfer → bank:** the platform Transfer moves money to the host's *connected balance*; Stripe then pays the connected balance to the host's bank on the Express account's payout schedule. Wisper tracks the Transfer; the connected→bank payout is Stripe's job (visible via `transfer.*` / connected `payout.*` webhooks for status).
- **Fees:** the platform fee (`fee_bps`) is already taken at `lease_charge` time. Stripe's Connect/transfer fees are a platform cost (tracked as they post); v0 does not pass them to hosts.
- **Failure:** a failed Transfer → `payouts.status='failed'`, **no** `payout` ledger txn commits (earnings remain in `host_earnings`, never lost); the next run retries. Repeated failures are logged; operator alerting is not wired up yet.

## 7. Refunds, disputes, chargebacks, clawbacks

The genuinely adversarial cases — specified, not hand-waved.

- **Refund of unspent credits** (§3): straightforward Stripe Refund + `refund` ledger txn; requires wallet funds.
- **Top-up dispute / chargeback** (`charge.dispute.created`): the hard case — a consumer disputes a top-up *after spending credits* on leases (money already transferred toward hosts). Handling: post a `chargeback` ledger txn; the wallet may go **negative** (a genuine debt — this is the one place a wallet balance is allowed below zero, representing money owed to the platform), **suspend the user** (`status='suspended'`, blocks new leases), and (*planned — no Stripe evidence API call exists yet*) **submit evidence** to Stripe automatically where available. Loss policy: the platform **eats** confirmed fraud loss for already-paid host earnings (a host who did real work is not clawed back for a consumer's fraud) and pursues the debt/limits the user. Mitigations that make this rare are enforced up front (below).
- **Fraud mitigations (in from day one, not later):** new-account **velocity limits** (max top-up/spend/day), a **first-top-up hold** (small initial cap until a payment clears the dispute window materially), per-user **spend + concurrency caps** (`platform_policy`), and admin review flags on anomalies. These are cheap policy checks at `topup`/`POST /leases`, not a fraud ML system (that would be nonsense at this stage — §13).
- **Failed payment "mid-lease" doesn't exist here.** Because funds are pre-held from an already-funded wallet (§4), there is no card charge to fail during a lease. This is a direct benefit of the wallet+hold model and a reason we chose it.
- **Clawback (host overpaid):** if a dispute lands after a host was paid and policy requires recovery, Stripe Connect supports debiting a connected account; v0 policy is platform-absorb for paid earnings (above), with clawback available as an admin `adjustment` if ever needed.

## 8. Webhook processing (exactly-once, order-independent)

Webhooks are the **source of truth** for every asynchronous money fact. The handler is built to be safe against duplicates, out-of-order delivery, and replay.

1. **Endpoint:** `POST /stripe/webhook` (app root; the gateway may add its own prefix) — unauthenticated but **signature-verified** with the endpoint's signing secret (`Stripe-Signature`). A bad signature → `400`, no processing.
2. **Persist-then-process:** immediately upsert into `stripe_events` (PK = Stripe event id). If the id already exists → **ack `200` and stop** (dedupe). Otherwise mark `received` and hand to an idempotent handler; mark `processed`/`ignored`/`failed`.
3. **Idempotent, order-independent handlers:** each handler is a pure function of the event + current ledger state, keyed so re-delivery is a no-op (ledger txns carry the event id as `idempotency_key`). Handlers **never assume ordering** — e.g. a `payment_intent.succeeded` that arrives before the API call's own bookkeeping still fully credits the wallet on its own.
4. **Retry + failure:** a handler that throws leaves the event `failed` with the error; **Stripe's own retry is the only re-delivery mechanism today** — the in-house sweeper that re-attempts `failed`/stale `received` rows, and operator alerting on repeated failure, are designed but not built. The `stripe_events` table still gives dead-letter *visibility* (nothing is lost silently), but a row Stripe stops retrying stays `failed` until someone looks.
5. **Events we handle:**

| Event | Action |
|---|---|
| `payment_intent.succeeded` | post `topup`; credit wallet |
| `payment_intent.payment_failed` | notify; no ledger effect |
| `charge.refunded` | post `refund` |
| `charge.dispute.created` | post `chargeback`; suspend the user (§7) |
| `charge.dispute.closed` | informational today — logged, no state change (resolution/adjustment handling is future work) |
| `account.updated` | recompute `connect_status`; gate host `online` (§5) |
| `transfer.created` / `.failed` / `.reversed` | update `payouts.status`. **No automatic rollback:** a reversal of an already-committed `payout` txn marks the row `failed` and logs — the compensating ledger reversal is deferred, deliberate-adjustment territory |
| `payout.paid` / `.failed` (connected) | informational — connected→bank status |
| `setup_intent.succeeded` | logged no-op today — "mark saved method usable" ships with the saved-method work (§3) |

## 9. Reconciliation with Stripe

Webhooks can, rarely, be missed; the ledger must never silently diverge from Stripe.

- **Ledger self-check** (`DATA_MODEL.md` §7e): balances re-derived from entries; drift pages.
- **Stripe cross-check** (*designed, not implemented*): compare `platform_cash` movement against Stripe **Balance Transactions** over a window (top-ups in, transfers/refunds out, fees) and reconcile per-object status by polling Stripe for any `stripe_events` stuck in `received`/`failed`. Discrepancies produce a report + alert, not an auto-mutation (money corrections are always deliberate `adjustment` txns, audited §12 of the data model). Until this ships, the ledger self-check above is the only automated drift detector.
- **Payout audit:** every `payouts` row maps 1:1 to a Stripe Transfer id and a `payout` ledger txn — a three-way tie-out (ledger ↔ payouts table ↔ Stripe) is checkable at any time.

## 10. Security & compliance

- **PCI SAQ-A** — tokenization client-side, no card data server-side (§1).
- **KYC/AML** delegated to Stripe Connect Express; Wisper stores account id + status only.
- **Webhook signature verification** mandatory; **idempotency keys** on all money-moving Stripe writes (§1).
- **Least-privilege keys** in a secrets manager, per env; the webhook signing secret is separate from the API key.
- **Audit** (`DATA_MODEL.md` §12): every admin money action (manual refund, adjustment, payout trigger, suspend) is logged with actor + before/after.
- **Money mutations are ledger-transactional** and idempotent at the DB layer, so even a compromised or buggy handler cannot post an unbalanced or duplicate entry.

## 11. Key sequences

**Payout run (per eligible host):**
```
payout job         Wisper ledger              Stripe
  │ for each host with host_earnings ≥ min & enabled:
  │ create payouts row (id=P) ───────────────▶
  │ Transfer(amount, dest=acct, idem=P) ─────────────────▶ tr_…
  │ ◀── tr id / status ──────────────────────────────────
  │ post `payout` txn (host_earnings→platform_cash) ─────▶ ledger
  │ (webhook transfer.created/paid updates payouts.status)
```

**Dispute after spend:**
```
Stripe ── charge.dispute.created ─▶ Wisper
  persist event · post `chargeback` (wallet may go negative = debt)
  suspend user · auto-submit evidence · alert admin · (paid host earnings absorbed)
```

## 12. Edge / failure matrix

| Scenario | Handling |
|---|---|
| Duplicate webhook | `stripe_events` PK dedupe → ack, no-op |
| Webhook missed | reconciliation poll (§9) repairs |
| Client confirms but never returns | webhook still credits (§3) |
| Wallet insufficient at lease start | balance gate rejects pre-provision; the ledger's non-negative trigger backstops the check-to-post race (§4) |
| Tunnel drops mid-lease | metering pauses; no charge for the gap (§4, `TUNNEL.md` §8) |
| Transfer fails | earnings retained in `host_earnings`, retried (§6) |
| Connect account restricted | payouts held, earnings accrue, re-onboarding link (§5) |
| Dispute after spend | `chargeback`, negative wallet debt, suspend (§7) |
| Auto-recharge fails (*when built*, §3) | notify; existing leases unaffected |
| Refund exceeds wallet | blocked; clawback is an audited admin `adjustment` (§7) |

## 13. Deliberate scope boundaries

- **Instant payouts** are out — standard scheduled Transfers (daily + threshold) are correct and cheaper; instant payout is a per-host toggle addable later without schema change.
- **ML fraud scoring** is out — day-one fraud control is deterministic policy (velocity, caps, first-top-up hold, KYC). A scoring model on ~zero transaction history would be theater, not protection; the hooks (`audit_log`, spend caps) are where it attaches when there's real data.
- **Multi-currency FX** is out (single `usd`), per `DATA_MODEL.md` §16 — additive, not a rebuild.
- **Tax computation** is delegated to Stripe (Tax / Connect reporting); Wisper stores no tax tables.
```
