-- 0007_StripeIdempotencyPolicyAudit.sql — the remaining money-adjacent infra tables
-- (docs/DATA_MODEL.md §9–§12): Stripe webhook dedupe, host payouts, API idempotency, the
-- versioned platform policy, and the append-only audit log.
--
-- These are the guard rails around every path that moves money:
--   • stripe_events  (§9)  — every webhook is processed exactly once; the PK = Stripe event id is
--                            the dedupe key (persist-then-process, docs/PAYMENTS.md §9).
--   • payouts        (§9)  — draining host_earnings via Connect transfers; stripe_transfer_id is
--                            unique so a retried run can't double-pay (docs/PAYMENTS.md §6).
--   • idempotency_keys (§10) — client-retry safety on money-mutating POSTs: replay the stored
--                            response on the same key+body, 409 on key+different body, an in-progress
--                            lock while the first request is still running (docs/API.md §9).
--   • platform_policy (§11) — admin-tunable and *versioned* (append-only rows; the active row is the
--                            latest) so a lease's fee basis is reproducible and every change auditable.
--   • audit_log      (§12) — append-only record of every admin/policy/money-sensitive action.
--
-- payouts.payout_txn_id and audit_log/platform_policy user FKs all reference tables created by earlier
-- migrations (users in 0003, ledger_transactions in 0006), so no circular dependency remains here.

CREATE TYPE payout_status       AS ENUM ('pending', 'in_transit', 'paid', 'failed', 'canceled');
CREATE TYPE stripe_event_status AS ENUM ('received', 'processed', 'ignored', 'failed');

-- §9 — Stripe webhook idempotency & audit. Every webhook is processed exactly once: the handler
-- upserts on the event id (PK); an existing id means re-delivery and is acked without re-processing.
CREATE TABLE stripe_events (
    id           text                PRIMARY KEY,                  -- Stripe event id (dedupe key)
    type         text                NOT NULL,                     -- payment_intent.succeeded, account.updated, …
    payload      jsonb               NOT NULL,                     -- raw event
    status       stripe_event_status NOT NULL DEFAULT 'received',
    received_at  timestamptz         NOT NULL DEFAULT now(),
    processed_at timestamptz,
    error        text                                              -- on failure, for retry/inspection
);

-- §9 — host payouts: Connect transfers that drain host_earnings. The Stripe transfer id is unique so a
-- retried run can't double-pay; payout_txn_id ties the row to its `payout` ledger transaction.
CREATE TABLE payouts (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    host_user_id       uuid          NOT NULL REFERENCES users (id),
    amount_cents       bigint        NOT NULL CHECK (amount_cents > 0),
    currency           text          NOT NULL DEFAULT 'usd',
    period_start       timestamptz,                                -- earnings window paid
    period_end         timestamptz,
    status             payout_status NOT NULL DEFAULT 'pending',
    stripe_transfer_id text          UNIQUE,                       -- the Connect transfer id
    payout_txn_id      uuid          REFERENCES ledger_transactions (id),  -- the `payout` ledger entry
    error              text,                                       -- on failure, for retry/inspection
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now()
);

CREATE INDEX payouts_host_idx ON payouts (host_user_id, created_at DESC);

-- §10 — API idempotency. Guards money-mutating POSTs (top-up intent, POST /leases, payout trigger).
-- The first request inserts an `in_progress` row (the lock); it is completed to `done` with the stored
-- response, which is replayed verbatim on a same-key+same-body retry. A same-key+different-body retry is
-- a 409 conflict (request_hash mismatch); a retry while the first is still in-flight is a 409 in-progress
-- lock. Keys are user-scoped and TTL'd (expires_at) for cleanup.
CREATE TABLE idempotency_keys (
    key             text        PRIMARY KEY,                        -- client- or server-supplied
    user_id         uuid        NOT NULL REFERENCES users (id),     -- scope
    request_hash    text        NOT NULL,                           -- rejects key reuse with a different body
    response_status int,                                            -- replayed on duplicate (NULL until done)
    response_body   jsonb,                                          -- replayed on duplicate (NULL until done)
    status          text        NOT NULL DEFAULT 'in_progress' CHECK (status IN ('in_progress', 'done')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL
);

CREATE INDEX idempotency_keys_expiry_idx ON idempotency_keys (expires_at);

-- §11 — platform policy: admin-tunable and versioned (append-only rows; the active row is the one with
-- the latest effective_from). Never UPDATE a row — insert a new version so every pricing/limit change is
-- auditable and a lease's fee basis is reproducible.
CREATE TABLE platform_policy (
    id                             uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    fee_bps                        int         NOT NULL CHECK (fee_bps BETWEEN 0 AND 10000),  -- platform cut, basis points
    min_topup_cents                bigint      NOT NULL DEFAULT 0 CHECK (min_topup_cents >= 0),
    max_concurrent_leases_per_user int,
    max_ttl_seconds_cap            int,                                                       -- global ceiling over host limits
    effective_from                 timestamptz NOT NULL DEFAULT now(),
    created_by                     uuid        REFERENCES users (id)                          -- admin (NULL for a seed default)
);

-- The active policy is the newest effective_from; this index makes that lookup cheap.
CREATE INDEX platform_policy_effective_idx ON platform_policy (effective_from DESC);

-- §12 — audit log: append-only record of every admin/policy/money-sensitive action. Never updated or
-- deleted; the meta jsonb carries before/after, amounts, and reason.
CREATE TABLE audit_log (
    id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_user_id uuid        REFERENCES users (id),   -- admin or system (NULL)
    action        text        NOT NULL,                -- host.suspend, policy.update, payout.trigger, ledger.adjustment, …
    target_type   text,
    target_id     uuid,
    meta          jsonb,                               -- before/after, amounts, reason
    created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX audit_log_actor_idx  ON audit_log (actor_user_id, created_at DESC);
CREATE INDEX audit_log_target_idx ON audit_log (target_type, target_id);

-- audit_log is append-only, like the ledger (§12). Reuse the same guard function (created in 0006) to
-- forbid UPDATE/DELETE — corrections are new rows, never edits.
CREATE TRIGGER audit_log_immutable
    BEFORE UPDATE OR DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION ledger_forbid_mutation();
