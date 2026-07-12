using DbUp.Engine.Output;

namespace Wisper.Api.Persistence;

/// <summary>
/// Bridges DbUp's <see cref="IUpgradeLog"/> to the app's <see cref="ILogger"/> so migration
/// output rides the same structured JSON log stream as everything else (see Program.cs).
/// DbUp formats with <see cref="string.Format(string,object?[])"/> placeholders (<c>{0}</c>),
/// so the message is pre-rendered and logged as a single field to avoid clashing with
/// <see cref="ILogger"/>'s named message templates.
/// </summary>
internal sealed class DbUpLogAdapter : IUpgradeLog
{
    private readonly ILogger _logger;

    public DbUpLogAdapter(ILogger logger) => _logger = logger;

    public void WriteInformation(string format, params object[] args) =>
        _logger.LogInformation("dbup: {Message}", Render(format, args));

    public void WriteWarning(string format, params object[] args) =>
        _logger.LogWarning("dbup: {Message}", Render(format, args));

    public void WriteError(string format, params object[] args) =>
        _logger.LogError("dbup: {Message}", Render(format, args));

    private static string Render(string format, object[] args)
    {
        if (args is null || args.Length == 0)
        {
            return format;
        }

        try
        {
            return string.Format(format, args);
        }
        catch (FormatException)
        {
            // Defensive: never let a logging format mishap abort a migration.
            return format;
        }
    }
}
