-- 0018_BillingIncidents.sql: persist the platform-policy fallback signal so the admin overview
-- survives restarts and multi-instance (task #210).
--
-- Until this migration, the fallback counter surfaced on GET /v1/admin/overview and the health
-- policy_fallback flag lived in a process-local PolicyFallbackMonitor: every restart cleared it and,
-- with several instances, only the one whose meter ticked the offending flush knew about the
-- incident. Since a fallback is a billing-integrity signal an operator must act on, that shape is
-- wrong: the aggregate must be durable and readable from any instance.
--
-- Shape:
--   * billing_incidents  -- append-only journal of fallback events (and any future billing-integrity
--                          events sharing the same read pattern). The metering flush inserts one row
--                          per observed fallback, carrying the lease id, the policy row it fell back
--                          to (or NULL on the missing-at-flush branch), the kind, and when it
--                          happened. History is kept forever so an operator can audit the incident
--                          trail; the ack watermark on operational_state is what clears the overview
--                          health flag without erasing the history.
--   * operational_state  -- a single-row table for one-off operational watermarks. First user:
--                          policy_fallback_ack_at, the wall clock of the last admin ack. The overview
--                          reports fallback_count / last_fallback_* over rows with
--                          occurred_at > COALESCE(policy_fallback_ack_at, '-infinity'), so an ack
--                          zeros the badge on every instance while the incidents journal is intact.
--
-- The single-row constraint (id = 1 with an id-domain CHECK) means an INSERT of a second row is a
-- PK conflict rather than a silent duplicate, and the seed INSERT below is idempotent so
-- re-applying the migration is a no-op.
CREATE TABLE billing_incidents (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    kind         text        NOT NULL
        CHECK (kind IN ('policy_stale_fallback', 'policy_missing_at_flush')),
    lease_id     uuid,
    policy_id    uuid        REFERENCES platform_policy (id),
    occurred_at  timestamptz NOT NULL DEFAULT now()
);

-- The overview aggregate reads WHERE kind IN (...) AND occurred_at > ack_at ORDER BY occurred_at
-- DESC LIMIT 1, so a descending index on occurred_at is the natural covering shape. Kind is not on
-- the index because both fallback kinds share the same badge; a future kind-scoped read would add a
-- separate partial index.
CREATE INDEX billing_incidents_occurred_idx ON billing_incidents (occurred_at DESC);

CREATE TABLE operational_state (
    id                     smallint    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    policy_fallback_ack_at timestamptz
);

INSERT INTO operational_state (id) VALUES (1) ON CONFLICT DO NOTHING;
