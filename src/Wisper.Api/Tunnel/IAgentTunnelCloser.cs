namespace Wisper.Api.Tunnel;

/// <summary>
/// Force-closes a host's live agent tunnel with a specific close code (docs/TUNNEL.md §3). The host API
/// uses it on agent-token rotation to revoke the old token's session: the tunnel is closed <c>4402</c>
/// (token revoked → the agent must not auto-reconnect until re-provisioned, docs/API.md §6, docs/TUNNEL.md
/// §13). Abstracted so it is testable without a live socket; the production impl drives the tunnel registry.
/// </summary>
public interface IAgentTunnelCloser
{
    /// <summary>
    /// Closes the live tunnel for <paramref name="hostId"/> with <paramref name="closeCode"/> and
    /// <paramref name="reason"/>. Returns <c>true</c> if a live tunnel was found and closed; <c>false</c>
    /// if the host had none (already offline -- nothing to do).
    /// </summary>
    Task<bool> CloseAsync(Guid hostId, int closeCode, string reason, CancellationToken ct = default);
}
