using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Task #210: <c>0018_BillingIncidents</c> must be embedded, discovered in order after the earlier
/// migrations, and lay out the two tables the durable policy-fallback signal needs -- an append-only
/// <c>billing_incidents</c> journal and a single-row <c>operational_state</c> for the ack watermark.
/// Grunt has no Postgres, so this asserts the migration <b>content</b> the DbUp runner will apply
/// rather than a live schema; the runtime behaviour rides on
/// <see cref="InMemoryPolicyFallbackStoreTests"/> instead.
/// </summary>
public class BillingIncidentsMigrationTests
{
    private const string Script = "0018_BillingIncidents.sql";

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
    public void The_migration_is_discovered_in_order_after_the_seed_migration()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0017_DefaultPlatformPolicy.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith(Script, StringComparison.Ordinal));

        // DbUp applies migrations in ordinal-string order, so the file discovery order must be sorted.
        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Billing_incidents_table_carries_the_four_fields_the_admin_overview_reads()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE billing_incidents", sql, StringComparison.Ordinal);
        Assert.Contains("kind         text        NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("lease_id     uuid", sql, StringComparison.Ordinal);
        Assert.Contains("policy_id    uuid        REFERENCES platform_policy (id)", sql, StringComparison.Ordinal);
        Assert.Contains("occurred_at  timestamptz NOT NULL DEFAULT now()", sql, StringComparison.Ordinal);
        // The CHECK constraint on kind pins the two labels the C# enum knows, so a typo cannot
        // slip a new kind onto the table without a matching migration + code change.
        Assert.Contains("CHECK (kind IN ('policy_stale_fallback', 'policy_missing_at_flush'))",
            sql, StringComparison.Ordinal);
        // Descending index on occurred_at backs the MAX + ORDER BY DESC LIMIT 1 the aggregate uses.
        Assert.Contains("CREATE INDEX billing_incidents_occurred_idx ON billing_incidents (occurred_at DESC)",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_state_is_a_single_row_table_seeded_idempotently()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE operational_state", sql, StringComparison.Ordinal);
        // The single-row constraint: a second INSERT is a PK conflict rather than a silent
        // duplicate, and the seed INSERT below is guarded with ON CONFLICT DO NOTHING so
        // reapplying the migration is a no-op.
        Assert.Contains("id                     smallint    PRIMARY KEY DEFAULT 1 CHECK (id = 1)",
            sql, StringComparison.Ordinal);
        Assert.Contains("policy_fallback_ack_at timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO operational_state (id) VALUES (1) ON CONFLICT DO NOTHING",
            sql, StringComparison.Ordinal);
    }
}
