namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Cross-instance set of hosts whose agent has self-reported <c>"degraded"</c> in <c>host.heartbeat</c>
/// (task #62). The tunnel is still up on some instance, but the agent cannot reach its local wisp
/// daemon, so every downstream <c>lease.create</c> against the host would fail -- the manager must stop
/// placing new leases on it until a subsequent heartbeat clears the flag. Lifecycle mirrors
/// <see cref="IHostPresenceStore"/>: written by the heartbeat handler on the owning instance and
/// cleared on tunnel disconnect, read by placement paths (catalog liveness, per-host admission) on
/// every instance so a degraded host is uniformly excluded regardless of which instance serves the
/// consumer request (repo rule: cross-request state lives in shared storage -- <c>docs/DESIGN.md §7</c>).
/// </summary>
public interface IHostDegradedStore
{
    /// <summary>Marks <paramref name="hostId"/> degraded. Idempotent if already marked.</summary>
    Task SetDegradedAsync(string hostId, CancellationToken ct = default);

    /// <summary>Clears <paramref name="hostId"/>'s degraded flag. Idempotent if absent.</summary>
    Task ClearDegradedAsync(string hostId, CancellationToken ct = default);

    /// <summary>True when <paramref name="hostId"/> is currently marked degraded.</summary>
    Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default);

    /// <summary>
    /// Snapshot of every currently-degraded host id -- one shared-store round-trip that a placement
    /// pass filters many candidates against without a per-host read, mirroring
    /// <see cref="IHostPresenceStore.SnapshotAsync"/>.
    /// </summary>
    Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default);
}
