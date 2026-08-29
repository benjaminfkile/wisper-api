-- 0017_DefaultPlatformPolicy.sql: seed a conservative default platform_policy row (task #184).
--
-- Until this migration, platform_policy started empty and only became populated when an admin published
-- a version through POST /v1/admin/policy. On a fresh database a paid lease's first metering flush would
-- then throw "No active platform policy is configured.": the tick swallowed the exception and moved on,
-- so the lease kept running unbilled until someone noticed and published a policy. Billing gaps are
-- unacceptable, so we seed a sane default row here so the fee-basis reader always has something to
-- return.
--
-- The seed is idempotent: only inserted when the table is empty, so a database that has already been
-- brought up (with admins publishing their own versions) is unaffected. created_by stays NULL to mark it
-- as a system seed rather than an admin-authored version (§11 already allows NULL there for that reason).
--
-- Defaults are conservative and match the NULL/0 pattern the schema uses for "no restriction":
--   * fee_bps = 0                                : no platform cut until an admin explicitly sets one.
--                                                  The billing paths still split cleanly (host gets 100%).
--   * min_topup_cents = 0                        : the column default; no minimum top-up.
--   * max_concurrent_leases_per_user = NULL      : unlimited concurrent leases per user.
--   * max_ttl_seconds_cap = NULL                 : no global TTL ceiling (per-image cap still applies).
--   * min_isolation = NULL                       : no isolation floor.
--   * first_topup_max_cents = NULL               : no first-top-up hold.
--   * new_account_window_hours = NULL            : new-account fraud guards inactive.
--   * new_account_max_topup_cents_per_day = NULL : new-account fraud guards inactive.
--   * max_spend_cents_per_day = NULL             : no per-user daily spend cap.
--   * effective_from = now()                     : active immediately.
--   * created_by = NULL                          : system seed, per §11.
INSERT INTO platform_policy (fee_bps, min_topup_cents, effective_from, created_by)
SELECT 0, 0, now(), NULL
WHERE NOT EXISTS (SELECT 1 FROM platform_policy);
