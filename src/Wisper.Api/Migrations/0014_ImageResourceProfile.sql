-- 0014_ImageResourceProfile.sql — an offer now SELLS A SIZE (docs/DATA_MODEL.md §4, task #569).
--
-- Offers were priced flat per image while consumers freely picked cpu/memory/gpu at lease time, so the
-- price never reflected the provisioned resources. An offer now sells a FIXED resource profile alongside its
-- image + price, like an instance type: host_images gains cpus/memory_mb (the exact per-lease provisioning),
-- and the former max_gpus GPU *ceiling* becomes an EXACT gpus count. Both new columns are nullable — a NULL
-- cpus/memory_mb means the host's own per-lease policy default applies downstream (lease-side consumption is
-- task #570). The rename preserves the stored values and the NOT NULL DEFAULT 0 the column already carried
-- (0 = no GPU); its meaning shifts from "consumer-chosen ceiling" to "exact devices provisioned".
ALTER TABLE host_images
    ADD COLUMN cpus      int,
    ADD COLUMN memory_mb int;

ALTER TABLE host_images
    RENAME COLUMN max_gpus TO gpus;
