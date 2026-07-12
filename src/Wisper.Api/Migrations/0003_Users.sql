-- 0003_Users.sql — identity & accounts (docs/DATA_MODEL.md §3, §13).
--
-- Roles are additive and authoritative in Cognito groups; this row mirrors identity plus payment
-- linkage. The four unique columns (cognito_sub, email, stripe_customer_id, connect_account_id) each
-- get an implicit unique index — the lookups in §13. Money lives in the ledger, never here.

CREATE TABLE users (
    id                 uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
    cognito_sub        text          NOT NULL UNIQUE,
    email              text          NOT NULL UNIQUE,
    status             user_status   NOT NULL DEFAULT 'active',
    stripe_customer_id text          UNIQUE,
    connect_account_id text          UNIQUE,
    connect_status     connect_status NOT NULL DEFAULT 'none',
    created_at         timestamptz   NOT NULL DEFAULT now(),
    updated_at         timestamptz   NOT NULL DEFAULT now()
);
