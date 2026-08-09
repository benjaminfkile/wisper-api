-- 0015_LeaseProvisionedProfile.sql — a lease stamps the offer's SIZE (docs/DATA_MODEL.md §5, task #570).
--
-- Task #569 made an offer sell a fixed resource profile (host_images.cpus/memory_mb/gpus). A lease now
-- provisions EXACTLY that profile — the consumer no longer picks resources at lease time. So the leases
-- table drops the old free-form snapshot knobs (cpus numeric(6,3), memory_mb int, pids int — the ceilings
-- consumers used to choose under) and gains the sized-profile snapshot instead: cpus/memory_mb as nullable
-- ints stamped from the offer at create (NULL = the offer left it to the host's own per-lease policy default,
-- so nothing is pinned downstream). leases.gpus already exists (migration 0013) and keeps its meaning as the
-- exact whole-device count booked. pids is gone entirely (a free-form knob, never part of the sized profile).
ALTER TABLE leases
    DROP COLUMN cpus,
    DROP COLUMN memory_mb,
    DROP COLUMN pids;

ALTER TABLE leases
    ADD COLUMN cpus      int,
    ADD COLUMN memory_mb int;
