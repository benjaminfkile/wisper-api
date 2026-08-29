-- 0009_ApiKeys.sql -- consumer API keys (docs/DATA_MODEL.md §3, docs/API.md §2).
--
-- A long-lived machine bearer for the authenticated /v1 surface (first client: the orchestrator app),
-- so a machine can drive the API with one credential instead of the Cognito JWT flow. The key value is
-- shown to the user exactly once at mint; only its SHA-256 hash and a short non-secret display prefix
-- (e.g. `wck_live_ab12`) are stored, mirroring the host agent token (§4). Because a key's roles cannot
-- come from Cognito groups, the granted scopes travel on the row -- constrained to the role labels. A key
-- is revocable (revoked_at) and carries a best-effort last_used_at stamp. Indexes: keys by owner, plus
-- the UNIQUE token_hash which backs the O(1) hashed lookup the auth gate uses.

CREATE TABLE api_keys (
    id           uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id      uuid        NOT NULL REFERENCES users (id),
    name         text        NOT NULL,
    token_hash   text        NOT NULL UNIQUE,
    token_prefix text        NOT NULL,
    scopes       text[]      NOT NULL DEFAULT '{}'
                             CHECK (scopes <@ ARRAY['consumer', 'host', 'admin']::text[]),
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz,
    revoked_at   timestamptz
);

CREATE INDEX api_keys_user_idx ON api_keys (user_id);
