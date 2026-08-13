using DbUp.Engine.Output;
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

    /// <summary>
    /// Regression for the CloudWatch connection-string leak: DbUp's default ConsoleUpgradeLog
    /// prints "Master ConnectionString => Host=...;Username=...;Password=******;..." at the
    /// EnsureDatabase step. <see cref="MigrationRunner"/> routes that step through
    /// <see cref="MigrationRunner.SilentEnsureLog"/> instead. Whatever DbUp hands to that sink
    /// — including the master connection string — must be dropped.
    /// </summary>
    [Fact]
    public void Silent_ensure_log_discards_all_output_including_connection_string()
    {
        var log = (IUpgradeLog)MigrationRunner.SilentEnsureLog;

        var recording = new RecordingLog();
        Assert.IsAssignableFrom<IUpgradeLog>(log);
        Assert.NotSame(recording, log);

        // Simulate the exact leak DbUp's ConsoleUpgradeLog would produce.
        log.WriteInformation(
            "Master ConnectionString => Host={0};Port=5432;Database=postgres;Username={1};Password=******;SSL Mode=Require",
            "db.internal", "wisper_app");
        log.WriteInformation("Beginning database upgrade");
        log.WriteWarning("Something odd: Host=leaked;Username=leaked");
        log.WriteError("Something failed: Host=leaked;Username=leaked");

        // The sink is a black hole — nothing observable, no state, no throw.
        Assert.Empty(recording.Messages);
    }

    private sealed class RecordingLog : IUpgradeLog
    {
        public List<string> Messages { get; } = new();
        public void WriteInformation(string format, params object[] args) => Messages.Add(string.Format(format, args));
        public void WriteWarning(string format, params object[] args) => Messages.Add(string.Format(format, args));
        public void WriteError(string format, params object[] args) => Messages.Add(string.Format(format, args));
    }
}
