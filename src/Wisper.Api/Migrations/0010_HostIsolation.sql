-- 0010_HostIsolation.sql — per-host advertised isolation capability (docs/TUNNEL.md §5, task #417).
--
-- A host advertises the sandbox isolation levels its wisp can place a lease in, and the level it
-- defaults to, on its tunnel `hello` (wire fields isolation_levels / default_isolation). The manager
-- stores them opaquely (levels are free-form strings, like host_images.networks but not an enum) and
-- surfaces them in the consumer catalog so a lease can be filtered by level. A host that reports nothing
-- (an older agent) is treated as offering only 'shared' with default 'shared' — the column defaults here
-- backfill existing rows and cover any row the tunnel never refreshes.

ALTER TABLE hosts
    ADD COLUMN isolation_levels  text[] NOT NULL DEFAULT ARRAY['shared']::text[],
    ADD COLUMN default_isolation text   NOT NULL DEFAULT 'shared';
