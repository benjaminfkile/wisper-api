using System.IO;
using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The lease + metering migration must be embedded, discovered in order after the catalog migrations,
/// and define the <c>lease_status</c>/<c>lease_end_reason</c> enums plus the <c>leases</c> and
/// <c>lease_usage</c> tables with their snapshots, checks, indexes and the
/// <c>(lease_id, period_start)</c> uniqueness (docs/DATA_MODEL.md §5, §6). Grunt has no Postgres, so
/// this asserts the migration <b>content</b> the DbUp runner will apply rather than a live schema.
/// </summary>
public class LeaseMigrationsTests
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
    public void The_lease_migration_is_discovered_in_order_after_the_catalog()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0005_Leases.sql", StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Lease_migration_creates_the_lease_enums()
    {
        var sql = ReadScript("0005_Leases.sql");

        Assert.Contains("CREATE TYPE lease_status", sql, StringComparison.Ordinal);
        Assert.Contains("'pending', 'provisioning', 'active', 'suspended', 'ended', 'failed'", sql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE lease_end_reason", sql, StringComparison.Ordinal);
        Assert.Contains("'released', 'expired', 'host_disconnect', 'container_lost', 'admin', 'payment_failed'",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Leases_table_has_snapshots_timeline_checks_and_indexes()
    {
        var sql = ReadScript("0005_Leases.sql");

        Assert.Contains("CREATE TABLE leases", sql, StringComparison.Ordinal);
        Assert.Contains("consumer_user_id    uuid         NOT NULL REFERENCES users (id)", sql, StringComparison.Ordinal);
        Assert.Contains("host_id             uuid         NOT NULL REFERENCES hosts (id)", sql, StringComparison.Ordinal);
        Assert.Contains("host_image_id       uuid         NOT NULL REFERENCES host_images (id)", sql, StringComparison.Ordinal);

        // immutable snapshots + their checks
        Assert.Contains("network             network_mode NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ttl_seconds         integer      NOT NULL CHECK (ttl_seconds > 0)", sql, StringComparison.Ordinal);
        Assert.Contains("price_cents_per_min bigint       NOT NULL CHECK (price_cents_per_min >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("currency            text         NOT NULL DEFAULT 'usd'", sql, StringComparison.Ordinal);

        // status/end reason + metering timeline
        Assert.Contains("status              lease_status NOT NULL DEFAULT 'pending'", sql, StringComparison.Ordinal);
        Assert.Contains("end_reason          lease_end_reason", sql, StringComparison.Ordinal);
        Assert.Contains("hold_txn_id         uuid", sql, StringComparison.Ordinal);
        Assert.Contains("started_at          timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("last_metered_at     timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("billable_seconds    bigint       NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("ended_at            timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK ((status = 'ended') = (ended_at IS NOT NULL) OR status <> 'ended')", sql, StringComparison.Ordinal);

        Assert.Contains("CREATE INDEX leases_consumer_idx    ON leases (consumer_user_id, created_at DESC)", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE status IN ('active', 'suspended')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Isolation_migration_adds_the_snapshot_column_and_policy_floor()
    {
        var scripts = MigrationRunner.DiscoverScripts();
        Assert.Contains(scripts, s => s.EndsWith("0011_LeaseIsolation.sql", StringComparison.Ordinal));

        var sql = ReadScript("0011_LeaseIsolation.sql");

        // The immutable per-lease snapshot column, defaulted so existing rows backfill to 'shared'.
        Assert.Contains("ALTER TABLE leases", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN isolation text NOT NULL DEFAULT 'shared'", sql, StringComparison.Ordinal);

        // The admin-tunable minimum-isolation floor on platform_policy (nullable -- no floor by default).
        Assert.Contains("ALTER TABLE platform_policy", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN min_isolation text", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Lease_usage_table_has_money_checks_and_period_uniqueness()
    {
        var sql = ReadScript("0005_Leases.sql");

        Assert.Contains("CREATE TABLE lease_usage", sql, StringComparison.Ordinal);
        Assert.Contains("lease_id           uuid        NOT NULL REFERENCES leases (id)", sql, StringComparison.Ordinal);
        Assert.Contains("billable_seconds   integer     NOT NULL CHECK (billable_seconds >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("amount_cents       bigint      NOT NULL CHECK (amount_cents >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("platform_fee_cents bigint      NOT NULL CHECK (platform_fee_cents >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("host_payout_cents  bigint      NOT NULL CHECK (host_payout_cents >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("charge_txn_id      uuid        NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (amount_cents = platform_fee_cents + host_payout_cents)", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (lease_id, period_start)", sql, StringComparison.Ordinal);
    }
}
