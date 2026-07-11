namespace Wisper.Api.Tunnel;

/// <summary>
/// Tracks the live agent tunnel for each host (docs/TUNNEL.md §3, §16). A single tunnel
/// per host is the model; a new connection for a host that is already registered
/// <b>supersedes</b> the old one (the prior socket is closed with
/// <see cref="CloseCodes.Normal"/>). Phase 1 is an in-memory singleton
/// (<see cref="InMemoryHostRegistry"/>); a Redis-backed impl can replace it later.
/// </summary>
public interface IHostRegistry
{
    /// <summary>
    /// Registers <paramref name="connection"/> as the live tunnel for its host, superseding
    /// (and closing) any prior connection for the same host id.
    /// </summary>
    Task RegisterAsync(TunnelConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Removes <paramref name="connection"/> on disconnect. A no-op if it has already been
    /// superseded (only the currently-registered connection for the host is removed).
    /// </summary>
    void Unregister(TunnelConnection connection);

    /// <summary>Looks up the live tunnel for <paramref name="hostId"/>.</summary>
    bool TryGet(string hostId, out TunnelConnection? connection);

    /// <summary>Snapshot of all currently-online host connections.</summary>
    IReadOnlyCollection<TunnelConnection> Online { get; }
}
