-- 0012_HostGpu.sql — per-host advertised GPU capability (docs/TUNNEL.md §5, task #521).
--
-- A host advertises its GPU on the tunnel `hello` capability (wire block `gpu`): the distinct hardware
-- classes it exposes and how many devices it offers. Mirroring the isolation precedent (0010), the manager
-- stores these opaquely — GPU classes are free-form strings this service never interprets, exactly like the
-- isolation levels — and later surfaces/filters on them without understanding the hardware. A host that
-- reports no GPU (an older agent, or a machine with none) advertises nothing; the column defaults here
-- backfill existing rows and cover any row the tunnel never refreshes.
ALTER TABLE hosts
    ADD COLUMN gpu_classes text[] NOT NULL DEFAULT '{}',
    ADD COLUMN gpu_count   int    NOT NULL DEFAULT 0;
