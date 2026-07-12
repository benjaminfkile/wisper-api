using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The DbUp runner must discover the ordered, embedded migration scripts from the API assembly
/// without a live database — proving the wiring end-to-end short of an actual Postgres apply.
/// </summary>
public class MigrationRunnerTests
{
    [Fact]
    public void Discovers_the_first_embedded_migration()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        Assert.NotEmpty(scripts);
        Assert.Contains(scripts, s => s.EndsWith("0001_Init.sql", StringComparison.Ordinal));
    }

    [Fact]
    public void Scripts_are_returned_in_ascending_order()
    {
        var scripts = MigrationRunner.DiscoverScripts();

        var sorted = scripts.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, scripts.ToArray());
    }

    [Theory]
    [InlineData("Wisper.Api.Migrations.0001_Init.sql", true)]
    [InlineData("Wisper.Api.Migrations.0002_Users.SQL", true)]
    [InlineData("Wisper.Api.Tunnel.FrameTypes.cs", false)]
    [InlineData("Wisper.Api.Migrations.readme.txt", false)]
    public void IsMigrationScript_matches_only_embedded_sql_under_migrations(string name, bool expected)
    {
        Assert.Equal(expected, MigrationRunner.IsMigrationScript(name));
    }

    [Fact]
    public void Run_requires_a_connection_string()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        Assert.ThrowsAny<ArgumentException>(() => MigrationRunner.Run("", logger));
    }
}
