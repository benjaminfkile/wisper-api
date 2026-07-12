using Wisper.Api.Tunnel;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// A lightweight <see cref="IHostRegistry"/> double for the catalog unit/integration suite. The real
/// registry only becomes populated via live agent WebSocket connections (Grunt has none), so this
/// double lets a test declare which host ids are "online" directly via <see cref="SetOnline"/>. Only
/// the presence check (<see cref="TryGet"/>) is meaningful; the connection-mutating members throw,
/// since the catalog path never registers a tunnel.
/// </summary>
public sealed class FakeHostRegistry : IHostRegistry
{
    private readonly HashSet<string> _online = new(StringComparer.Ordinal);

    /// <summary>Marks <paramref name="hostId"/> as having a live tunnel (its <see cref="Guid"/> form).</summary>
    public void SetOnline(Guid hostId) => _online.Add(hostId.ToString());

    /// <summary>Drops <paramref name="hostId"/> from the live set.</summary>
    public void SetOffline(Guid hostId) => _online.Remove(hostId.ToString());

    public bool TryGet(string hostId, out TunnelConnection? connection)
    {
        connection = null;
        return _online.Contains(hostId);
    }

    public Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default) =>
        throw new NotSupportedException("The catalog path does not register tunnels.");

    public void Unregister(TunnelConnection connection) =>
        throw new NotSupportedException("The catalog path does not unregister tunnels.");

    public IReadOnlyCollection<TunnelConnection> Online =>
        throw new NotSupportedException("The catalog path does not enumerate live connections.");
}
