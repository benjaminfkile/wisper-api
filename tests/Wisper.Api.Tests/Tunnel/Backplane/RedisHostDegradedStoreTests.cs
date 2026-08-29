using System.Collections.Concurrent;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel.Backplane;
using Xunit;

namespace Wisper.Api.Tests.Tunnel.Backplane;

/// <summary>
/// Task #65: unit tests for <see cref="RedisHostDegradedStore"/> using a shared in-memory backing to
/// simulate two instances sharing one Redis (docs/DESIGN.md §7). The critical property is that every
/// <see cref="RedisHostDegradedStore.SetDegradedAsync"/> writes with a TTL (so a live degraded host is
/// refreshed on every heartbeat) and that a host past its TTL is treated as absent by the read side
/// (so a crashed instance cannot leave a stuck-degraded entry forever), while a live degraded host
/// whose TTL keeps getting refreshed never flaps healthy from expiration alone.
/// </summary>
public class RedisHostDegradedStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private const string KeyPrefix = "wisper";

    /// <summary>
    /// Returns two <see cref="RedisHostDegradedStore"/> instances backed by the same in-memory store so
    /// "instance A marks degraded, instance B sees it" can be verified -- mirroring the
    /// <c>RedisShellTicketStoreTests</c> shared-pair pattern.
    /// </summary>
    private static (RedisHostDegradedStore A, RedisHostDegradedStore B) MakeSharedPair(FakeTimeProvider time)
    {
        var shared = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        RedisHostDegradedStore Make() => new(
            (key, ttl) =>
            {
                shared[key] = time.GetUtcNow() + ttl;
                return Task.CompletedTask;
            },
            key =>
            {
                shared.TryRemove(key, out _);
                return Task.CompletedTask;
            },
            key =>
            {
                if (!shared.TryGetValue(key, out var expiresAt))
                {
                    return Task.FromResult(false);
                }
                if (time.GetUtcNow() >= expiresAt)
                {
                    shared.TryRemove(key, out _); // lazy expiry -- mirrors Redis's own behavior
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            },
            () =>
            {
                var alive = new List<string>();
                var now = time.GetUtcNow();
                var prefix = KeyPrefix + ":degraded:";
                foreach (var kv in shared)
                {
                    if (kv.Value > now && kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        alive.Add(kv.Key[prefix.Length..]);
                    }
                }
                return Task.FromResult<IReadOnlyCollection<string>>(alive);
            },
            KeyPrefix,
            Ttl);

        return (Make(), Make());
    }

    [Fact]
    public async Task Set_on_A_is_visible_on_B()
    {
        var (a, b) = MakeSharedPair(new FakeTimeProvider(T0));

        await a.SetDegradedAsync("host-1");

        Assert.True(await b.IsDegradedAsync("host-1"));
        Assert.Contains("host-1", await b.SnapshotAsync());
    }

    [Fact]
    public async Task Clear_on_A_removes_from_B()
    {
        var (a, b) = MakeSharedPair(new FakeTimeProvider(T0));
        await a.SetDegradedAsync("host-1");

        await a.ClearDegradedAsync("host-1");

        Assert.False(await b.IsDegradedAsync("host-1"));
        Assert.DoesNotContain("host-1", await b.SnapshotAsync());
    }

    [Fact]
    public async Task Entry_expires_after_ttl_without_a_refresh()
    {
        // A crashed instance leaves a stuck-degraded entry behind; the TTL is the backstop so the
        // stale entry does not exclude the host forever (task #65).
        var time = new FakeTimeProvider(T0);
        var (a, b) = MakeSharedPair(time);
        await a.SetDegradedAsync("host-1");
        Assert.True(await b.IsDegradedAsync("host-1"));

        time.Advance(Ttl + TimeSpan.FromSeconds(1));

        Assert.False(await b.IsDegradedAsync("host-1"));
        Assert.DoesNotContain("host-1", await b.SnapshotAsync());
    }

    [Fact]
    public async Task Repeated_set_refreshes_ttl_so_a_live_degraded_host_never_flaps_healthy()
    {
        // AC #226: a degraded host that keeps heartbeating never expires from TTL alone. Every SET on
        // the same key resets its expiration, so as long as the beat cadence stays inside the TTL the
        // host stays in the store continuously.
        var time = new FakeTimeProvider(T0);
        var (a, b) = MakeSharedPair(time);

        for (var i = 0; i < 5; i++)
        {
            await a.SetDegradedAsync("host-1");
            // Advance halfway through the TTL between refreshes -- the entry should never expire.
            time.Advance(Ttl / 2);
            Assert.True(await b.IsDegradedAsync("host-1"),
                $"host must remain degraded across TTL refresh #{i}");
        }
    }

    [Fact]
    public async Task Snapshot_returns_only_hostids_not_full_keys()
    {
        var (a, _) = MakeSharedPair(new FakeTimeProvider(T0));
        await a.SetDegradedAsync("host-1");
        await a.SetDegradedAsync("host-2");

        var snapshot = await a.SnapshotAsync();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains("host-1", snapshot);
        Assert.Contains("host-2", snapshot);
        // No entry carries the {prefix}:degraded: envelope.
        Assert.All(snapshot, id => Assert.DoesNotContain(":", id, StringComparison.Ordinal));
    }
}
