namespace Wisper.Api.Leases;

/// <summary>
/// Mints and redeems the one-time WebSocket tickets that guard <c>WS /v1/leases/:id/shell</c>
/// (docs/API.md §2, §7). A browser cannot set an <c>Authorization</c> header on a WS handshake and a JWT
/// must never land in a URL, so the JWT-authenticated <c>POST /v1/leases/:id/shell-ticket</c> mints a
/// short-lived, single-use ticket bound to <c>(user, lease)</c>; the handshake presents it in the query
/// and Wisper redeems it exactly once before bridging to the tunnel shell. The store is the sole authority
/// on single-use, expiry, and the lease binding -- the endpoint layer re-checks ownership/ready state
/// against the redeemed user.
/// </summary>
public interface IShellTicketStore
{
    /// <summary>
    /// Mints a fresh ticket bound to <paramref name="userId"/> and <paramref name="leaseId"/>, valid for a
    /// short TTL. The caller must have already established that the user owns the (active) lease.
    /// </summary>
    ShellTicket Mint(Guid userId, Guid leaseId);

    /// <summary>
    /// Redeems <paramref name="ticket"/> for <paramref name="leaseId"/>, consuming it so a second attempt
    /// fails (single-use). Returns the binding it was minted with, or <c>null</c> when the ticket is
    /// unknown/already redeemed, expired, or was minted for a different lease.
    /// </summary>
    ShellTicketRedemption? Redeem(string? ticket, Guid leaseId);
}

/// <summary>A freshly minted shell ticket: the opaque <c>tkt_…</c> value and its remaining TTL in seconds.</summary>
public sealed record ShellTicket(string Value, int ExpiresInSeconds);

/// <summary>The <c>(user, lease)</c> binding a redeemed ticket carried (docs/API.md §7).</summary>
public sealed record ShellTicketRedemption(Guid UserId, Guid LeaseId);
