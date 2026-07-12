namespace Wisper.Api.Tunnel;

/// <summary>
/// <see cref="IAgentTunnelCloser"/> over the live tunnel <see cref="IHostRegistry"/>: looks up the host's
/// current <see cref="TunnelConnection"/> and closes it with the requested code. The connection's own
/// receive loop then tears down and unregisters itself (docs/TUNNEL.md §3). The registry keys on the host
/// id's <c>Guid.ToString()</c> form.
/// </summary>
public sealed class RegistryAgentTunnelCloser : IAgentTunnelCloser
{
    private readonly IHostRegistry _registry;

    public RegistryAgentTunnelCloser(IHostRegistry registry) => _registry = registry;

    public async Task<bool> CloseAsync(Guid hostId, int closeCode, string reason, CancellationToken ct = default)
    {
        if (!_registry.TryGet(hostId.ToString(), out var connection) || connection is null)
        {
            return false;
        }

        await connection.CloseAsync(closeCode, reason, ct);
        return true;
    }
}
