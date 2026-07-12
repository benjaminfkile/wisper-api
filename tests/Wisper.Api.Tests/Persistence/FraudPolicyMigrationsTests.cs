using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The P6.6 fraud-guard migration must be embedded, discovered in order after the §9–§12 infra, and add the
/// four NULL-able <c>platform_policy</c> columns the billing paths read (docs/PAYMENTS.md §7): the first-top-up
/// hold, the new-account window + top-up velocity, and the per-user daily spend cap. Grunt has no Postgres, so
/// this asserts the migration <b>content</b> the DbUp runner will apply rather than a live schema.
/// </summary>
public class FraudPolicyMigrationsTests
{
    private const string Script = "0008_FraudPolicy.sql";

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
    public void The_migration_is_discovered_in_order_after_the_infra_migration()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0007_StripeIdempotencyPolicyAudit.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith(Script, StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Migration_adds_the_fraud_guard_columns_to_platform_policy()
    {
        var sql = ReadScript(Script);

        Assert.Contains("ALTER TABLE platform_policy", sql, StringComparison.Ordinal);
        Assert.Contains("first_topup_max_cents", sql, StringComparison.Ordinal);
        Assert.Contains("new_account_window_hours", sql, StringComparison.Ordinal);
        Assert.Contains("new_account_max_topup_cents_per_day", sql, StringComparison.Ordinal);
        Assert.Contains("max_spend_cents_per_day", sql, StringComparison.Ordinal);
    }
}
