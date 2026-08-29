namespace Wisper.Api.Domain;

/// <summary>
/// The GPU capability a host advertises -- the distinct hardware classes its wisp exposes and how many
/// devices it offers (docs/TUNNEL.md §5, task #521). Classes are <b>opaque strings</b> mirrored from the
/// agent's capability report (the wire <c>gpu</c> block), exactly like the isolation levels
/// (<see cref="HostIsolation"/>): the manager stores and surfaces them without interpreting the hardware, so
/// a newer agent can advertise additional classes without a schema change. A host that reports no GPU (an
/// older agent, or a machine with none) advertises nothing, which normalizes to an empty class list.
/// </summary>
public static class HostGpu
{
    /// <summary>The empty class list (<c>[]</c>) for a host that advertises no GPU.</summary>
    public static readonly IReadOnlyList<string> NoClasses = Array.Empty<string>();

    /// <summary>
    /// Normalizes advertised GPU classes to what is persisted/surfaced: trims each class, drops blanks, and
    /// de-dupes ordinally while preserving the advertised order. Returns an empty list for null/blank input
    /// (a host that advertises no GPU). The device <i>count</i> is carried separately (total advertised
    /// devices) and is not derived from this list, which is the <b>distinct</b> classes.
    /// </summary>
    public static IReadOnlyList<string> NormalizeClasses(IReadOnlyList<string>? classes)
    {
        var normalized = new List<string>();
        if (classes is not null)
        {
            foreach (var raw in classes)
            {
                var cls = raw?.Trim();
                if (string.IsNullOrEmpty(cls) || normalized.Contains(cls, StringComparer.Ordinal))
                {
                    continue;
                }

                normalized.Add(cls);
            }
        }

        return normalized;
    }
}
