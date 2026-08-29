-- 0016_LeaseSuspendedAt.sql -- durable suspension timestamp for the grace sweep (task #55).
--
-- Before this: the 90s disconnect grace window lived ONLY in process memory
-- (TunnelDisconnectCoordinator._grace: a ConcurrentDictionary with an ad-hoc Task.Delay timer). If
-- wisper-api restarted while any host was inside grace, its leases were stranded in `suspended` in
-- Postgres FOREVER -- wallet hold never released, consumer and host capacity slots consumed forever
-- (both counts see `suspended` as live). Only manual SQL fixed it.
--
-- The multi-instance rule (docs/DESIGN.md §7) requires cross-request state to live in shared storage.
-- We now durably stamp the suspension moment on the lease row so a background sweep -- running on every
-- instance, guarded by conditional state transitions -- can reap leases whose grace has demonstrably
-- expired (suspended_at < now() - grace) even after a restart or scale-in wipes the in-memory timer.
-- Suspend sets suspended_at; resume/revive clears it (docs/TUNNEL.md §8).
ALTER TABLE leases
    ADD COLUMN suspended_at timestamptz;

-- Partial index over just the suspended set -- the sweep's driving query. Cheap because the suspended
-- population is small and short-lived by design.
CREATE INDEX leases_suspended_at_idx ON leases (suspended_at) WHERE status = 'suspended';
