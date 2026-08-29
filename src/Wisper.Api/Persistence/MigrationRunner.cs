using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;

namespace Wisper.Api.Persistence;

/// <summary>
/// Applies the ordered, embedded SQL migrations (docs/DATA_MODEL.md §1, §15) with DbUp.
/// Scripts live in <c>Migrations/</c> as <c>EmbeddedResource</c>s and run in ascending
/// resource-name order (name them <c>NNNN_description.sql</c>). DbUp journals applied scripts
/// in a <c>schemaversions</c> table, so a rerun only applies what is new -- the operation is
/// idempotent and safe to run on every startup and against each per-env logical DB.
/// </summary>
public static class MigrationRunner
{
    /// <summary>Marks which embedded resources are migration scripts.</summary>
    private const string MigrationsMarker = ".Migrations.";

    private static readonly Assembly ScriptAssembly = typeof(MigrationRunner).Assembly;

    /// <summary>
    /// A discarding <see cref="IUpgradeLog"/> used only for the ensure-database step. DbUp's
    /// default <c>ConsoleUpgradeLog</c> prints "Master ConnectionString =&gt; ..." to stdout at
    /// that step, leaking host/username/database to CloudWatch even with the password masked. The
    /// ensure step throws on failure, so nothing important is lost by dropping its informational
    /// output -- errors still surface as exceptions and abort startup.
    /// </summary>
    public static readonly IUpgradeLog SilentEnsureLog = new NullUpgradeLog();

    /// <summary>
    /// Runs all pending migrations against <paramref name="connectionString"/>. Throws if the
    /// upgrade fails (so a bad migration aborts startup loudly rather than booting on a half-applied
    /// schema). Callers must only invoke this when a database is configured.
    /// </summary>
    public static DatabaseUpgradeResult Run(string connectionString, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Create the target logical database if it does not yet exist (docs/DATA_MODEL.md §15).
        // Route DbUp's own logging into a silent sink so the master connection string never lands
        // in stdout/CloudWatch; emit a single plain success line via the structured logger instead.
        EnsureDatabase.For.PostgresqlDatabase(connectionString, SilentEnsureLog);
        logger.LogInformation("database ensured (connection ok)");

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(ScriptAssembly, IsMigrationScript)
            .WithTransactionPerScript()
            .LogTo(new DbUpLogAdapter(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed on script '{result.ErrorScript?.Name}'.", result.Error);
        }

        return result;
    }

    private sealed class NullUpgradeLog : IUpgradeLog
    {
        public void WriteInformation(string format, params object[] args) { }
        public void WriteWarning(string format, params object[] args) { }
        public void WriteError(string format, params object[] args) { }
    }

    /// <summary>Whether an embedded resource name is one of our ordered migration scripts.</summary>
    public static bool IsMigrationScript(string resourceName) =>
        resourceName.Contains(MigrationsMarker, StringComparison.Ordinal) &&
        resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ordered migration script resource names embedded in the assembly. Lets startup and
    /// tests confirm the runner sees the scripts (and their order) without a live database.
    /// </summary>
    public static IReadOnlyList<string> DiscoverScripts() =>
        ScriptAssembly.GetManifestResourceNames()
            .Where(IsMigrationScript)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
}
