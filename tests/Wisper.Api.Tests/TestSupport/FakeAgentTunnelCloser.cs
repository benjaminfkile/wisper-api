using System.Collections.Concurrent;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Tests.TestSupport;

/// <summary>
/// An <see cref="IAgentTunnelCloser"/> double: records every close request and reports whether a host had a
/// "live" tunnel to close (declared via <see cref="SetLive"/>), so a test can assert token rotation revokes
/// the old session with the right close code without a real WebSocket.
/// </summary>
public sealed class FakeAgentTunnelCloser : IAgentTunnelCloser
{
    private readonly HashSet<Guid> _live = new();

    /// <summary>Every close request made, in order.</summary>
    public ConcurrentQueue<(Guid HostId, int CloseCode, string Reason)> Closes { get; } = new();

    /// <summary>Marks <paramref name="hostId"/> as having a live tunnel, so the next close returns <c>true</c>.</summary>
    public void SetLive(Guid hostId) => _live.Add(hostId);

    public Task<bool> CloseAsync(Guid hostId, int closeCode, string reason, CancellationToken ct = default)
    {
        Closes.Enqueue((hostId, closeCode, reason));
        return Task.FromResult(_live.Remove(hostId));
    }
}
