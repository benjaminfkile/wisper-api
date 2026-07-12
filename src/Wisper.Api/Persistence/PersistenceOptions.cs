namespace Wisper.Api.Persistence;

/// <summary>
/// Operational parameters for the persistence layer, bound from configuration
/// (section <see cref="SectionName"/>). The connection string itself lives under
/// <c>ConnectionStrings:Wisper</c> (env <c>ConnectionStrings__Wisper</c>), not here.
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Persistence";

    /// <summary>The name of the connection string entry under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "Wisper";

    /// <summary>
    /// When <c>true</c> (the default) and a database is configured, the DbUp migration runner
    /// applies any pending embedded scripts at startup. Idempotent — already-applied scripts are
    /// skipped. Has no effect when no connection string is set (the app still boots for the tunnel).
    /// </summary>
    public bool RunMigrationsAtStartup { get; set; } = true;
}
