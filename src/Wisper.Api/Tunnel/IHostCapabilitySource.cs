using Wisper.Api.Domain;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Resolves a host's <b>live, advertised wisp capability</b> -- the <c>hello.capability</c> from its current
/// agent tunnel (docs/TUNNEL.md §5) -- for the host API to validate a priced allow-list against
/// (docs/API.md §6). Presence is authoritative from the live tunnel registry: a host with no live tunnel
/// has no capability to validate against, so the source returns <c>null</c> and the caller reports the host
/// offline. A Redis-backed implementation can replace the in-process one with the multi-instance backplane.
/// </summary>
public interface IHostCapabilitySource
{
    /// <summary>The live advertised capability for <paramref name="hostId"/>, or <c>null</c> if it has no live tunnel.</summary>
    HostCapabilitySnapshot? GetCapability(Guid hostId);
}

/// <summary>
/// A flattened snapshot of a host's advertised wisp capability (docs/TUNNEL.md §5): the allow-listed image
/// refs plus the host-wide per-lease ceilings a priced image may not exceed. <see cref="MaxCpus"/>/
/// <see cref="MaxMemoryMb"/> are the host's advertised per-lease caps (wisp <c>limits.max_cpus</c>/
/// <c>max_memory_mb</c>), which task #578 also uses to resolve the effective size a NULL-profile offer sells
/// or a lease booked (see <see cref="Wisper.Api.Domain.ResolvedResources"/>) -- refreshed on every reconnect
/// since the snapshot is read live off the current tunnel connection. Network labels that do not map
/// to a known <see cref="NetworkMode"/> are dropped (forward-compatible with additive modes). <see cref="Os"/>
/// carries the host's advertised container OS (<c>"linux"</c> | <c>"windows"</c>), or <c>null</c> when an
/// older agent's hello did not advertise it -- surfacing only, it gates no routing. <see cref="MaxGpus"/> and
/// <see cref="GpuClasses"/> carry the live advertised GPU facts a later task validates a GPU lease against
/// (task #521); a host that advertises no GPU has <c>0</c> / an empty list.
/// </summary>
public sealed record HostCapabilitySnapshot(
    IReadOnlyList<string> Images,
    IReadOnlyList<NetworkMode> Networks,
    long MaxTtlSeconds,
    double MaxCpus,
    long MaxMemoryMb,
    long MaxPids,
    string? Os = null,
    int MaxGpus = 0,
    IReadOnlyList<string>? GpuClasses = null,
    int MaxContracts = 0)
{
    /// <summary>
    /// The distinct GPU hardware classes this host advertises live, in advertised order -- opaque strings
    /// (task #521). Never null: an empty list means the host advertises no GPU.
    /// </summary>
    public IReadOnlyList<string> GpuClasses { get; init; } = GpuClasses ?? HostGpu.NoClasses;

    /// <summary>
    /// Whether the host advertises a positive concurrent-contract ceiling (task #571). When false the host is
    /// treated as unlimited and no per-host admission decision is made -- the pre-#571 behavior.
    /// </summary>
    public bool HasContractLimit => MaxContracts > 0;

    /// <summary>Whether <paramref name="imageRef"/> is in the host's advertised allow-list (ordinal match).</summary>
    public bool AllowsImage(string imageRef) => Images.Contains(imageRef, StringComparer.Ordinal);
}
