# Wisper — Data Model

**Status:** Draft / v0 · **Companion to:** [`DESIGN.md`](./DESIGN.md), [`TUNNEL.md`](./TUNNEL.md) · **Store:** PostgreSQL 17 (managed; a separate logical DB per env)

This is the authoritative schema for Wisper. It is built to be correct under concurrency and crash, because it holds money: a **double-entry ledger** is the single source of truth for every cent, and the balance you see is always *derived from* immutable journal entries, never a number someone incremented.

---

## 1. Principles

1. **Money is integer `bigint` cents.** Never floats, never `numeric` arithmetic in app code. One currency for now (`usd`); a `currency` column exists on money tables so multi-currency is an additive change, not a migration of every row (see §16).
2. **The ledger is the source of truth.** Wallet balances, host earnings, and platform revenue are all *account balances derived from* `ledger_entries`. There is no authoritative "balance" that lives outside the journal.
3. **Double-entry, always balanced.** Every money movement is one `ledger_transaction` whose `ledger_entries` satisfy `Σ debit = Σ credit`. Enforced by a **deferred constraint trigger** — an unbalanced transaction cannot commit.
4. **Entries are immutable.** `ledger_entries` and `ledger_transactions` are append-only; `UPDATE`/`DELETE` are blocked by trigger. Corrections are *reversing* transactions, so history is complete and auditable.
5. **Idempotency wherever money moves.** Every money-mutating path (top-up, lease start, Stripe webhook, payout) carries an idempotency key so a retry or duplicate webhook can never double-charge or double-pay.
6. **UTC everywhere** (`timestamptz`). Metering time is Wisper's clock (`DESIGN.md` §2), stamped server-side.
7. **Metering is crash-safe.** Usage is flushed to the DB on a fixed tick, so a manager crash risks at most one tick of billing, recoverable on restart (§14).

