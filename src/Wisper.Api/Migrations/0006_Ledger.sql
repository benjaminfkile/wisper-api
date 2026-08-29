-- 0006_Ledger.sql -- the double-entry ledger: the money source of truth (docs/DATA_MODEL.md §7, §8).
--
-- Every cent that exists in Wisper is a balance *derived from* these three append-only tables. The
-- invariants are enforced in the database itself, not just app code, so no bug above the DB can post
-- unbalanced or duplicate money:
--   (a) every transaction balances (Σ debit = Σ credit) -- a DEFERRED constraint trigger checked at
--       commit, after all of a txn's entries are inserted;
--   (b) entries & transactions are immutable (append-only) -- corrections are reversing transactions;
--   (c) account balances are *maintained*, never authored -- a trigger updates balance_cents by each
--       account's normal side as entries land;
--   (d) the same trigger guards the earmarked liabilities (user_wallet, lease_holds) non-negative, the
--       hard guarantee a consumer can never outspend their wallet (the one documented exception is a
--       `chargeback`, which may drive a wallet negative = a genuine debt, docs/PAYMENTS.md §7).
-- The C# LedgerService mirrors (a)-(d) over an in-memory ledger so the logic is unit-testable without
-- Postgres; these SQL triggers are the defense-in-depth backstop.
--
-- This migration also closes the circular FKs deferred by 0005 (leases.hold_txn_id and
-- lease_usage.charge_txn_id → ledger_transactions), now that ledger_transactions exists.

CREATE TYPE ledger_account_kind AS ENUM ('user_wallet', 'host_earnings', 'lease_holds', 'platform_revenue', 'platform_cash', 'stripe_fees');
CREATE TYPE ledger_txn_kind     AS ENUM ('topup', 'lease_hold', 'lease_charge', 'hold_release', 'payout', 'refund', 'chargeback', 'adjustment');

CREATE TABLE ledger_accounts (
    id            uuid                PRIMARY KEY DEFAULT gen_random_uuid(),
    kind          ledger_account_kind NOT NULL,
    owner_user_id uuid                REFERENCES users (id),   -- NULL for platform singletons
    currency      text                NOT NULL DEFAULT 'usd',
    -- normal side: user_wallet/host_earnings/lease_holds/platform_revenue = credit; platform_cash/stripe_fees = debit
    balance_cents bigint              NOT NULL DEFAULT 0,      -- maintained by trigger; natural positive balance
    created_at    timestamptz         NOT NULL DEFAULT now(),
    -- one wallet & one earnings account per user; platform singletons are unique by kind (owner NULL)
    UNIQUE (kind, owner_user_id)
);

CREATE TABLE ledger_transactions (
    id              uuid            PRIMARY KEY DEFAULT gen_random_uuid(),
    kind            ledger_txn_kind NOT NULL,
    lease_id        uuid            REFERENCES leases (id),   -- when lease-scoped
    external_ref    text,                                    -- stripe pi/tr/ch id, etc.
    idempotency_key text            UNIQUE,                  -- dedupes retries/webhooks
    memo            text,
    created_at      timestamptz     NOT NULL DEFAULT now()
);

CREATE TABLE ledger_entries (
    id             bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    transaction_id uuid        NOT NULL REFERENCES ledger_transactions (id),
    account_id     uuid        NOT NULL REFERENCES ledger_accounts (id),
    debit_cents    bigint      NOT NULL DEFAULT 0 CHECK (debit_cents  >= 0),
    credit_cents   bigint      NOT NULL DEFAULT 0 CHECK (credit_cents >= 0),
    lease_id       uuid        REFERENCES leases (id),       -- for per-lease hold tracking
    created_at     timestamptz NOT NULL DEFAULT now(),
    CHECK ((debit_cents = 0) <> (credit_cents = 0))          -- exactly one side per entry
);

-- The UNIQUE (kind, owner_user_id) above pins one wallet/earnings account per user, but NULLs are
-- distinct in a unique constraint, so it does not stop two platform singletons of the same kind. This
-- partial unique index makes the owner-less singletons (lease_holds/platform_revenue/platform_cash/
-- stripe_fees) genuinely one-per-kind, matching the intent in docs/DATA_MODEL.md §7.
CREATE UNIQUE INDEX ledger_accounts_singleton_idx ON ledger_accounts (kind) WHERE owner_user_id IS NULL;

