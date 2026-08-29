using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Task #184: the default-platform-policy migration must be embedded, discovered in order after the
/// fraud-guard migration, and insert a conservative seed row on an empty <c>platform_policy</c> table so
/// a fresh Postgres deployment never lacks the fee_bps a paid metering flush needs. Grunt has no Postgres,
/// so this asserts the migration <b>content</b> the DbUp runner will apply rather than a live schema.
/// </summary>
public class DefaultPlatformPolicyMigrationTests
{
    private const string Script = "0017_DefaultPlatformPolicy.sql";

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
    public void The_seed_migration_is_discovered_in_order_after_the_prior_migrations()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0008_FraudPolicy.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith(Script, StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Migration_inserts_a_conservative_default_row_only_when_the_table_is_empty()
    {
        var sql = ReadScript(Script);

        // The insert is guarded so it never overrides an admin-published policy: it fires only when the
        // table is still empty, keeping the migration idempotent and safe to reapply.
        Assert.Contains("INSERT INTO platform_policy", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM platform_policy)", sql, StringComparison.Ordinal);

        // The seed is the "no restriction" shape: no platform cut and no minimum top-up until an admin
        // publishes a real version. Every optional cap column is omitted (defaults to NULL = "no limit").
        Assert.Contains("SELECT 0, 0, now(), NULL", sql, StringComparison.Ordinal);
    }
}
