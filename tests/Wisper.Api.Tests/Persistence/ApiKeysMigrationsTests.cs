using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The consumer API-keys migration must be embedded, discovered in order after the P6.6 fraud-guard
/// migration, and create the <c>api_keys</c> table with the hash-at-rest columns and scope constraint
/// (docs/DATA_MODEL.md §3). Grunt has no Postgres, so this asserts the migration <b>content</b> the DbUp
/// runner will apply rather than a live schema.
/// </summary>
public class ApiKeysMigrationsTests
{
    private const string Script = "0009_ApiKeys.sql";

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
    public void The_migration_is_embedded_and_discovered_in_order_after_the_fraud_guard_migration()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0008_FraudPolicy.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith(Script, StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Migration_creates_the_api_keys_table_with_hash_at_rest_columns_and_scope_check()
    {
        var sql = ReadScript(Script);

        Assert.Contains("CREATE TABLE api_keys", sql, StringComparison.Ordinal);
        Assert.Contains("token_hash", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("token_prefix", sql, StringComparison.Ordinal);
        Assert.Contains("scopes", sql, StringComparison.Ordinal);
        Assert.Contains("last_used_at", sql, StringComparison.Ordinal);
        Assert.Contains("revoked_at", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES users", sql, StringComparison.Ordinal);
        // Scopes are constrained to the role labels.
        Assert.Contains("CHECK (scopes <@ ARRAY['consumer', 'host', 'admin']", sql, StringComparison.Ordinal);
        // The owner index the listing UX reads.
        Assert.Contains("api_keys_user_idx", sql, StringComparison.Ordinal);
    }
}
