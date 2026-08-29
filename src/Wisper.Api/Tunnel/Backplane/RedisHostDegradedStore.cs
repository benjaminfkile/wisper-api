using StackExchange.Redis;

namespace Wisper.Api.Tunnel.Backplane;

/// <summary>
/// Redis <see cref="IHostDegradedStore"/>: one string key per degraded host, <c>{prefix}:degraded:{hostId}</c>,
/// carrying a native Redis TTL (<see cref="BackplaneOptions.DegradedTtlSeconds"/>) that every degraded
/// heartbeat refreshes (task #65). The per-key model -- rather than a single set -- is what gives us
/// per-host expiration: an instance that crashes with a degraded host in-flight (the Redis-mode
/// disconnect handler never runs) cannot leave a stuck-degraded entry behind forever, while a live
/// degraded host that keeps heartbeating never flaps healthy from TTL alone because its TTL is reset
/// on every beat.
///
/// <para><c>SetDegradedAsync</c> maps to <c>SET {key} 1 EX ttl</c> (atomic set + TTL reset),
/// <c>ClearDegradedAsync</c> to <c>DEL</c>, <c>IsDegradedAsync</c> to <c>EXISTS</c> (Redis expires
/// missing keys automatically, so no client-side clock check is needed), and <c>SnapshotAsync</c> to
/// <c>SCAN MATCH {prefix}:degraded:*</c>. Verified against a real Redis separately -- Grunt tests the
/// in-memory store.</para>
/// </summary>
public sealed class RedisHostDegradedStore : IHostDegradedStore
{
    private const string KeyInfix = ":degraded:";
    private const string ScanMatchSuffix = ":degraded:*";

    private readonly Func<string, TimeSpan, Task> _setWithTtl;
    private readonly Func<string, Task> _delete;
    private readonly Func<string, Task<bool>> _exists;
    private readonly Func<Task<IReadOnlyCollection<string>>> _scan;
    private readonly string _keyPrefix;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Production constructor -- reuses the backplane's <paramref name="multiplexer"/> to write, delete,
    /// probe, and scan the per-host degraded keys. TTL is <see cref="BackplaneOptions.DegradedTtlSeconds"/>.
    /// </summary>
    public RedisHostDegradedStore(IConnectionMultiplexer multiplexer, BackplaneOptions options)
    {
        _keyPrefix = options.ChannelPrefix + KeyInfix;
        var scanPattern = options.ChannelPrefix + ScanMatchSuffix;
        _ttl = TimeSpan.FromSeconds(Math.Max(1, options.DegradedTtlSeconds));

        var db = multiplexer.GetDatabase();
        _setWithTtl = (key, ttl) => db.StringSetAsync(key, "1", ttl);
        _delete = key => db.KeyDeleteAsync(key);
        _exists = key => db.KeyExistsAsync(key);
        _scan = async () =>
        {
            var results = new List<string>();
            foreach (var endpoint in multiplexer.GetEndPoints())
            {
                var server = multiplexer.GetServer(endpoint);
                if (server.IsReplica || !server.IsConnected)
                {
                    continue;
                }

                await foreach (var key in server.KeysAsync(pattern: scanPattern))
                {
                    var s = (string?)key;
                    if (s is not null && s.StartsWith(_keyPrefix, StringComparison.Ordinal))
                    {
                        results.Add(s[_keyPrefix.Length..]);
                    }
                }
            }
            return (IReadOnlyCollection<string>)results;
        };
    }

    /// <summary>
    /// Test constructor -- closes over an in-memory backing so two <see cref="RedisHostDegradedStore"/>
    /// instances built on the same shared structure behave as two instances sharing one Redis. TTL
    /// enforcement is the caller's responsibility (typically evaluated against a fake time provider).
    /// </summary>
    public RedisHostDegradedStore(
        Func<string, TimeSpan, Task> setWithTtl,
        Func<string, Task> delete,
        Func<string, Task<bool>> exists,
        Func<Task<IReadOnlyCollection<string>>> scan,
        string keyPrefix,
        TimeSpan ttl)
    {
        _setWithTtl = setWithTtl;
        _delete = delete;
        _exists = exists;
        _scan = scan;
        _keyPrefix = keyPrefix + KeyInfix;
        _ttl = ttl;
    }

    public Task SetDegradedAsync(string hostId, CancellationToken ct = default) =>
        _setWithTtl(_keyPrefix + hostId, _ttl);

    public Task ClearDegradedAsync(string hostId, CancellationToken ct = default) =>
        _delete(_keyPrefix + hostId);

    public Task<bool> IsDegradedAsync(string hostId, CancellationToken ct = default) =>
        _exists(_keyPrefix + hostId);

    public Task<IReadOnlyCollection<string>> SnapshotAsync(CancellationToken ct = default) =>
        _scan();
}
