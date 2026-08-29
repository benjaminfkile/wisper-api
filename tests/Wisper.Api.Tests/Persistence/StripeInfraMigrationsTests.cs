using System.IO;
using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The §9-§12 infra migration (stripe_events, payouts, idempotency_keys, platform_policy, audit_log) must
/// be embedded, discovered in order after the ledger, and define each table with the constraints the docs
/// call for (docs/DATA_MODEL.md §9-§12): the two enums, the webhook dedupe PK, the unique Stripe transfer
/// id, the idempotency status lock + TTL, the versioned policy with its fee-bps range, and the append-only
/// audit log. Grunt has no Postgres, so this asserts the migration <b>content</b> the DbUp runner will
/// apply rather than a live schema.
/// </summary>
public class StripeInfraMigrationsTests
{
    private const string Script = "0007_StripeIdempotencyPolicyAudit.sql";

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
    public void The_infra_migration_is_discovered_in_order_after_the_ledger()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0006_Ledger.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith(Script, StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Migration_creates_the_payout_and_stripe_event_enums()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TYPE payout_status", sql, StringComparison.Ordinal);
        Assert.Contains("'pending', 'in_transit', 'paid', 'failed', 'canceled'", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE stripe_event_status", sql, StringComparison.Ordinal);
        Assert.Contains("'received', 'processed', 'ignored', 'failed'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Stripe_events_is_the_dedupe_store_keyed_by_event_id()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE stripe_events", sql, StringComparison.Ordinal);
        Assert.Contains("id           text                PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("payload      jsonb               NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("status       stripe_event_status NOT NULL DEFAULT 'received'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Payouts_has_the_amount_check_and_unique_transfer_id()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE payouts", sql, StringComparison.Ordinal);
        Assert.Contains("host_user_id       uuid          NOT NULL REFERENCES users (id)", sql, StringComparison.Ordinal);
        Assert.Contains("amount_cents       bigint        NOT NULL CHECK (amount_cents > 0)", sql, StringComparison.Ordinal);
        Assert.Contains("stripe_transfer_id text          UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("payout_txn_id      uuid          REFERENCES ledger_transactions (id)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Idempotency_keys_has_the_status_lock_hash_and_ttl()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE idempotency_keys", sql, StringComparison.Ordinal);
        Assert.Contains("key             text        PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("request_hash    text        NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("response_body   jsonb", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (status IN ('in_progress', 'done'))", sql, StringComparison.Ordinal);
        Assert.Contains("expires_at      timestamptz NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_policy_is_versioned_with_the_fee_bps_range()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE platform_policy", sql, StringComparison.Ordinal);
        Assert.Contains("fee_bps                        int         NOT NULL CHECK (fee_bps BETWEEN 0 AND 10000)", sql, StringComparison.Ordinal);
        Assert.Contains("effective_from                 timestamptz NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("platform_policy_effective_idx", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_log_is_append_only()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE audit_log", sql, StringComparison.Ordinal);
        Assert.Contains("id            bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("meta          jsonb", sql, StringComparison.Ordinal);
        // Reuses the ledger's immutability guard (created in 0006) to forbid UPDATE/DELETE.
        Assert.Contains("CREATE TRIGGER audit_log_immutable", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON audit_log", sql, StringComparison.Ordinal);
        Assert.Contains("EXECUTE FUNCTION ledger_forbid_mutation()", sql, StringComparison.Ordinal);
    }
}
