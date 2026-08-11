namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Persists a host's <see cref="HostCapabilitySnapshot"/> alongside its presence record so the snapshot
/// is readable from any instance, even when the host's tunnel lives on a different one (task #17).
/// Lifecycle is tied to presence: <see cref="SetAsync"/> is called on register and <see cref="ClearAsync"/>
/// on disconnect — a dead tunnel never leaves a readable snapshot.
/// </summary>
public interface IHostCapabilityStore
{
    /// <summary>Stores (or replaces) the capability snapshot for <paramref name="hostId"/>.</summary>
    Task SetAsync(string hostId, HostCapabilitySnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Returns the stored snapshot for <paramref name="hostId"/>, or <c>null</c> if none is present.
    /// Synchronous so callers on the <see cref="IHostCapabilitySource"/> sync interface pay no allocation overhead
    /// on the local fast path; a Redis-backed implementation uses the StackExchange.Redis synchronous API.
    /// </summary>
    HostCapabilitySnapshot? Get(string hostId);

    /// <summary>Removes the snapshot for <paramref name="hostId"/>; idempotent if absent.</summary>
    Task ClearAsync(string hostId, CancellationToken ct = default);
}