**Access & migrations.** Wisper (C#/ASP.NET Core) uses **Dapper + explicit SQL** with **DbUp**-managed, ordered, raw-SQL migrations — not a heavyweight ORM. Rationale: a financial ledger with constraint triggers and hand-tuned transactions wants explicit, reviewable SQL and no ORM surprises around isolation or lazy loading. (EF Core would be acceptable for the non-financial CRUD tables, but one access pattern across the service is simpler to reason about.)

**Persistence backend (Postgres vs in-memory dev mode).** The backend is chosen at boot from `ConnectionStrings:Wisper`:

- **Connection string present → Postgres** (the production path): the Dapper repositories over a pooled `NpgsqlDataSource`, DbUp migrations at startup, and the live DB health probe. This is the only mode production ever runs.
- **Connection string unset → in-memory** (an explicit **dev mode**): in-memory doubles are registered for **every** repository in this document (users, hosts, host_images, api_keys, leases, lease_usage, ledger store, stripe_events, payouts, idempotency, platform_policy, audit), so the full `/v1` path runs with no Postgres. Migrations are a no-op, and the background loops that need a database (metering tick, suspension sweep, payout run) do not start. It is **loud**: one startup warning line (`persistence: in-memory (no connection string) ... state resets on restart`), and the health report's `database` entry reads `in-memory` rather than pretending a database is healthy. **All state lives in process memory and resets on every restart, never for production.** See `DESIGN.md` §16.

**Migrations as shipped** (`src/Wisper.Api/Migrations`, embedded, applied by DbUp at startup unless `Persistence:RunMigrationsAtStartup` is false; journaled in `schemaversions`): `0001_Init` (empty probe), `0002_Enums`, `0003_Users`, `0004_HostsAndImages`, `0005_Leases` (leases + lease_usage + their enums), `0006_Ledger` (accounts/transactions/entries, triggers, and the circular FKs deferred from 0005), `0007_StripeIdempotencyPolicyAudit`, `0008_FraudPolicy`, `0009_ApiKeys`, `0010_HostIsolation`, `0011_LeaseIsolation`, `0012_HostGpu`, `0013_ImageAndLeaseGpu`, `0014_ImageResourceProfile`, `0015_LeaseProvisionedProfile`, `0016_LeaseSuspendedAt`. The runner also creates the target logical database if it does not exist.

## 2. Enum types

```sql
CREATE TYPE user_status      AS ENUM ('active','suspended','deleted');
CREATE TYPE host_status       AS ENUM ('offline','online','suspended');
CREATE TYPE connect_status    AS ENUM ('none','pending','restricted','enabled','disabled');
CREATE TYPE network_mode      AS ENUM ('none','open','egress');
CREATE TYPE lease_status       AS ENUM ('pending','provisioning','active','suspended','ended','failed');
CREATE TYPE lease_end_reason   AS ENUM ('released','expired','host_disconnect','container_lost','admin','payment_failed');
CREATE TYPE ledger_account_kind AS ENUM ('user_wallet','host_earnings','lease_holds','platform_revenue','platform_cash','stripe_fees');
CREATE TYPE ledger_txn_kind    AS ENUM ('topup','lease_hold','lease_charge','hold_release','payout','refund','chargeback','adjustment');
CREATE TYPE payout_status       AS ENUM ('pending','in_transit','paid','failed','canceled');
CREATE TYPE stripe_event_status AS ENUM ('received','processed','ignored','failed');
```

Native enums are self-documenting and cheap; new values are added with `ALTER TYPE … ADD VALUE` (append-only, which suits an audited system — you never *remove* a state that history references).

## 3. Identity & accounts — `users`

Roles are **additive** (`DESIGN.md` §10) and authoritative in Cognito groups; the row mirrors identity + payment linkage.

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK (`gen_random_uuid()`) | internal id |
| `cognito_sub` | `text` UNIQUE NOT NULL | Cognito subject |
| `email` | `text` UNIQUE NOT NULL | |
| `status` | `user_status` NOT NULL DEFAULT `'active'` | suspend gates all activity |
| `stripe_customer_id` | `text` UNIQUE | created on first top-up (consumer side) |
| `connect_account_id` | `text` UNIQUE | Stripe Connect account (host side) |
| `connect_status` | `connect_status` NOT NULL DEFAULT `'none'` | gates a host going `online` (§10) |
| `created_at` / `updated_at` | `timestamptz` NOT NULL | |

Each user gets **exactly one `user_wallet`** and (if a host) **one `host_earnings`** ledger account, created lazily and pinned by the unique constraint in §8.

### `api_keys` — consumer machine credentials

A long-lived **machine bearer** for the authenticated `/v1` surface (`API.md` §2) — a machine client (first: the orchestrator app) drives the API with one key instead of the Cognito JWT flow. The key value is **shown once** at mint and stored **hashed only** (SHA-256), exactly like the host agent token (§4). Roles cannot come from Cognito groups for a key, so its granted **scopes** ride on the row (constrained to the role labels). The auth layer tells a key from a JWT by its `wck_` prefix.

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK (`gen_random_uuid()`) | |
| `user_id` | `uuid` NOT NULL → `users(id)` | the user the key authenticates as |
| `name` | `text` NOT NULL | user-supplied label for the listing UX |
| `token_hash` | `text` NOT NULL UNIQUE | **hash only** (SHA-256); backs the O(1) hashed lookup |
| `token_prefix` | `text` NOT NULL | short non-secret display prefix (e.g. `wck_live_ab12`) |
| `scopes` | `text[]` NOT NULL DEFAULT `'{}'` | granted role labels, CHECK ⊆ `{consumer, host, admin}` |
| `created_at` | `timestamptz` NOT NULL | |
| `last_used_at` | `timestamptz` | best-effort touch on use; may lag |
| `revoked_at` | `timestamptz` | NULL = active; a revoked key no longer resolves |

Format `wck_live_<64-hex>` (256-bit CSPRNG). Lookup is `token_hash = … AND revoked_at IS NULL`, so a revoked key **fails closed**. Index: `(user_id)`; the UNIQUE `token_hash` backs the auth lookup.

## 4. Hosts & priced images

### `hosts`

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | |
| `owner_user_id` | `uuid` NOT NULL → `users(id)` | |
| `name` / `label` | `text` | display + region/label |
| `status` | `host_status` NOT NULL DEFAULT `'offline'` | |
| `agent_token_hash` | `text` NOT NULL | **hash only**: lowercase hex SHA-256 of the full `wht_live_<64-hex>` token (deterministic, so the tunnel resolves a presented token by an indexed lookup; a salted password hash would not allow that). The token itself is shown once at issuance/rotation |
| `agent_token_prefix` | `text` | short non-secret prefix for identification/rotation UX (`wht_live_` + 4 hex chars) |
| `wisp_version` / `agent_version` | `text` | mirror `hello.wispVersion` / `hello.agentVersion`; stamped at handshake by the tunnel presence hook (task #182), left `NULL` when the agent sends nothing or the value is blank. Surfaced read-only on the owner and admin host views |
| `max_leases` / `max_streams` | `int` | the top-level `hello.capacity.{maxLeases,maxStreams}`; stamped at handshake by the tunnel presence hook (task #182) and surfaced on the owner and admin host views. A non-positive value is stored as `NULL`. These fields are **advisory only**: per-host admission is enforced against the live `capability.capacity.max_contracts` snapshot the heartbeat capability refresh maintains (`TUNNEL.md` §5, task #571). A heartbeat capability refresh (task #61) never rewrites them, so the live in-memory ceiling is authoritative for placement while the hosts row reflects what the agent last handshaked with |
| `isolation_levels` | `text[]` NOT NULL DEFAULT `ARRAY['shared']` | effective isolation levels the host offers, from the `hello`/`heartbeat` capability (`TUNNEL.md` §5); surfaced in `GET /v1/catalog`. Stored as free-form `text` (like `host_images.networks`), not an enum. Migration `0010_HostIsolation` |
| `default_isolation` | `text` NOT NULL DEFAULT `'shared'` | level applied when a lease omits `isolation`. Migration `0010_HostIsolation` |
| `gpu_classes` | `text[]` NOT NULL DEFAULT `'{}'` | distinct GPU hardware classes the host advertises, from the `hello`/`heartbeat` `gpu` block (`TUNNEL.md` §5). Opaque free-form `text` (like `isolation_levels`), never interpreted; empty for a host with no GPU (an older agent). Migration `0012_HostGpu` |
| `gpu_count` | `int` NOT NULL DEFAULT `0` | total GPU devices the host advertises (not distinct classes). Migration `0012_HostGpu` |
| `last_seen_at` | `timestamptz` | stamped when presence flips the host `online` (the handshake instant) and `offline` (the last-healthy instant, `TUNNEL.md` §8); not refreshed per heartbeat |
| `created_at` / `updated_at` | `timestamptz` | |

Indexes: `(owner_user_id)`, partial `(status) WHERE status='online'` for the catalog, `(agent_token_hash)` for the tunnel's token lookup. Admin moderation sets `status='suspended'`; unsuspend returns the row to `offline` and the tunnel lifecycle brings it back `online`.

### `host_images` — the priced allow-list

The overlay of *price* on wisp's capability (`DESIGN.md` §12); wisp itself stays money-blind.

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | |
| `host_id` | `uuid` NOT NULL → `hosts(id)` ON DELETE CASCADE | |
| `image_ref` | `text` NOT NULL | must be in the host's wisp allow-list (validated live from `hello.capability`) |
| `price_cents_per_min` | `bigint` NOT NULL CHECK (`>= 0`) | host-set |
| `networks` | `network_mode[]` NOT NULL | subset the host permits for this image |
| `max_ttl_seconds` | `int` NOT NULL | |
| `max_cpus` | `numeric(6,3)` · `max_memory_mb` `int` · `max_pids` `int` | legacy resource ceilings — vestigial since task #570 removed the free-form lease knobs they governed; kept on the offer row, no longer consulted at lease create |
| `cpus` | `int` NULL | **sized offer** (task #569): the EXACT vCPU count this offer provisions per lease. `NULL` = the host's own per-lease policy default applies downstream. Positive when present. Migration `0014_ImageResourceProfile` |
| `memory_mb` | `int` NULL | **sized offer** (task #569): the EXACT memory (MB) this offer provisions per lease. `NULL` = the host's own per-lease policy default applies downstream. Positive when present. Migration `0014_ImageResourceProfile` |
| `gpus` | `int` NOT NULL DEFAULT `0` | **sized offer** (task #569): the EXACT whole exclusive GPU devices this offer provisions per lease (0 = no GPU access on this offer); validated live against `hosts.gpu_count` — over-ask rejects, never clamps. GPU access is priced into this offer, not a separate rate table. Renamed from `max_gpus` (a consumer-chosen ceiling) in migration `0014_ImageResourceProfile`; the column originated in `0013_ImageAndLeaseGpu` (task #522) |
| `enabled` | `bool` NOT NULL DEFAULT `true` | |
| `created_at` / `updated_at` | `timestamptz` | |

Unique `(host_id, image_ref)`. Pricing edits apply to **new** leases only (running leases keep their snapshot — §6).

An offer now **sells a size**: image + a fixed resource profile (`cpus`, `memory_mb`, `gpus`) at a price, like an instance type — so the price reflects the provisioned resources rather than a flat per-image rate over consumer-chosen knobs. A `NULL` `cpus`/`memory_mb` defers to the host's own per-lease policy default; `gpus` is always an exact count (`0` = none). A lease provisions **exactly** this profile and stamps it on the lease row (task #570): `POST /v1/leases` no longer accepts a `resources` object or `gpus` count (both `validation_error`), and the removed `disk_gb` knob is gone entirely.

## 5. Leases — full DDL

```sql
CREATE TABLE leases (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  consumer_user_id    uuid NOT NULL REFERENCES users(id),
  host_id             uuid NOT NULL REFERENCES hosts(id),
  host_image_id       uuid NOT NULL REFERENCES host_images(id),

  -- immutable snapshots taken at creation (host may reprice later)
  image_ref           text  NOT NULL,
  network             network_mode NOT NULL,
  isolation           text  NOT NULL DEFAULT 'shared',  -- ordered shared<sandboxed<vm (migration 0011)
  -- the RESOLVED sized profile, stamped at create (task #570/#578, migration 0015); consumer no longer chooses it
  cpus                integer,  -- exact vCPUs provisioned: offer value, else host per-lease cap; NULL only when neither exists (task #578)
  memory_mb           integer,  -- exact memory (MB) provisioned: offer value, else host per-lease cap; NULL only when neither exists (task #578)
  gpus                integer NOT NULL DEFAULT 0,  -- whole exclusive GPU devices provisioned from the offer; 0 = none (migration 0013)
  ttl_seconds         integer NOT NULL CHECK (ttl_seconds > 0),
  price_cents_per_min bigint  NOT NULL CHECK (price_cents_per_min >= 0),
  currency            text    NOT NULL DEFAULT 'usd',

  status              lease_status NOT NULL DEFAULT 'pending',
  end_reason          lease_end_reason,
  wisp_contract_id    text,               -- opaque id from the host's wisp

  hold_txn_id         uuid REFERENCES ledger_transactions(id),  -- the up-front hold

  -- metering timeline (Wisper's clock; excludes suspended gaps, TUNNEL.md §8)
  created_at          timestamptz NOT NULL DEFAULT now(),
  started_at          timestamptz,        -- first lease.ready → meter start
  last_metered_at     timestamptz,        -- watermark of billed time
  billable_seconds    bigint NOT NULL DEFAULT 0,  -- accrued, excludes gaps
  ended_at            timestamptz,
  suspended_at        timestamptz,        -- when the row moved to 'suspended' (wall-clock); cleared on resume/revive/end (migration 0016)

  CHECK ((status = 'ended') = (ended_at IS NOT NULL) OR status <> 'ended')
);

CREATE INDEX leases_consumer_idx ON leases (consumer_user_id, created_at DESC);
CREATE INDEX leases_host_active_idx ON leases (host_id) WHERE status IN ('active','suspended');
CREATE INDEX leases_suspended_at_idx ON leases (suspended_at) WHERE status = 'suspended';  -- the durable grace sweep's driving query
```

History: `0005_Leases` created `cpus numeric(6,3)`, `memory_mb integer` and `pids integer` as consumer-chosen snapshots; `0015_LeaseProvisionedProfile` dropped all three and re-added `cpus`/`memory_mb` as nullable `integer` stamped from the offer (`pids` is gone). `0013` added `gpus`, `0011` added `isolation`, `0016` added `suspended_at`.

**State machine** (`TUNNEL.md` §8 governs `suspended`):
```
pending → provisioning → active ⇄ suspended → ended
                       ↘ failed          ↘ ended
```
As built: `POST /v1/leases` waits for `lease.ready` and then inserts the row directly as `active` (with `started_at`/`last_metered_at` set), so `pending`/`provisioning` are never persisted today. `active → failed` (`end_reason = payment_failed`) happens when the hold cannot be posted after provisioning (the container is torn down). `active → suspended` on tunnel loss; `suspended → active` on resume; `suspended → ended` with `host_disconnect` (grace expiry or the durable sweep), `container_lost` (reported gone on reconnect/heartbeat) or `admin`; `active → ended` with `released` (consumer DELETE), `expired` (`lease.ended` from wisp), `container_lost` (heartbeat set-diff) or `admin`; and `ended (host_disconnect) → active` on a **revive** when the container survived the outage (a paid lease is re-held first, else it ends `payment_failed`). Every end and resume transition is a compare-and-set on the status that was read, so concurrent drivers converge on one transition. `suspended` never bills; `billable_seconds` accrues only over healthy intervals, so the wallet is charged for real usage, not wall-clock through outages.

## 6. Metering — `lease_usage`

One row per **flushed metering interval** (tick + on lease end), each tied to the `lease_charge` ledger transaction that debited the hold. Idempotent on `(lease_id, period_start)`. An interval that is worth less than one whole cent at the lease price is not flushed; the watermark stays put and the seconds roll into the next tick (no value is lost). A free (`price 0`) lease advances its watermark and `billable_seconds` but writes no `lease_usage` row and no ledger transaction (`charge_txn_id` is NOT NULL).

```sql
CREATE TABLE lease_usage (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  lease_id           uuid NOT NULL REFERENCES leases(id),
  period_start       timestamptz NOT NULL,
  period_end         timestamptz NOT NULL,
  billable_seconds   integer NOT NULL CHECK (billable_seconds >= 0),
  amount_cents       bigint  NOT NULL CHECK (amount_cents >= 0),
  platform_fee_cents bigint  NOT NULL CHECK (platform_fee_cents >= 0),
  host_payout_cents  bigint  NOT NULL CHECK (host_payout_cents >= 0),
  charge_txn_id      uuid NOT NULL REFERENCES ledger_transactions(id),
  created_at         timestamptz NOT NULL DEFAULT now(),
  CHECK (amount_cents = platform_fee_cents + host_payout_cents),
  UNIQUE (lease_id, period_start)
);
```

## 7. The double-entry ledger

Three tables. Every cent that exists in Wisper is a balance derived from these.

```sql
CREATE TABLE ledger_accounts (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  kind           ledger_account_kind NOT NULL,
  owner_user_id  uuid REFERENCES users(id),   -- NULL for singletons
  currency       text NOT NULL DEFAULT 'usd',
  -- normal side: user_wallet/host_earnings/lease_holds/platform_revenue = credit; platform_cash/stripe_fees = debit
  balance_cents  bigint NOT NULL DEFAULT 0,    -- maintained by trigger; = natural positive balance
  created_at     timestamptz NOT NULL DEFAULT now(),
  -- one wallet & one earnings account per user
  UNIQUE (kind, owner_user_id)
);

-- Platform singletons (owner NULL) need their own partial unique index: in Postgres,
-- NULLs are distinct, so UNIQUE (kind, owner_user_id) alone would allow duplicates.
CREATE UNIQUE INDEX ledger_accounts_singleton_idx
  ON ledger_accounts (kind) WHERE owner_user_id IS NULL;

CREATE TABLE ledger_transactions (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  kind             ledger_txn_kind NOT NULL,
  lease_id         uuid REFERENCES leases(id),        -- when lease-scoped
  external_ref     text,                              -- stripe pi/tr/ch id, etc.
  idempotency_key  text UNIQUE,                       -- dedupes retries/webhooks
  memo             text,
  created_at       timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE ledger_entries (
  id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  transaction_id uuid NOT NULL REFERENCES ledger_transactions(id),
  account_id     uuid NOT NULL REFERENCES ledger_accounts(id),
  debit_cents    bigint NOT NULL DEFAULT 0 CHECK (debit_cents  >= 0),
  credit_cents   bigint NOT NULL DEFAULT 0 CHECK (credit_cents >= 0),
  lease_id       uuid REFERENCES leases(id),          -- for per-lease hold tracking
  created_at     timestamptz NOT NULL DEFAULT now(),
  CHECK ( (debit_cents = 0) <> (credit_cents = 0) )   -- exactly one side per entry
);

CREATE INDEX ledger_entries_account_idx ON ledger_entries (account_id, id);
CREATE INDEX ledger_entries_txn_idx     ON ledger_entries (transaction_id);
CREATE INDEX ledger_entries_lease_idx   ON ledger_entries (lease_id) WHERE lease_id IS NOT NULL;
```

### Invariants (enforced in the database, not just app code)

**(a) Every transaction balances** — deferred so all entries of a txn are inserted first, checked at commit:

```sql
CREATE CONSTRAINT TRIGGER ledger_txn_balanced
AFTER INSERT ON ledger_entries
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION assert_txn_balanced();

-- assert_txn_balanced(): SELECT into sums for NEW.transaction_id;
--   IF SUM(debit_cents) <> SUM(credit_cents) THEN RAISE EXCEPTION 'unbalanced ledger txn %'
```

**(b) Entries & transactions are immutable** — `BEFORE UPDATE OR DELETE` triggers on both tables `RAISE EXCEPTION`. Corrections are reversing `adjustment`/`refund` transactions.

**(c) Balances are maintained, never authored** — `AFTER INSERT` on `ledger_entries` updates `ledger_accounts.balance_cents` by the account's normal side:
`credit-normal: balance += credit − debit; debit-normal: balance += debit − credit`.

**(d) Non-negative earmarked liabilities** — the same trigger `RAISE`s if a `user_wallet` or `lease_holds` account would go **below zero**. This is the hard guarantee that a consumer can never outspend their wallet and a hold can't be over-drawn — with **one deliberate exception**: a `chargeback` transaction may drive a `user_wallet` negative (the trigger checks the transaction kind), because a chargeback is a genuine debt the platform records rather than refuses (`PAYMENTS.md` §7). `lease_holds` is guarded unconditionally.

**(e) Reconciliation** re-derives `SUM` per account from `ledger_entries` and compares it to `balance_cents`; any drift should page an operator. Balances are a cache; the journal is truth. *Status:* a scheduled background loop (`LedgerReconcileHostedService`, `LedgerReconcile:Enabled` + `LedgerReconcile:IntervalMinutes`, default every 15 minutes) runs the pass, logs any drift at warning (per-account: kind, owner, maintained vs derived, drift), and records the outcome on the admin overview (`GET /v1/admin/overview` returns `ledger_reconcile.has_drift`, and `health` flips to `ledger_drift` when non-zero drift is seen). The loop is off in the in-memory persistence mode (no database to reconcile) and off when disabled by config; multi-instance safe via a Postgres session-scope advisory lock (`pg_try_advisory_lock`), so exactly one instance runs each pass and a crash releases the lock automatically.

## 8. Money flows as balanced transactions

Every flow is `Σ debit = Σ credit`. Debit/credit chosen by each account's normal side (§7). Example figures in cents.

| Flow (`kind`) | Debit | Credit | Guard |
|---|---|---|---|
| **top-up** (pay 1000, Stripe fee 59) | `platform_cash` 941, `stripe_fees` 59 | `user_wallet` 1000 | Stripe `payment_intent.succeeded` (idempotent on event id); platform absorbs the processor fee |
| **lease_hold** (estimate = ⌈ttl/60⌉·price, e.g. 500) | `user_wallet` 500 | `lease_holds` 500 | requires `user_wallet.balance ≥ 500` — the non-negative trigger (d) is the backstop |
| **lease_charge** (tick 120 → host 102 + fee 18) | `lease_holds` 120 | `host_earnings` 102, `platform_revenue` 18 | fee split from `platform_policy.fee_bps` (§11), fee floored so `fee + host = amount` exactly; a zero leg is omitted. Requires an active policy row (a flush with no policy throws) |
| **hold_release** (unused 380 at end) | `lease_holds` 380 | `user_wallet` 380 | returns the earmark to spendable balance |
| **payout** (transfer 102 to host) | `host_earnings` 102 | `platform_cash` 102 | Stripe Connect transfer; money leaves platform |
| **refund** (top-up reversed) | `user_wallet` 1000 | `platform_cash` 941, `stripe_fees` 59 | requires wallet funds; else clawback policy |
| **chargeback** / **adjustment** | (as needed) | (as needed) | admin-initiated, always audited (§13) |

The **hold model** is what makes prepaid billing safe: at `POST /leases`, Wisper holds the estimated maximum out of the wallet (so the consumer is guaranteed able to pay for what they can consume), charges *actual* metered minutes out of the hold, and releases the remainder at end. A consumer literally cannot start a lease they can't afford, and a host is guaranteed funds exist for metered time.

**Zero-priced images degenerate to zero flows.** A `price_cents_per_min = 0` image (a free tier or a self-hosted operator pricing their own box at cost) makes every figure above `0`: the `lease_hold`, each `lease_charge`, and the `hold_release` are all zero-amount and are therefore **skipped, not posted** — a `0=0` transaction carries no money, and the `ledger_entries` CHECK (`(debit_cents = 0) <> (credit_cents = 0)`, exactly one side per entry) hard-rejects a `0/0` row anyway, so writing one is impossible as well as vacuous. A free lease thus never touches the ledger, and no balance can go negative from it (`PAYMENTS.md` §4).

## 9. Stripe integration

- **Consumer side:** `users.stripe_customer_id`; top-ups create `payment_intent`s; the `topup` ledger txn is keyed by the Stripe event id.
- **Host side:** `users.connect_account_id` + `connect_status`; a host cannot go `online` until `connect_status = 'enabled'` **unless** every enabled image it offers is priced at 0 (the zero-earn arm, `PAYMENTS.md` §5), and cannot enable a priced image without Connect. Payouts require `enabled`. Onboarding/KYC is Stripe-hosted; we store only the account id + status (`pending` once an Express account is created, then `enabled`/`restricted` from `account.updated`; `disabled` is defined but never written today).
- **`stripe_events`** — webhook idempotency and audit; **every webhook is processed exactly once**:

| column | type | notes |
|---|---|---|
| `id` | `text` PK | Stripe event id (dedupe key) |
| `type` | `text` NOT NULL | `payment_intent.succeeded`, `account.updated`, `transfer.*`, … |
| `payload` | `jsonb` NOT NULL | raw event |
| `status` | `stripe_event_status` NOT NULL DEFAULT `'received'` | received→processed/ignored/failed |
| `received_at` / `processed_at` | `timestamptz` | `received_at` NOT NULL DEFAULT `now()` |
| `error` | `text` | on failure, for retry/inspection |

- **`payouts`** — draining `host_earnings` via Connect transfers:

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | |
| `host_user_id` | `uuid` NOT NULL → `users(id)` | |
| `amount_cents` | `bigint` CHECK (`> 0`) | |
| `currency` | `text` NOT NULL DEFAULT `'usd'` | |
| `period_start` / `period_end` | `timestamptz` | earnings window paid |
| `status` | `payout_status` | pending→in_transit→paid/failed |
| `stripe_transfer_id` | `text` UNIQUE | |
| `payout_txn_id` | `uuid` → `ledger_transactions(id)` | the `payout` entry |
| `error` | `text` | on failure, for retry/inspection |
| `created_at` / `updated_at` | `timestamptz` | |

## 10. Idempotency — `idempotency_keys`

Guards money-mutating API endpoints (`POST /v1/leases`, `/v1/billing/topup`, `/v1/billing/refund`, `/v1/payouts`, `/v1/admin/refunds`, `/v1/admin/adjustments`) so client retries are safe.

| column | type | notes |
|---|---|---|
| `key` | `text` PK | the client-supplied `Idempotency-Key`; the PK is global, so the same key from a different user is a `409 conflict` rather than a separate record |
| `user_id` | `uuid` NOT NULL → `users(id)` | scope |
| `request_hash` | `text` NOT NULL | SHA-256 of the raw body; rejects key reuse with a *different* body |
| `response_status` | `int` · `response_body` `jsonb` | replayed on duplicate; NULL while in progress |
| `status` | `text` NOT NULL DEFAULT `'in_progress'` CHECK (`in_progress`/`done`) | in-progress lock prevents concurrent dupes; the row is deleted (lock released) when the operation fails |
| `created_at` / `expires_at` | `timestamptz` NOT NULL | TTL = 24 h; an expired row is swept lazily when its key is presented again, and a scheduled background loop (`IdempotencySweepHostedService`, `IdempotencySweep:Enabled` + `IdempotencySweep:IntervalMinutes`, default every 60 minutes) proactively deletes stale rows so the table doesn't accumulate between low-traffic windows. The loop is off in the in-memory persistence mode and off when disabled by config; multi-instance safe via a Postgres session-scope advisory lock, so exactly one instance runs each sweep |

(Ledger-level idempotency is *also* enforced by `ledger_transactions.idempotency_key`, so even a bug above the DB cannot double-post. The keys in use: `lease_hold:<lease>`, `lease_hold:<lease>:revive`, `hold_release:<lease>[:<hold_txn>]`, `lease_charge:<lease>:<period_start>`, `refund:<stripe refund id>`, `adjustment:<api key>`, the Stripe event id for top-ups and chargebacks, and `payouts.id` for payouts.)

## 11. Platform policy — `platform_policy`

Admin-tunable, **versioned** (append-only rows; the active row is the latest) so every pricing/limit change is auditable and a lease's fee basis is reproducible.

| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | |
| `fee_bps` | `int` NOT NULL CHECK (0..10000) | platform cut in basis points |
| `min_topup_cents` | `bigint` NOT NULL DEFAULT 0 CHECK (`>= 0`) | |
| `max_concurrent_leases_per_user` | `int` | NULL = unlimited; counts `pending`/`provisioning`/`active`/`suspended` leases |
| `max_ttl_seconds_cap` | `int` | global TTL ceiling in seconds over per-image `max_ttl_seconds` (task #181): a create whose `ttl_seconds` exceeds this cap is refused with `validation_error` naming the requested TTL and the cap (never silently clamped). NULL = no global ceiling; the per-image cap is then the only bound. With no active policy row at all, no cap applies either |
| `min_isolation` | `text` | global minimum isolation floor, NULL = no floor; a lease below it is rejected (`API.md`). Must be one of `shared`/`sandboxed`/`vm`. Migration `0011_LeaseIsolation` |
| `first_topup_max_cents` | `bigint` | fraud guard — first-top-up hold cap (`PAYMENTS.md` §7) |
| `new_account_window_hours` | `int` | fraud guard — how long an account counts as "new" |
| `new_account_max_topup_cents_per_day` | `bigint` | fraud guard — new-account top-up velocity (rolling 24h) |
| `max_spend_cents_per_day` | `bigint` | fraud guard — per-user daily spend cap (by lease holds) |
| `effective_from` | `timestamptz` NOT NULL DEFAULT `now()` | the active row is the newest `effective_from` at or before now, so a future value schedules a version |
| `created_by` | `uuid` → `users(id)` | admin (NULL for a seed row) |

The four fraud-guard columns are all NULL-able (NULL = "no limit"), each with a `CHECK (>= 0)` on non-NULL values (`0008_FraudPolicy`); they carry the day-one, deterministic fraud controls (`PAYMENTS.md` §7, §13) the billing paths enforce at top-up and lease start. There is no seed row: until an admin publishes a policy, `fee_bps` is undefined and a paid metering flush throws (no charge is posted; the tick retries), while the caps and minimum top-up all read as "no limit".

## 12. Audit — `audit_log`

Append-only record of every admin/policy/money-sensitive action. Append-only is DB-enforced, not just convention: a `BEFORE UPDATE OR DELETE` trigger (`audit_log_immutable`, reusing the ledger's `ledger_forbid_mutation()`) raises on any mutation.

| column | type | notes |
|---|---|---|
| `id` | `bigint` identity PK | |
| `actor_user_id` | `uuid` → `users(id)` | admin or system |
| `action` | `text` | as written today: `policy.update`, `host.suspend`/`host.unsuspend`, `user.suspend`/`user.unsuspend`, `admin.refund`, `billing.refund` (the self-serve refund, actor = the consumer), `ledger.adjustment`, `lease.admin_end`, `user.chargeback_suspend` (system actor, NULL). Payouts (scheduled or on-demand) are **not** audited today; the `payouts` row is their record |
| `target_type` / `target_id` | `text` / `uuid` | |
| `meta` | `jsonb` | before/after, amounts, reason |
| `created_at` | `timestamptz` | |

## 13. Index & constraint summary (beyond PKs)

- `users(cognito_sub)`, `users(email)`, `users(stripe_customer_id)`, `users(connect_account_id)` — unique.
- `api_keys(token_hash)` — unique (backs the hashed auth lookup); `api_keys(user_id)`.
- `hosts(owner_user_id)`; `hosts(agent_token_hash)`; partial `hosts(status) WHERE 'online'`; `host_images(host_id,image_ref)` unique; `host_images(host_id)`.
- `leases(consumer_user_id, created_at DESC)`; partial `leases(host_id) WHERE status IN ('active','suspended')`; partial `leases(suspended_at) WHERE status = 'suspended'` (the durable grace sweep).
- `lease_usage(lease_id, period_start)` unique; `lease_usage(lease_id)`.
- `ledger_entries(account_id, id)`, `(transaction_id)`, partial `(lease_id)`.
- `ledger_transactions(idempotency_key)` unique; partial unique `ledger_accounts(kind) WHERE owner_user_id IS NULL` (platform singletons, §7); `stripe_events(id)` PK dedupe.
- `payouts(stripe_transfer_id)` unique; `payouts(host_user_id, created_at DESC)`.
- `idempotency_keys(expires_at)` (TTL sweep); `platform_policy(effective_from DESC)` (active-row lookup).
- `audit_log(actor_user_id, created_at DESC)`; `audit_log(target_type, target_id)`.

## 14. Crash-safety & consistency

- **Metering durability.** The meter flushes a `lease_usage` row + `lease_charge` ledger txn on a fixed **tick (`Metering:TickSeconds`, default 60s; `Metering:Enabled` turns the loop off)** and on lease end. Each tick reloads every `active` lease from the DB, skips any whose host has no live tunnel on this instance (the disconnect path flushes those to last-healthy and suspends them), and bills from `last_metered_at` up to `now` capped at both `started_at + ttl_seconds` and the host's last-healthy instant, so a post-TTL or blind-window tail is never billed. The charge for an interval is the cumulative integer `⌊billable_seconds × price / 60⌋` minus what was already charged, so per-tick rounding never drifts and the running total never exceeds the hold. A manager crash loses at most one un-flushed tick; on restart, active leases are reloaded and metering resumes from `leases.last_metered_at`. Each flush is idempotent on `(lease_id, period_start)`, so a retried flush can't double-charge. Note the tick is per instance: with several instances, each meters only the hosts whose tunnel it owns.
- **Transactional ledger writes.** A money movement = one DB transaction that inserts the `ledger_transaction` + all balanced `ledger_entries`; the deferred balance trigger and account-balance update commit atomically. Concurrent writers to the same account serialize on the `ledger_accounts` row, which the balance trigger locks `FOR UPDATE`; the service runs at the default `READ COMMITTED` and relies on that row lock plus the unique `idempotency_key` (a concurrent poster of the same key gets the winner's transaction back). A trigger `RAISE` is translated to a typed ledger violation (unbalanced, hold over-drawn, insufficient funds).
- **Reconciliation.** The balance re-derivation (§7e) is the safety net that turns any silent drift into a page, not a loss. A scheduled background loop (`LedgerReconcileHostedService`, `LedgerReconcile:Enabled` + `LedgerReconcile:IntervalMinutes`, default every 15 minutes) runs the pass on every configured-database instance, coordinated via a Postgres session-scope advisory lock so exactly one instance runs each pass. Findings are logged (warning per drifted account) and surfaced on the admin overview: `GET /v1/admin/overview` returns `ledger_reconcile` (`ran_at`, `accounts_checked`, `drift_account_count`, `total_absolute_drift_cents`, `has_drift`) and flips `health` to `ledger_drift` when the last observed pass drifted. Off in the in-memory persistence mode (nothing to reconcile).
- **Idempotency-key TTL sweep.** A scheduled background loop (`IdempotencySweepHostedService`, `IdempotencySweep:Enabled` + `IdempotencySweep:IntervalMinutes`, default every 60 minutes) deletes expired `idempotency_keys` rows so the table doesn't bloat between low-traffic windows (the lazy retry-path sweep still runs on every presented expired key). Coordinated by a Postgres advisory lock so exactly one instance runs each pass. Off in the in-memory persistence mode.
- **Backpressure to consumers:** if a top-up/hold cannot be posted (e.g. wallet insufficient at hold time), `POST /leases` fails *before* any `lease.create` frame reaches the host — no compute is provisioned that can't be paid for.

## 15. Environments

One managed PostgreSQL instance, **one logical database per environment** (`wisper_dev`, `wisper_prod`) with identical migrations; connection selected by env config. DbUp runs the same ordered migration set against each. (Dedicated instances per environment later — a deployment change, not a schema change.)

## 16. Deliberate scope boundaries

- **Single currency (`usd`).** Every money table carries `currency` and accounts are currency-scoped, so adding a currency is new accounts + FX handling, never a backfill. Multi-currency *logic* (FX, per-currency payout) is out of scope until there's a non-USD host or consumer — not because it's hard, but because building FX with one currency in the system would be untested speculation.
- **Pricing granularity.** Prices are quoted per minute (`price_cents_per_min`) and the hold is sized per whole minute (`⌈ttl/60⌉ × price`), but the charge itself is computed from `billable_seconds` as `⌊seconds × price / 60⌋` integer cents (§14, `DESIGN.md` §11), i.e. per-second accrual with a whole-cent floor and no 1-minute minimum. Changing that rule is a pricing-rule change, not a schema migration.
- **Tax handling** is delegated to Stripe (Stripe Tax / Connect handles host tax reporting); Wisper does not compute or store tax tables. If direct tax calculation is ever required it attaches to `lease_usage`/`payouts` additively.
```
