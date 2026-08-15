using Microsoft.Extensions.Logging;
using Wisper.Api.Tunnel.Backplane;
using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Applies a <see cref="HostHeartbeat"/>'s optional <c>status</c> field (task #62) to the shared
/// degraded set: when the agent self-reports <c>"degraded"</c> (its local wisp is unreachable, so
/// every downstream <c>lease.create</c> would fail), the host is added to
/// <see cref="IHostDegradedStore"/> so every placement path — catalog liveness and per-host lease
/// admission — excludes it uniformly, on every instance. A subsequent heartbeat with no
/// <c>status</c> (or any non-<c>degraded</c> value) restores the host to normal placement. Transitions
/// are logged exactly once (guarded by the per-connection <see cref="TunnelConnection.IsDegraded"/>
/// bool) so a healthy agent that stays degraded through many beats does not flood the log.
///
/// Lease state is intentionally untouched: the containers may still be running fine — the agent just
/// cannot reach wisp's API — and lease reconciliation is governed solely by the heartbeat lease
/// set-diff (docs/TUNNEL.md §8).
/// </summary>
internal static class HeartbeatDegradedApply
{
    /// <summary>The wire value the agent sends when its local wisp is unreachable (docs/TUNNEL.md §5).</summary>
    public const string DegradedStatus = "degraded";

    /// <summary>
    /// Reconciles <paramref name="connection"/>'s degraded state against this heartbeat's
    /// <see cref="HostHeartbeat.Status"/> value. Idempotent on unchanged state — writes to the shared
    /// store (and logs) only on a transition. Fail-safe: any exception is logged and swallowed so a
    /// degraded-set hiccup can never disturb lease reconciliation or the tunnel.
    /// </summary>
    public static async Task ApplyAsync(
        TunnelConnection connection,
        HostHeartbeat heartbeat,
        IHostDegradedStore degradedStore,
        ILogger logger,
        CancellationToken ct)
    {
        var reportedDegraded = IsDegradedStatus(heartbeat.Status);
        if (reportedDegraded == connection.IsDegraded)
        {
            return; // steady state — the common case — never touches the shared store
        }

        try
        {
            if (reportedDegraded)
            {
                await degradedStore.SetDegradedAsync(connection.HostId, ct);
                connection.IsDegraded = true;
                logger.LogWarning(
                    "host {HostId} agent reported degraded (local wisp unreachable) — excluding from placement",
                    connection.HostId);
            }
            else
            {
                await degradedStore.ClearDegradedAsync(connection.HostId, ct);
                connection.IsDegraded = false;
                logger.LogInformation(
                    "host {HostId} agent recovered (local wisp reachable again) — restoring placement",
                    connection.HostId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "agent tunnel: applying degraded status for host {HostId} failed",
                connection.HostId);
        }
    }

    /// <summary>
    /// A status value counts as degraded only when it is exactly <c>"degraded"</c> (case-insensitive).
    /// Any other value — including <c>null</c>, empty, or an unrecognised label — normalises to
    /// "healthy" so an older agent (no <c>status</c> field at all) or a future agent (a value we do
    /// not yet know) never accidentally strands a host out of placement.
    /// </summary>
    public static bool IsDegradedStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && string.Equals(status.Trim(), DegradedStatus, StringComparison.OrdinalIgnoreCase);
}
