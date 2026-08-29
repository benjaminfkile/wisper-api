using System.Collections.Concurrent;
using Wisper.Api.Leases;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Unit tests for <see cref="RedisShellTicketStore"/> using a shared in-memory backing to simulate two
/// instances sharing the same Redis (docs/API.md §7, P8.1). The test constructor closes both delegates
/// over a single <see cref="ConcurrentDictionary{TKey,TValue}"/>, proving cross-instance semantics without
/// a live Redis connection.
/// </summary>
public class RedisShellTicketStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Returns two <see cref="RedisShellTicketStore"/> instances backed by the same in-memory store so
    /// "instance A mints, instance B redeems" can be verified in a unit test.
    /// </summary>
    private static (RedisShellTicketStore A, RedisShellTicketStore B) MakeSharedPair(FakeTimeProvider time)
    {
        var shared = new ConcurrentDictionary<string, (string Json, DateTimeOffset ExpiresAt)>(
            StringComparer.Ordinal);

        RedisShellTicketStore Make() => new(
            (key, json, ttl) => { shared[key] = (json, time.GetUtcNow() + ttl); },
            key =>
            {
                // Mimic GETDEL: atomically remove and return, or null if absent or expired.
                if (shared.TryRemove(key, out var entry))
                {
                    return time.GetUtcNow() >= entry.ExpiresAt ? null : entry.Json;
                }
                return null;
            },
            "wisper");

        return (Make(), Make());
    }

    [Fact]
    public void Mint_on_A_redeems_successfully_on_B()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, storeB) = MakeSharedPair(time);
        var userId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();

        var ticket = storeA.Mint(userId, leaseId);
        var redemption = storeB.Redeem(ticket.Value, leaseId);

        Assert.NotNull(redemption);
        Assert.Equal(userId, redemption!.UserId);
        Assert.Equal(leaseId, redemption.LeaseId);
    }

    [Fact]
    public void Double_redeem_fails_second_wins_nothing()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, storeB) = MakeSharedPair(time);
        var leaseId = Guid.NewGuid();

        var ticket = storeA.Mint(Guid.NewGuid(), leaseId);

        // First redemption wins.
        Assert.NotNull(storeA.Redeem(ticket.Value, leaseId));
        // Second redemption (even from a different "instance") gets nothing -- key already deleted.
        Assert.Null(storeB.Redeem(ticket.Value, leaseId));
    }

    [Fact]
    public void Expired_ticket_does_not_redeem()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, storeB) = MakeSharedPair(time);
        var leaseId = Guid.NewGuid();

        var ticket = storeA.Mint(Guid.NewGuid(), leaseId);
        time.Advance(RedisShellTicketStore.Ttl + TimeSpan.FromSeconds(1));

        Assert.Null(storeB.Redeem(ticket.Value, leaseId));
    }

    [Fact]
    public void Wrong_lease_does_not_redeem_and_burns_the_ticket()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, storeB) = MakeSharedPair(time);
        var leaseId = Guid.NewGuid();
        var otherLease = Guid.NewGuid();

        var ticket = storeA.Mint(Guid.NewGuid(), leaseId);

        // Wrong-lease presentation is rejected ...
        Assert.Null(storeB.Redeem(ticket.Value, otherLease));
        // ... and consumes the ticket so the correct lease can't retry.
        Assert.Null(storeA.Redeem(ticket.Value, leaseId));
    }

    [Fact]
    public void Minted_ticket_has_correct_prefix_and_ttl()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, _) = MakeSharedPair(time);

        var ticket = storeA.Mint(Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith("tkt_", ticket.Value);
        Assert.Equal((int)RedisShellTicketStore.Ttl.TotalSeconds, ticket.ExpiresInSeconds);
    }

    [Fact]
    public void Unknown_or_empty_ticket_does_not_redeem()
    {
        var time = new FakeTimeProvider(T0);
        var (storeA, _) = MakeSharedPair(time);

        Assert.Null(storeA.Redeem(null, Guid.NewGuid()));
        Assert.Null(storeA.Redeem(string.Empty, Guid.NewGuid()));
        Assert.Null(storeA.Redeem("tkt_never-minted", Guid.NewGuid()));
    }
}
