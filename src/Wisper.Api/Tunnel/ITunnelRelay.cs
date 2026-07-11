using Wisper.Api.Tunnel.Messages;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Drives a connected host over its tunnel (docs/TUNNEL.md §5, §11): sends
/// <c>lease.create</c>/<c>exec.run</c>/<c>lease.release</c> request frames and awaits the
/// agent's correlated responses by <c>rid</c> (and <c>leaseId</c> for the unsolicited
/// <c>lease.ready</c>). Wisper owns the id space, so leaseIds and rids are allocated here.
/// <para>
/// SCOPE: lease lifecycle + <b>synchronous</b> exec only. Interactive shell, streamed exec,
/// and credit flow control are a later task.
/// </para>
/// </summary>
public interface ITunnelRelay
{
    /// <summary>
    /// Creates a lease on <paramref name="hostId"/>: sends <c>lease.create</c>, awaits
    /// <c>lease.accepted</c> (by rid), then <c>lease.ready</c> (by the server-issued leaseId).
    /// The <paramref name="spec"/>'s <c>rid</c>/<c>leaseId</c> are overwritten by the relay.
    /// </summary>
    /// <exception cref="Wisper.Api.Infrastructure.ApiException">
    /// <c>host_offline</c> (no live tunnel), <c>upstream_timeout</c> (no response in time), or
    /// <c>lease_failed</c> (the agent reported <c>lease.failed</c>).
    /// </exception>
    Task<LeaseResult> CreateLeaseAsync(string hostId, LeaseCreate spec, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="command"/> synchronously in <paramref name="leaseId"/> on
    /// <paramref name="hostId"/>: sends <c>exec.run</c> and awaits the <c>exec.result</c> (by rid).
    /// </summary>
    /// <exception cref="Wisper.Api.Infrastructure.ApiException"><c>host_offline</c> / <c>upstream_timeout</c>.</exception>
    Task<ExecResult> ExecAsync(string hostId, string leaseId, string command, CancellationToken ct = default);

    /// <summary>
    /// Releases <paramref name="leaseId"/> on <paramref name="hostId"/>: sends
    /// <c>lease.release</c> and awaits the <c>lease.released</c> (by rid).
    /// </summary>
    /// <exception cref="Wisper.Api.Infrastructure.ApiException"><c>host_offline</c> / <c>upstream_timeout</c>.</exception>
    Task ReleaseAsync(string hostId, string leaseId, CancellationToken ct = default);

    /// <summary>
    /// Routes an inbound agent→server response frame (<c>lease.accepted</c>/<c>ready</c>/
    /// <c>failed</c>/<c>released</c>, <c>exec.result</c>, <c>lease.ended</c>) to the pending
    /// awaiter it correlates with. Wired to <see cref="TunnelConnection.ControlFrameRouter"/>.
    /// </summary>
    Task RouteAgentFrameAsync(TunnelConnection connection, string type, ReadOnlyMemory<byte> payload, CancellationToken ct);

    /// <summary>
    /// Fails every request still pending on <paramref name="connection"/> when its tunnel closes,
    /// so awaiters don't hang until their timeout. Called by the endpoint on disconnect.
    /// </summary>
    void OnConnectionClosed(TunnelConnection connection);
}

/// <summary>The outcome of a successful <see cref="ITunnelRelay.CreateLeaseAsync"/> (ready lease).</summary>
public sealed record LeaseResult(string LeaseId, string WispContractId, string Status);
