using Wisper.Api.Metering;

namespace Wisper.Api.Tunnel;

/// <summary>
/// <see cref="IMeterLivenessSource"/> over the live tunnel <see cref="IHostRegistry"/>: a host's
/// last-healthy point is the most recent frame its connection received (<see
/// cref="TunnelConnection.LastActivityUtc"/>), which is bracketed by ping/pong liveness (docs/TUNNEL.md
/// §7). A host with no live tunnel returns <c>null</c>, so the meter accrues nothing past a point it
/// cannot vouch for (docs/TUNNEL.md §8). The registry keys on the host id's <c>Guid.ToString()</c> form,
/// matching the id the relay/lease layer uses.
/// </summary>
public sealed class RegistryMeterLivenessSource : IMeterLivenessSource
{
    private readonly IHostRegistry _registry;

    public RegistryMeterLivenessSource(IHostRegistry registry) => _registry = registry;

    public DateTimeOffset? LastHealthyAt(Guid hostId)
    {
        if (_registry.TryGet(hostId.ToString(), out var connection) && connection is not null)
        {
            return new DateTimeOffset(connection.LastActivityUtc, TimeSpan.Zero);
        }

        return null;
    }
}
