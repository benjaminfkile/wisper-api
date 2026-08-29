using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wisper.Api.Tunnel;

/// <summary>
/// Thread-safe in-memory <see cref="IHostRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by host id. Registered as a
/// singleton. A Redis-backed implementation can replace it without touching callers.
/// </summary>
public sealed class InMemoryHostRegistry : IHostRegistry
{
    private readonly ConcurrentDictionary<string, TunnelConnection> _hosts = new(StringComparer.Ordinal);
    private readonly ILogger<InMemoryHostRegistry> _logger;

    public InMemoryHostRegistry(ILogger<InMemoryHostRegistry> logger) => _logger = logger;

    public async Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default)
    {
        while (true)
        {
            if (_hosts.TryGetValue(connection.HostId, out var existing))
            {
                if (!ReferenceEquals(existing, connection) &&
                    _hosts.TryUpdate(connection.HostId, connection, existing))
                {
                    _logger.LogInformation(
                        "host {HostId}: connection {SessionId} superseded by {NewSessionId}",
                        connection.HostId, existing.SessionId, connection.SessionId);

                    // Close the superseded tunnel normally (docs/TUNNEL.md §16). Its own receive
                    // loop then ends and its Unregister is a no-op (it is no longer registered).
                    await existing.CloseAsync(CloseCodes.Normal, "superseded by new connection", ct);
                    return;
                }
            }
            else if (_hosts.TryAdd(connection.HostId, connection))
            {
                return;
            }

            // Lost a race with a concurrent register/unregister for this host -- retry.
        }
    }

    public void Unregister(TunnelConnection connection)
    {
        // Remove only if this exact connection is still the registered one, so a superseded
        // connection tearing down cannot evict the connection that replaced it.
        _hosts.TryRemove(new KeyValuePair<string, TunnelConnection>(connection.HostId, connection));
    }

    public bool TryGet(string hostId, out TunnelConnection? connection)
    {
        if (_hosts.TryGetValue(hostId, out var found))
        {
            connection = found;
            return true;
        }

        connection = null;
        return false;
    }

    public IReadOnlyCollection<TunnelConnection> Online => _hosts.Values.ToArray();
}
