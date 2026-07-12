-- 0008_FraudPolicy.sql — the fraud-guard knobs on platform_policy (docs/PAYMENTS.md §7).
--
-- Refunds/disputes/chargebacks (P6.6) are made rare up front by cheap, deterministic policy checks — not a
-- fraud ML system (docs/PAYMENTS.md §7, §13). These additive, versioned columns carry the day-one limits the
-- billing paths enforce at top-up (BillingService) and lease start (WalletLeaseGate):
--   • first_topup_max_cents               — the first-top-up hold: cap a user's first-ever top-up until a
--                                            payment materially clears the dispute window.
--   • new_account_window_hours            — how long (since users.created_at) an account is "new".
--   • new_account_max_topup_cents_per_day — new-account top-up velocity (rolling 24h).
--   • max_spend_cents_per_day             — per-user daily spend cap (measured by up-front lease holds).
-- All are NULL-able and NULL means "no limit", so an existing policy row is unaffected and the guards stay
-- opt-in per version — the same append-only, reproducible model as the rest of platform_policy (§11).

ALTER TABLE platform_policy
    ADD COLUMN first_topup_max_cents               bigint CHECK (first_topup_max_cents >= 0),
    ADD COLUMN new_account_window_hours            int    CHECK (new_account_window_hours >= 0),
    ADD COLUMN new_account_max_topup_cents_per_day bigint CHECK (new_account_max_topup_cents_per_day >= 0),
    ADD COLUMN max_spend_cents_per_day             bigint CHECK (max_spend_cents_per_day >= 0);
