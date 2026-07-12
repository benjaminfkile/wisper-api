using System.IO;
using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The ledger migration must be embedded, discovered in order after the leases migration, and define the
/// double-entry schema with its invariants enforced <b>in the database</b> (docs/DATA_MODEL.md §7): the
/// three tables, the deferred balanced-transaction constraint trigger, the immutability triggers, the
/// maintained-balance trigger and its non-negative guard, plus the FKs closing the circular
/// lease ↔ ledger dependency 0005 deferred. Grunt has no Postgres, so this asserts the migration
/// <b>content</b> the DbUp runner will apply rather than a live schema.
/// </summary>
public class LedgerMigrationsTests
{
    private static string ReadScript(string suffix)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No embedded migration ending in '{suffix}'.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_ledger_migration_is_discovered_in_order_after_the_leases()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0006_Ledger.sql", StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Ledger_migration_creates_the_ledger_enums()
    {
        var sql = ReadScript("0006_Ledger.sql");

        Assert.Contains("CREATE TYPE ledger_account_kind", sql, StringComparison.Ordinal);
        Assert.Contains(
            "'user_wallet', 'host_earnings', 'lease_holds', 'platform_revenue', 'platform_cash', 'stripe_fees'",
            sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE ledger_txn_kind", sql, StringComparison.Ordinal);
        Assert.Contains(
            "'topup', 'lease_hold', 'lease_charge', 'hold_release', 'payout', 'refund', 'chargeback', 'adjustment'",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_tables_have_their_columns_constraints_and_indexes()
    {
        var sql = ReadScript("0006_Ledger.sql");

        // ledger_accounts — maintained balance + one-per-user uniqueness.
        Assert.Contains("CREATE TABLE ledger_accounts", sql, StringComparison.Ordinal);
        Assert.Contains("kind          ledger_account_kind NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id uuid                REFERENCES users (id)", sql, StringComparison.Ordinal);
        Assert.Contains("balance_cents bigint              NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (kind, owner_user_id)", sql, StringComparison.Ordinal);

        // ledger_transactions — idempotency dedupe key.
        Assert.Contains("CREATE TABLE ledger_transactions", sql, StringComparison.Ordinal);
        Assert.Contains("kind            ledger_txn_kind NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("idempotency_key text            UNIQUE", sql, StringComparison.Ordinal);

        // ledger_entries — exactly-one-side + non-negative amounts + the account/txn/lease indexes.
        Assert.Contains("CREATE TABLE ledger_entries", sql, StringComparison.Ordinal);
        Assert.Contains("debit_cents    bigint      NOT NULL DEFAULT 0 CHECK (debit_cents  >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("credit_cents   bigint      NOT NULL DEFAULT 0 CHECK (credit_cents >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK ((debit_cents = 0) <> (credit_cents = 0))", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ledger_entries_account_idx ON ledger_entries (account_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ledger_entries_lease_idx   ON ledger_entries (lease_id) WHERE lease_id IS NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_migration_closes_the_circular_lease_ledger_fks()
    {
        var sql = ReadScript("0006_Ledger.sql");

        Assert.Contains("ALTER TABLE leases", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (hold_txn_id) REFERENCES ledger_transactions (id)", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE lease_usage", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (charge_txn_id) REFERENCES ledger_transactions (id)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_migration_enforces_the_balanced_transaction_invariant_with_a_deferred_trigger()
    {
        var sql = ReadScript("0006_Ledger.sql");

        Assert.Contains("FUNCTION assert_txn_balanced()", sql, StringComparison.Ordinal);
        Assert.Contains("RAISE EXCEPTION 'unbalanced ledger txn", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE CONSTRAINT TRIGGER ledger_txn_balanced", sql, StringComparison.Ordinal);
        Assert.Contains("AFTER INSERT ON ledger_entries", sql, StringComparison.Ordinal);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_migration_makes_entries_and_transactions_immutable()
    {
        var sql = ReadScript("0006_Ledger.sql");

        Assert.Contains("FUNCTION ledger_forbid_mutation()", sql, StringComparison.Ordinal);
        Assert.Contains("is append-only", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER ledger_transactions_immutable", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON ledger_transactions", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER ledger_entries_immutable", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON ledger_entries", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Ledger_migration_maintains_balances_and_guards_the_earmarked_liabilities()
    {
        var sql = ReadScript("0006_Ledger.sql");

        Assert.Contains("FUNCTION ledger_apply_entry()", sql, StringComparison.Ordinal);
        // maintained balance by normal side (§7c)
        Assert.Contains("SET balance_cents = balance_cents + delta", sql, StringComparison.Ordinal);
        Assert.Contains("delta := NEW.credit_cents - NEW.debit_cents", sql, StringComparison.Ordinal);
        Assert.Contains("delta := NEW.debit_cents - NEW.credit_cents", sql, StringComparison.Ordinal);
        // non-negative guard on wallet + holds, with the chargeback exception (§7d, PAYMENTS §7)
        Assert.Contains("would be over-drawn", sql, StringComparison.Ordinal);
        Assert.Contains("has insufficient funds", sql, StringComparison.Ordinal);
        Assert.Contains("txn_kind <> 'chargeback'", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER ledger_entries_apply", sql, StringComparison.Ordinal);
    }
}
