namespace Wisper.Api.Domain;

/// <summary>The provenance of a resolved effective resource profile (task #578, wire field <c>resources_source</c>).</summary>
public static class ResourceSources
{
    /// <summary>The value came from the offer/lease's own sized profile (highest precedence).</summary>
    public const string Offer = "offer";

    /// <summary>The value fell back to the host's advertised per-lease cap (the wisp <c>limits</c> block).</summary>
    public const string HostCap = "host_cap";

    /// <summary>Neither an offer value nor a host cap is available -- the size is genuinely unknown.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The <b>effective</b> resource profile an offer sells or a lease booked (task #578): never surface or record
/// an unknown size when the information exists server-side. It resolves per dimension with a fixed precedence --
/// the offer/lease's own value, else the host's advertised per-lease cap (the wisp <c>limits.max_cpus</c> /
/// <c>max_memory_mb</c>), else genuinely unknown (<c>null</c>). <see cref="Source"/> records which tier the
/// profile came from so a consumer surface can annotate honestly. This is a display/bookkeeping resolution:
/// the <c>lease.create</c> frame still omits what the offer left unset so wisp's own defaults keep applying.
/// </summary>
public sealed record ResolvedResources(decimal? Cpus, int? MemoryMb, string Source)
{
    /// <summary>
    /// Resolves the effective profile from an offer/lease's own <paramref name="offerCpus"/>/
    /// <paramref name="offerMemoryMb"/> (each <c>null</c> when unset) over the host's advertised per-lease caps
    /// (<paramref name="hostCapCpus"/> cores, <paramref name="hostCapMemoryMb"/> MB; <c>0</c> or negative means
    /// the host advertises no cap -- e.g. it is offline). Precedence is offer &gt; host cap &gt; null, and
    /// <see cref="Source"/> is the highest tier that contributed any value.
    /// </summary>
    public static ResolvedResources Resolve(
        int? offerCpus, int? offerMemoryMb, double hostCapCpus, long hostCapMemoryMb)
    {
        decimal? capCpus = hostCapCpus > 0 ? (decimal)hostCapCpus : null;
        int? capMemoryMb = hostCapMemoryMb > 0 ? (int)Math.Min(hostCapMemoryMb, int.MaxValue) : null;

        decimal? cpus = offerCpus ?? capCpus;
        int? memoryMb = offerMemoryMb ?? capMemoryMb;

        var source =
            offerCpus is not null || offerMemoryMb is not null ? ResourceSources.Offer
            : cpus is not null || memoryMb is not null ? ResourceSources.HostCap
            : ResourceSources.Unknown;

        return new ResolvedResources(cpus, memoryMb, source);
    }

    /// <summary>
    /// The effective vCPU count as a whole-number <c>int</c> for stamping the integer <c>leases.cpus</c> column
    /// (task #578): an offer value passes through exactly, a fractional host cap rounds to the nearest whole
    /// core (wisp caps are whole cores in practice). <c>null</c> when the size is genuinely unknown.
    /// </summary>
    public int? CpusForStamp => Cpus is { } c ? (int)Math.Round(c, MidpointRounding.AwayFromZero) : null;
}
