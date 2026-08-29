-- 0002_Enums.sql -- native enum types for the identity + catalog schema (docs/DATA_MODEL.md §2).
--
-- Only the enums the identity/catalog tables reference are created here (users, hosts, host_images);
-- the lease and ledger enums land with their own tables in the following migrations (P2.3-P2.5),
-- keeping each migration self-contained. Native enums are self-documenting and cheap; new values are
-- added later with `ALTER TYPE … ADD VALUE` (append-only), which suits an audited system.

CREATE TYPE user_status    AS ENUM ('active', 'suspended', 'deleted');
CREATE TYPE host_status     AS ENUM ('offline', 'online', 'suspended');
CREATE TYPE connect_status  AS ENUM ('none', 'pending', 'restricted', 'enabled', 'disabled');
CREATE TYPE network_mode    AS ENUM ('none', 'open', 'egress');
