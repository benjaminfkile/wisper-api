using StackExchange.Redis;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Redis <see cref="IHostDegradedStore"/>: the degraded set lives in one Redis set
/// <c>{prefix}:degraded</c> keyed by host id (task #62, docs/DESIGN.md §7). Set/clear map to
/// <c>SADD</c>/<c>SREM</c>, membership to <c>SISMEMBER</c>, and the placement snapshot to
/// <c>SMEMBERS</c>. Verified against a real Redis separately — Grunt tests the in-memory store.
/// </summary>
public sealed class RedisHostDegradedStore : IHostDegradedStore
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisKey _key;

    public RedisHostDegradedStore(IConnectionMultiplexer multiplexer, BackplaneOptions options)
    {
        _multiplexer = multiplexer;
        _key = $"{options.ChannelPrefix}:degraded";
    }

    private IDatabase Db => _multiplexer.GetDatabase();

    public Task SetDegradedAsync(string hostId, CancellationToken ct = default) =>
        Db.SetAddAsync(_key, hostId);

    public Task ClearDegradedAsync(string hostId, CancellationToken ct = default) =>
        Db.SetRemoveAsync(_key, hostId);

    public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
        Db.SetContainsAsync(_key, hostId);

    public async Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default)
    {
        var members = await Db.SetMembersAsync(_key);
        return members.Select(m => m.ToString()).ToArray();
    }
}
