-- 0013_ImageAndLeaseGpu.sql — GPU offer ceiling + lease snapshot (docs/DATA_MODEL.md §4, §5, task #522).
--
-- GPU access is priced into the OFFER, not a separate rate table (settled model). A priced allow-list entry
-- may offer whole exclusive GPU devices up to a ceiling: host_images.max_gpus is that offered ceiling (0 = no
-- GPU access on this offer), validated live against the host's advertised GPU capability (0012) exactly like
-- the max_cpus/max_memory_mb/max_pids ceilings. A lease then snapshots leases.gpus — the immutable count of
-- whole devices it booked — mirroring the other resource snapshot columns. wisp enforces the real isolation/
-- allocation; this service enforces the ceiling and passes the count down the tunnel. Both default here so
-- existing rows backfill to 0 (no GPU).
ALTER TABLE host_images
    ADD COLUMN max_gpus int NOT NULL DEFAULT 0;

ALTER TABLE leases
    ADD COLUMN gpus int NOT NULL DEFAULT 0;
