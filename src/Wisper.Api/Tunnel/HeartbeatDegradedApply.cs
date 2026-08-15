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
/// bool) so a healthy agent that stays degraded through many beats does not flood the log. The FIRST
/// heartbeat of every connection also unconditionally settles the shared store (task #65) so a stale
/// entry left by a prior superseded/crashed/race-lost connection cannot strand a returning host out of
/// placement.
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
    /// <see cref="HostHeartbeat.Status"/> value. The first heartbeat of every connection settles the
    /// shared store authoritatively (a write regardless of transition — see below); subsequent
    /// heartbeats are idempotent on unchanged state and write / log only on a transition. Fail-safe:
    /// any exception is logged and swallowed so a degraded-set hiccup can never disturb lease
    /// reconciliation or the tunnel.
    ///
    /// The unconditional first-beat settle (task #65) closes the "stuck degraded" leak: a stale entry
    /// left by a superseded, crashed, or race-lost prior connection would otherwise keep a returning
    /// HEALTHY agent excluded forever, because a fresh <see cref="TunnelConnection"/> starts
    /// <see cref="TunnelConnection.IsDegraded"/> = false and the transition-edge guard would skip the
    /// clear on every steady-state healthy beat. After the settle the applier reverts to steady-state
    /// once-per-transition logging so a healthy host does not flood the log.
    /// </summary>
    public static async Task ApplyAsync(
        TunnelConnection connection,
        HostHeartbeat heartbeat,
        IHostDegradedStore degradedStore,
        ILogger logger,
        CancellationToken ct)
    {
        var reportedDegraded = IsDegradedStatus(heartbeat.Status);
        var isTransition = reportedDegraded != connection.IsDegraded;

        // Healthy steady state on a settled connection is the common case — never touches the store
        // or logs. A degraded heartbeat always writes: (a) transitions log + set, (b) steady-state
        // degraded beats refresh the Redis TTL (task #65) so a live degraded host never flaps healthy
        // from expiration alone. A healthy transition or first-beat settle also writes to clear any
        // stale entry authoritatively.
        if (!reportedDegraded && !isTransition && connection.IsDegradedSettled)
        {
            return;
        }

        try
        {
            if (reportedDegraded)
            {
                // Always write on a degraded beat — the Redis store treats this as SET+EX so the TTL
                // is refreshed on every heartbeat (task #65). In-memory store is idempotent.
                await degradedStore.SetDegradedAsync(connection.HostId, ct);
                connection.IsDegraded = true;
                if (isTransition)
                {
                    logger.LogWarning(
                        "host {HostId} agent reported degraded (local wisp unreachable) — excluding from placement",
                        connection.HostId);
                }
            }
            else
            {
                await degradedStore.ClearDegradedAsync(connection.HostId, ct);
                connection.IsDegraded = false;
                if (isTransition)
                {
                    logger.LogInformation(
                        "host {HostId} agent recovered (local wisp reachable again) — restoring placement",
                        connection.HostId);
                }
            }

            // Only latch the settled flag on a successful store write, so a first-beat failure retries
            // on the next heartbeat rather than sticking in the fast-path with a possibly-stale entry
            // still in the store.
            connection.IsDegradedSettled = true;
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