CREATE INDEX ledger_entries_account_idx ON ledger_entries (account_id, id);
CREATE INDEX ledger_entries_txn_idx     ON ledger_entries (transaction_id);
CREATE INDEX ledger_entries_lease_idx   ON ledger_entries (lease_id) WHERE lease_id IS NOT NULL;

-- Close the circular FKs 0005 deferred (leases/lease_usage ↔ ledger_transactions, docs/DATA_MODEL.md §5, §6).
ALTER TABLE leases
    ADD CONSTRAINT leases_hold_txn_fk
    FOREIGN KEY (hold_txn_id) REFERENCES ledger_transactions (id);
ALTER TABLE lease_usage
    ADD CONSTRAINT lease_usage_charge_txn_fk
    FOREIGN KEY (charge_txn_id) REFERENCES ledger_transactions (id);

-- (a) Every transaction balances. Deferred so all of a txn's entries are inserted first and the sum is
-- checked once at commit (docs/DATA_MODEL.md §7a).
CREATE FUNCTION assert_txn_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    total_debit  bigint;
    total_credit bigint;
BEGIN
    SELECT COALESCE(SUM(debit_cents), 0), COALESCE(SUM(credit_cents), 0)
      INTO total_debit, total_credit
      FROM ledger_entries
     WHERE transaction_id = NEW.transaction_id;

    IF total_debit <> total_credit THEN
        RAISE EXCEPTION 'unbalanced ledger txn %: debit % <> credit %',
            NEW.transaction_id, total_debit, total_credit;
    END IF;

    RETURN NULL;
END;
$$;

CREATE CONSTRAINT TRIGGER ledger_txn_balanced
    AFTER INSERT ON ledger_entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_txn_balanced();

-- (b) Entries & transactions are immutable -- append-only; corrections are reversing transactions
-- (docs/DATA_MODEL.md §7b). Balances live on ledger_accounts and are maintained by (c), so accounts are
-- deliberately not frozen.
CREATE FUNCTION ledger_forbid_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger % is append-only; % is not permitted (corrections are reversing transactions)',
        TG_TABLE_NAME, TG_OP;
END;
$$;

CREATE TRIGGER ledger_transactions_immutable
    BEFORE UPDATE OR DELETE ON ledger_transactions
    FOR EACH ROW EXECUTE FUNCTION ledger_forbid_mutation();

CREATE TRIGGER ledger_entries_immutable
    BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION ledger_forbid_mutation();

-- (c) Balances are maintained, never authored, and (d) the earmarked liabilities stay non-negative
-- (docs/DATA_MODEL.md §7c, §7d). credit-normal: balance += credit − debit; debit-normal: += debit − credit.
CREATE FUNCTION ledger_apply_entry() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    acct_kind   ledger_account_kind;
    txn_kind    ledger_txn_kind;
    delta       bigint;
    new_balance bigint;
BEGIN
    -- Lock the balance row so concurrent writers to the same account serialize (docs/DATA_MODEL.md §14).
    SELECT kind INTO acct_kind FROM ledger_accounts WHERE id = NEW.account_id FOR UPDATE;

    IF acct_kind IN ('user_wallet', 'host_earnings', 'lease_holds', 'platform_revenue') THEN
        delta := NEW.credit_cents - NEW.debit_cents;   -- credit-normal
    ELSE
        delta := NEW.debit_cents - NEW.credit_cents;   -- debit-normal (platform_cash, stripe_fees)
    END IF;

    UPDATE ledger_accounts
       SET balance_cents = balance_cents + delta
     WHERE id = NEW.account_id
    RETURNING balance_cents INTO new_balance;

    IF new_balance < 0 THEN
        IF acct_kind = 'lease_holds' THEN
            RAISE EXCEPTION 'lease_holds account % would be over-drawn: %', NEW.account_id, new_balance;
        ELSIF acct_kind = 'user_wallet' THEN
            -- A chargeback is the one documented case a wallet may go negative (a debt, docs/PAYMENTS.md §7).
            SELECT kind INTO txn_kind FROM ledger_transactions WHERE id = NEW.transaction_id;
            IF txn_kind <> 'chargeback' THEN
                RAISE EXCEPTION 'user_wallet account % has insufficient funds: %', NEW.account_id, new_balance;
            END IF;
        END IF;
    END IF;

    RETURN NULL;
END;
$$;

CREATE TRIGGER ledger_entries_apply
    AFTER INSERT ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION ledger_apply_entry();
