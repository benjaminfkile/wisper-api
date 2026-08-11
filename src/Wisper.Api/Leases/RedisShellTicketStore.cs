using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StackExchange.Redis;
using Wisper.Api.Tunnel.Backplane;

namespace Wisper.Api.Leases;

/// <summary>
/// Redis-backed <see cref="IShellTicketStore"/> for multi-instance deployments (docs/API.md §7, P8.1).
/// Each ticket is a Redis string key <c>{prefix}:ticket:{tkt_…}</c> whose value is a compact JSON
/// payload binding the ticket to <c>(user, lease)</c>. Redis TTL enforces the ~30 s lifetime.
/// Redemption calls <c>GETDEL</c> — atomic across all instances sharing the same Redis: exactly one
/// concurrent presenter wins the key; all others get nil.
/// </summary>
/// <remarks>
/// <para>
/// <b>Production</b>: use <see cref="RedisShellTicketStore(IConnectionMultiplexer,BackplaneOptions)"/>;
/// the DI registration in <see cref="Wisper.Api.Tunnel.Backplane.BackplaneServiceCollectionExtensions"/>
/// supplies the backplane's shared multiplexer so no second Redis connection is opened.
/// </para>
/// <para>
/// <b>Tests</b>: use <see cref="RedisShellTicketStore(Action{string,string,TimeSpan},Func{string,string?},string)"/>
/// and close both the <paramref name="setWithTtl"/> and <paramref name="getDelete"/> delegates over the
/// same in-memory structure to simulate two instances sharing one Redis.
/// </para>
/// </remarks>
public sealed class RedisShellTicketStore : IShellTicketStore
{
    /// <summary>Ticket lifetime (docs/API.md §7: single-use, ~30 s TTL).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private const string TicketPrefix = "tkt_";
    private const int TokenBytes = 32;
    private const string KeyInfix = ":ticket:";

    private readonly Action<string, string, TimeSpan> _setWithTtl;
    private readonly Func<string, string?> _getDelete;
    private readonly string _keyPrefix;

    /// <summary>
    /// Production constructor — reuses the backplane's <paramref name="multiplexer"/> to write and
    /// atomically read-delete ticket keys, using Redis TTL for expiry.
    /// </summary>
    public RedisShellTicketStore(IConnectionMultiplexer multiplexer, BackplaneOptions options)
    {
        var db = multiplexer.GetDatabase();
        _keyPrefix = options.ChannelPrefix + KeyInfix;
        _setWithTtl = (key, json, ttl) => db.StringSet(key, json, ttl);
        _getDelete = key =>
        {
            // GETDEL: atomically returns the string and removes the key (or returns nil if absent/expired).
            var v = db.StringGetDelete(key);
            return v.IsNullOrEmpty ? null : v.ToString();
        };
    }

    /// <summary>
    /// Test constructor — both delegates should close over the same shared in-memory structure so two
    /// <see cref="RedisShellTicketStore"/> instances behave as if they share one Redis.
    /// </summary>
    public RedisShellTicketStore(
        Action<string, string, TimeSpan> setWithTtl,
        Func<string, string?> getDelete,
        string keyPrefix)
    {
        _setWithTtl = setWithTtl;
        _getDelete = getDelete;
        _keyPrefix = keyPrefix + KeyInfix;
    }

    public ShellTicket Mint(Guid userId, Guid leaseId)
    {
        var value = TicketPrefix + NewToken();
        var key = _keyPrefix + value;
        // Ticket value is the key — it must not appear in the stored payload.
        var json = Serialize(new Payload(userId, leaseId));
        _setWithTtl(key, json, Ttl);
        return new ShellTicket(value, (int)Ttl.TotalSeconds);
    }

    public ShellTicketRedemption? Redeem(string? ticket, Guid leaseId)
    {
        if (string.IsNullOrEmpty(ticket)) return null;

        var key = _keyPrefix + ticket;

        // Atomic GETDEL: one winner gets the JSON; concurrent or repeat presenters get null.
        var raw = _getDelete(key);
        if (raw is null) return null;

        Payload? payload;
        try { payload = Deserialize(raw); }
        catch (JsonException) { return null; }

        if (payload is null) return null;

        // Bound to exactly one lease — a wrong-lease presentation burns the ticket.
        if (payload.L != leaseId) return null;

        return new ShellTicketRedemption(payload.U, leaseId);
    }

    private static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Serialize(Payload p) => JsonSerializer.Serialize(p);

    private static Payload? Deserialize(string json) => JsonSerializer.Deserialize<Payload>(json);

    // Compact field names keep stored values small; property names never appear in logs.
    private sealed record Payload(
        [property: JsonPropertyName("u")] Guid U,
        [property: JsonPropertyName("l")] Guid L);
}
