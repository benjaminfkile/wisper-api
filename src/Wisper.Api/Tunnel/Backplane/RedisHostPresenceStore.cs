using StackExchange.Redis;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Redis <see cref="IHostPresenceStore"/>: presence lives in a single hash <c>{prefix}:presence</c>
/// mapping <c>hostId → instanceId</c> (docs/DESIGN.md §7). Reads/writes are O(1) hash ops; the
/// supersede-safe clear uses a tiny Lua script so it only deletes the field when this instance still
/// owns it. Verified against a real Redis separately — Grunt tests the in-memory store.
/// </summary>
public sealed class RedisHostPresenceStore : IHostPresenceStore
{
    // DEL the field only if it still equals the owning instance — an atomic compare-and-clear.
    private const string ClearIfOwnerScript =
        "if redis.call('HGET', KEYS[1], ARGV[1]) == ARGV[2] then return redis.call('HDEL', KEYS[1], ARGV[1]) else return 0 end";

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisKey _key;

    public RedisHostPresenceStore(IConnectionMultiplexer multiplexer, BackplaneOptions options)
    {
        _multiplexer = multiplexer;
        _key = $"{options.ChannelPrefix}:presence";
    }

    private IDatabase Db => _multiplexer.GetDatabase();

    public Task SetOwnerAsync(string hostId, string instanceId, CancellationToken ct = default) =>
        Db.HashSetAsync(_key, hostId, instanceId);

    public async Task<string?> GetOwnerAsync(string hostId, CancellationToken ct = default)
    {
        var value = await Db.HashGetAsync(_key, hostId);
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    public Task ClearOwnerAsync(string hostId, string instanceId, CancellationToken ct = default) =>
        Db.ScriptEvaluateAsync(
            ClearIfOwnerScript,
            new RedisKey[] { _key },
            new RedisValue[] { hostId, instanceId });

    public async Task<IReadOnlyCollection<HostPresence>> SnapshotAsync(CancellationToken ct = default)
    {
        var entries = await Db.HashGetAllAsync(_key);
        return entries
            .Select(e => new HostPresence(e.Name.ToString(), e.Value.ToString()))
            .ToArray();
    }
}
