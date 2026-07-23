namespace Wisper.Api.Domain;

/// <summary>
/// The isolation capability a host advertises — the sandbox levels its wisp can place a lease in and the
/// level it defaults to (docs/TUNNEL.md §5, task #417). Levels are opaque strings mirrored from the agent's
/// capability report (wire fields <c>isolation_levels</c> / <c>default_isolation</c>); the manager stores and
/// surfaces them without interpreting each one, so a newer agent can advertise additional levels without a
/// schema change. A host that reports nothing (an older agent) is treated as offering only
/// <see cref="Shared"/> with <see cref="Shared"/> as its default.
/// </summary>
public static class HostIsolation
{
    /// <summary>The always-available baseline isolation level — the fallback for a host that reports none.</summary>
    public const string Shared = "shared";

    /// <summary>The single-level fallback list (<c>["shared"]</c>) for a host that advertised nothing.</summary>
    public static readonly IReadOnlyList<string> SharedOnly = new[] { Shared };

    /// <summary>
    /// Normalizes an advertised capability to what is persisted/surfaced: trims each level, drops blanks,
    /// de-dupes ordinally while preserving order, and falls back to <c>["shared"]</c> when the result is
    /// empty. The default is trimmed and forced to be one of the levels — an omitted/blank/unknown default
    /// resolves to <see cref="Shared"/> when offered, else the first advertised level (task #417).
    /// </summary>
    public static (IReadOnlyList<string> Levels, string Default) Normalize(
        IReadOnlyList<string>? levels, string? defaultLevel)
    {
        var normalized = new List<string>();
        if (levels is not null)
        {
            foreach (var raw in levels)
            {
                var level = raw?.Trim();
                if (string.IsNullOrEmpty(level) || normalized.Contains(level, StringComparer.Ordinal))
                {
                    continue;
                }

                normalized.Add(level);
            }
        }

        if (normalized.Count == 0)
        {
            normalized.Add(Shared);
        }

        var def = defaultLevel?.Trim();
        if (string.IsNullOrEmpty(def) || !normalized.Contains(def, StringComparer.Ordinal))
        {
            def = normalized.Contains(Shared, StringComparer.Ordinal) ? Shared : normalized[0];
        }

        return (normalized, def);
    }
}
