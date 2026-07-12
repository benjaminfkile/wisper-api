-- 0005_Leases.sql — leases and their flushed metering intervals (docs/DATA_MODEL.md §5, §6, §13).
--
-- A lease snapshots the priced image at creation (immutable — the host may reprice later, §6) and
-- carries the metering timeline on Wisper's clock (started_at → last_metered_at watermark →
-- billable_seconds, excluding suspended gaps, docs/TUNNEL.md §8). lease_usage is one row per flushed
-- tick (and on lease end), idempotent on (lease_id, period_start) so a retried flush can't
-- double-charge (§14).
--
-- The lease/ledger FKs are circular (leases.hold_txn_id → ledger_transactions, and
-- ledger_transactions.lease_id → leases); the ledger tables land in P2.4. hold_txn_id and
-- charge_txn_id are therefore plain uuid columns here, and P2.4 adds their FK constraints once
-- ledger_transactions exists.

CREATE TYPE lease_status     AS ENUM ('pending', 'provisioning', 'active', 'suspended', 'ended', 'failed');
CREATE TYPE lease_end_reason AS ENUM ('released', 'expired', 'host_disconnect', 'container_lost', 'admin', 'payment_failed');

CREATE TABLE leases (
    id                  uuid         PRIMARY KEY DEFAULT gen_random_uuid(),
    consumer_user_id    uuid         NOT NULL REFERENCES users (id),
    host_id             uuid         NOT NULL REFERENCES hosts (id),
    host_image_id       uuid         NOT NULL REFERENCES host_images (id),

    -- immutable snapshots taken at creation (host may reprice later)
    image_ref           text         NOT NULL,
    network             network_mode NOT NULL,
    cpus                numeric(6, 3),
    memory_mb           integer,
    pids                integer,
    ttl_seconds         integer      NOT NULL CHECK (ttl_seconds > 0),
    price_cents_per_min bigint       NOT NULL CHECK (price_cents_per_min >= 0),
    currency            text         NOT NULL DEFAULT 'usd',

    status              lease_status NOT NULL DEFAULT 'pending',
    end_reason          lease_end_reason,
    wisp_contract_id    text,               -- opaque id from the host's wisp

    hold_txn_id         uuid,               -- the up-front hold; FK → ledger_transactions added in P2.4

    -- metering timeline (Wisper's clock; excludes suspended gaps, TUNNEL.md §8)
    created_at          timestamptz  NOT NULL DEFAULT now(),
    started_at          timestamptz,        -- first lease.ready → meter start
    last_metered_at     timestamptz,        -- watermark of billed time
    billable_seconds    bigint       NOT NULL DEFAULT 0,  -- accrued, excludes gaps
    ended_at            timestamptz,

    CHECK ((status = 'ended') = (ended_at IS NOT NULL) OR status <> 'ended')
);

CREATE INDEX leases_consumer_idx    ON leases (consumer_user_id, created_at DESC);
CREATE INDEX leases_host_active_idx ON leases (host_id) WHERE status IN ('active', 'suspended');

CREATE TABLE lease_usage (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    lease_id           uuid        NOT NULL REFERENCES leases (id),
    period_start       timestamptz NOT NULL,
    period_end         timestamptz NOT NULL,
    billable_seconds   integer     NOT NULL CHECK (billable_seconds >= 0),
    amount_cents       bigint      NOT NULL CHECK (amount_cents >= 0),
    platform_fee_cents bigint      NOT NULL CHECK (platform_fee_cents >= 0),
    host_payout_cents  bigint      NOT NULL CHECK (host_payout_cents >= 0),
    charge_txn_id      uuid        NOT NULL,  -- FK → ledger_transactions added in P2.4
    created_at         timestamptz NOT NULL DEFAULT now(),
    CHECK (amount_cents = platform_fee_cents + host_payout_cents),
    UNIQUE (lease_id, period_start)
);

CREATE INDEX lease_usage_lease_idx ON lease_usage (lease_id);
