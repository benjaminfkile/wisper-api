using Microsoft.Extensions.Logging;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Applies a <see cref="HostHeartbeat"/>'s optional <c>capability</c> block (task #61) to the manager's
/// live view of a host: it replaces the reference the local registry serves (so
/// <see cref="RegistryHostCapabilitySource"/> and lease admission see fresh values within one heartbeat)
/// and republishes the projected snapshot to the shared <see cref="IHostCapabilityStore"/> (so non-owner
/// instances reading via <see cref="DistributedHostCapabilitySource"/> see them too), then routes
/// isolation/GPU into the persistence side via <see cref="IHostPresence"/> -- the pre-#61 mid-session
/// refreshes for tasks #417 / #521 are preserved unchanged. An omitted capability is "no update -- keep
/// last known" (the agent deliberately omits it when its local wisp is unreachable), so callers only
/// invoke this for a non-null block. All work is best-effort: any exception is logged and swallowed so
/// a refresh hiccup can never disturb lease reconciliation or the tunnel.
/// </summary>
internal static class HeartbeatCapabilityRefresh
{
    /// <summary>
    /// Refreshes <paramref name="connection"/>'s in-memory capability from a heartbeat-carried block,
    /// publishes the projected snapshot to <paramref name="capabilityStore"/>, and forwards the
    /// isolation/GPU refresh to <paramref name="presence"/>. Extracted from the endpoint's
    /// <c>HeartbeatRouter</c> closure so the behavior is directly unit-testable.
    /// </summary>
    public static async Task ApplyAsync(
        TunnelConnection connection,
        HelloCapability capability,
        IHostCapabilityStore capabilityStore,
        IHostPresence presence,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            // Refresh the live capability so lease admission (contract ceiling / OS / limits) sees fresh
            // values within one heartbeat -- the entire point of #61. Same source of truth
            // RegistryHostCapabilitySource reads, so the fast path picks it up with no extra plumbing.
            connection.Capability = capability;

            // Republish the snapshot to the shared capability store so non-owner instances reading via
            // DistributedHostCapabilitySource see the fresh values too (in single-instance mode this is
            // a cheap in-memory write nothing reads).
            var snapshot = RegistryHostCapabilitySource.BuildSnapshot(capability);
            if (snapshot is not null)
            {
                await capabilityStore.SetAsync(connection.HostId, snapshot, ct);
            }

            await presence.RefreshAdvertisedIsolationAsync(
                connection.HostId, capability.IsolationLevels, capability.DefaultIsolation, ct);

            // A heartbeat that re-advertises its gpu block refreshes the persisted GPU the same way
            // (task #521); a heartbeat with no gpu block leaves it as-is.
            if (capability.Gpu is { } gpu)
            {
                await presence.RefreshAdvertisedGpuAsync(
                    connection.HostId, gpu.DeviceClasses, gpu.Devices.Count, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex, "agent tunnel: refreshing host {HostId} capability from heartbeat failed", connection.HostId);
        }
    }
}
