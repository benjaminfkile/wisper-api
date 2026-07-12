-- 0004_HostsAndImages.sql — hosts and their priced allow-list (docs/DATA_MODEL.md §4, §13).
--
-- A host advertises capability/versions via the tunnel `hello`; the agent token is stored hashed
-- only. host_images overlays price on wisp's capability (wisp stays money-blind), unique per
-- (host_id, image_ref), cascading on host delete. Indexes: hosts by owner, a partial index over the
-- online subset for the catalog, and host_images by host; plus a token-hash lookup for agent auth.

CREATE TABLE hosts (
    id                 uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_user_id      uuid        NOT NULL REFERENCES users (id),
    name               text,
    label              text,
    status             host_status NOT NULL DEFAULT 'offline',
    agent_token_hash   text        NOT NULL,
    agent_token_prefix text,
    wisp_version       text,
    agent_version      text,
    max_leases         integer,
    max_streams        integer,
    last_seen_at       timestamptz,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX hosts_owner_idx            ON hosts (owner_user_id);
CREATE INDEX hosts_online_idx           ON hosts (status) WHERE status = 'online';
CREATE INDEX hosts_agent_token_hash_idx ON hosts (agent_token_hash);

CREATE TABLE host_images (
    id                  uuid           PRIMARY KEY DEFAULT gen_random_uuid(),
    host_id             uuid           NOT NULL REFERENCES hosts (id) ON DELETE CASCADE,
    image_ref           text           NOT NULL,
    price_cents_per_min bigint         NOT NULL CHECK (price_cents_per_min >= 0),
    networks            network_mode[] NOT NULL,
    max_ttl_seconds     integer        NOT NULL,
    max_cpus            numeric(6, 3),
    max_memory_mb       integer,
    max_pids            integer,
    enabled             boolean        NOT NULL DEFAULT true,
    created_at          timestamptz    NOT NULL DEFAULT now(),
    updated_at          timestamptz    NOT NULL DEFAULT now(),
    UNIQUE (host_id, image_ref)
);

CREATE INDEX host_images_host_idx ON host_images (host_id);
