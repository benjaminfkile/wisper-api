-- 0001_Init.sql -- first migration (docs/DATA_MODEL.md §1, §15).
--
-- Deliberately empty: it exists to prove the DbUp runner discovers, orders, applies, and journals
-- embedded migrations end-to-end. The real schema (enum types, users, hosts, host_images, leases,
-- the double-entry ledger, …) lands in the following ordered migrations (tasks P2.2-P2.5).
--
-- DbUp records this script in `schemaversions` once applied, so it never runs twice.

SELECT 1;
