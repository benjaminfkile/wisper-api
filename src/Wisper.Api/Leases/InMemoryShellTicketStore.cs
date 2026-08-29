using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Wisper.Api.Leases;

/// <summary>
/// In-memory <see cref="IShellTicketStore"/> (docs/API.md §7): a process-local map of opaque
/// <c>tkt_&lt;random&gt;</c> tokens to their <c>(user, lease, expiry)</c> binding. Tickets are single-use
/// (redemption removes them) and short-lived (~30s). This is deliberately non-durable for now -- a shell
/// ticket only needs to survive the seconds between minting and the WS handshake on the same instance; a
/// Redis-backed store lands with the multi-instance backplane (docs/API.md §7, P8.1). Tokens are 256 bits
/// of CSPRNG entropy, so they are unguessable and safe to carry in a URL.
/// </summary>
public sealed class InMemoryShellTicketStore : IShellTicketStore
{
    /// <summary>The ticket lifetime (docs/API.md §7: single-use, ~30s TTL).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private const string Prefix = "tkt_";
    private const int TokenBytes = 32;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public InMemoryShellTicketStore(TimeProvider time) => _time = time;

    public ShellTicket Mint(Guid userId, Guid leaseId)
    {
        // Opportunistically drop anything already expired so an unredeemed ticket can't leak forever.
        PruneExpired();

        var now = _time.GetUtcNow();
        var value = Prefix + NewToken();
        _tickets[value] = new Entry(userId, leaseId, now + Ttl);
        return new ShellTicket(value, (int)Ttl.TotalSeconds);
    }

    public ShellTicketRedemption? Redeem(string? ticket, Guid leaseId)
    {
        // TryRemove enforces single-use: any presentation (valid or not) consumes the token, so a replay
        // or a probe against the wrong lease can't be retried.
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var entry))
        {
            return null;
        }

        if (_time.GetUtcNow() >= entry.ExpiresAt)
        {
            return null;
        }

        // The ticket is bound to one lease; presenting it at any other lease's socket is rejected.
        if (entry.LeaseId != leaseId)
        {
            return null;
        }

        return new ShellTicketRedemption(entry.UserId, entry.LeaseId);
    }

    private void PruneExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var (value, entry) in _tickets)
        {
            if (now >= entry.ExpiresAt)
            {
                _tickets.TryRemove(value, out _);
            }
        }
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        // URL-safe base64 (no padding) so the token drops straight into the handshake query string.
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record Entry(Guid UserId, Guid LeaseId, DateTimeOffset ExpiresAt);
}
