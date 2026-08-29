using System.IO;
using System.Reflection;
using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The identity + catalog migrations must be embedded, discovered in order, and define the enum types
/// and tables the schema calls for (docs/DATA_MODEL.md §2-§4). Grunt has no Postgres, so this asserts
/// the migration <b>content</b> the DbUp runner will apply rather than a live schema.
/// </summary>
public class CatalogMigrationsTests
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
    public void The_new_catalog_migrations_are_discovered_in_order()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.Contains(scripts, s => s.EndsWith("0002_Enums.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith("0003_Users.sql", StringComparison.Ordinal));
        Assert.Contains(scripts, s => s.EndsWith("0004_HostsAndImages.sql", StringComparison.Ordinal));

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Fact]
    public void Enum_migration_creates_the_identity_and_catalog_enums()
    {
        var sql = ReadScript("0002_Enums.sql");

        Assert.Contains("CREATE TYPE user_status", sql, StringComparison.Ordinal);
        Assert.Contains("'active', 'suspended', 'deleted'", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE host_status", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE connect_status", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE network_mode", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Users_migration_defines_the_table_with_its_unique_columns()
    {
        var sql = ReadScript("0003_Users.sql");

        Assert.Contains("CREATE TABLE users", sql, StringComparison.Ordinal);
        Assert.Contains("cognito_sub        text          NOT NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("email              text          NOT NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("stripe_customer_id text          UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("connect_account_id text          UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("status             user_status", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Hosts_migration_defines_tables_indexes_and_constraints()
    {
        var sql = ReadScript("0004_HostsAndImages.sql");

        Assert.Contains("CREATE TABLE hosts", sql, StringComparison.Ordinal);
        Assert.Contains("owner_user_id      uuid        NOT NULL REFERENCES users (id)", sql, StringComparison.Ordinal);
        Assert.Contains("agent_token_hash   text        NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX hosts_owner_idx", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE status = 'online'", sql, StringComparison.Ordinal);

        Assert.Contains("CREATE TABLE host_images", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES hosts (id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("networks            network_mode[] NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (price_cents_per_min >= 0)", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE (host_id, image_ref)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_isolation_migration_adds_the_capability_columns_with_shared_defaults()
    {
        var sql = ReadScript("0010_HostIsolation.sql");

        Assert.Contains("ALTER TABLE hosts", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN isolation_levels  text[] NOT NULL DEFAULT ARRAY['shared']::text[]",
            sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN default_isolation text   NOT NULL DEFAULT 'shared'",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_gpu_migration_adds_the_capability_columns_with_empty_defaults()
    {
        var sql = ReadScript("0012_HostGpu.sql");

        Assert.Contains("ALTER TABLE hosts", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN gpu_classes text[] NOT NULL DEFAULT '{}'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN gpu_count   int    NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_resource_profile_migration_adds_the_sized_columns_and_renames_gpus()
    {
        // The sized-offer profile (task #569): nullable cpus/memory_mb are added, and the former max_gpus GPU
        // ceiling is renamed to gpus (its meaning shifts to the exact provisioned device count).
        var sql = ReadScript("0014_ImageResourceProfile.sql");

        Assert.Contains("ALTER TABLE host_images", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN cpus      int", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN memory_mb int", sql, StringComparison.Ordinal);
        Assert.Contains("RENAME COLUMN max_gpus TO gpus", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Lease_provisioned_profile_migration_swaps_the_free_form_knobs_for_the_sized_snapshot()
    {
        // A lease now stamps the offer's sized profile (task #570): the free-form cpus/memory_mb/pids knobs are
        // dropped and the sized-profile snapshot (nullable int cpus/memory_mb) is added; leases.gpus predates it.
        var sql = ReadScript("0015_LeaseProvisionedProfile.sql");

        Assert.Contains("ALTER TABLE leases", sql, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN cpus", sql, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN memory_mb", sql, StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN pids", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN cpus      int", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN memory_mb int", sql, StringComparison.Ordinal);
    }
}
