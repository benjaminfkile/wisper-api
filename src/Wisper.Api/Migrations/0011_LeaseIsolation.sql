-- 0011_LeaseIsolation.sql -- the isolation dimension end to end (task #418, docs/TUNNEL.md §5).
--
-- A lease is booked at a sandbox isolation level (ordered shared < sandboxed < vm); the level is an
-- immutable snapshot taken at creation, resolved server-side (omitted request -> 'shared') after the
-- min_isolation policy floor and the target host's advertised levels (0010) are enforced. Like the other
-- snapshot columns it is a plain text value, defaulted here so existing rows backfill to 'shared'.
ALTER TABLE leases
    ADD COLUMN isolation text NOT NULL DEFAULT 'shared';

-- The admin-tunable minimum isolation level a lease may be created at (a versioned platform_policy knob,
-- docs/DATA_MODEL.md §11): a floor over the per-host advertised levels using the same ordered ranking.
-- Nullable -- NULL means no floor. Append-only like the rest of platform_policy, so this backfills existing
-- versions to "no floor" and each new version carries its own value.
ALTER TABLE platform_policy
    ADD COLUMN min_isolation text;
