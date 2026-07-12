using Wisper.Api.Domain;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Resolves a host's <b>live, advertised wisp capability</b> — the <c>hello.capability</c> from its current
/// agent tunnel (docs/TUNNEL.md §5) — for the host API to validate a priced allow-list against
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
/// refs plus the host-wide per-lease ceilings a priced image may not exceed. Network labels that do not map
/// to a known <see cref="NetworkMode"/> are dropped (forward-compatible with additive modes).
/// </summary>
public sealed record HostCapabilitySnapshot(
    IReadOnlyList<string> Images,
    IReadOnlyList<NetworkMode> Networks,
    long MaxTtlSeconds,
    double MaxCpus,
    long MaxMemoryMb,
    long MaxPids)
{
    /// <summary>Whether <paramref name="imageRef"/> is in the host's advertised allow-list (ordinal match).</summary>
    public bool AllowsImage(string imageRef) => Images.Contains(imageRef, StringComparer.Ordinal);
}
