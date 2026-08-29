using Wisper.Api.Leases;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Unit tests for the shell-ticket store (docs/API.md §7): the single-use, short-TTL, <c>(user, lease)</c>
/// binding a browser presents on the <c>WS /v1/leases/:id/shell</c> handshake. The clock is a
/// <see cref="FakeTimeProvider"/> so expiry is deterministic without wall-clock sleeps.
/// </summary>
public class InMemoryShellTicketStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minted_ticket_is_prefixed_and_carries_the_ttl()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));

        var ticket = store.Mint(Guid.NewGuid(), Guid.NewGuid());

        Assert.StartsWith("tkt_", ticket.Value);
        Assert.Equal((int)InMemoryShellTicketStore.Ttl.TotalSeconds, ticket.ExpiresInSeconds);
    }

    [Fact]
    public void Redeem_returns_the_bound_user_and_lease()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));
        var user = Guid.NewGuid();
        var lease = Guid.NewGuid();
        var ticket = store.Mint(user, lease);

        var redemption = store.Redeem(ticket.Value, lease);

        Assert.NotNull(redemption);
        Assert.Equal(user, redemption!.UserId);
        Assert.Equal(lease, redemption.LeaseId);
    }

    [Fact]
    public void Ticket_is_single_use()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));
        var lease = Guid.NewGuid();
        var ticket = store.Mint(Guid.NewGuid(), lease);

        Assert.NotNull(store.Redeem(ticket.Value, lease));
        // A second redemption of the same ticket fails -- it was consumed by the first.
        Assert.Null(store.Redeem(ticket.Value, lease));
    }

    [Fact]
    public void Expired_ticket_does_not_redeem()
    {
        var time = new FakeTimeProvider(T0);
        var store = new InMemoryShellTicketStore(time);
        var lease = Guid.NewGuid();
        var ticket = store.Mint(Guid.NewGuid(), lease);

        // Advance just past the TTL: the ticket is stale.
        time.Advance(InMemoryShellTicketStore.Ttl + TimeSpan.FromSeconds(1));

        Assert.Null(store.Redeem(ticket.Value, lease));
    }

    [Fact]
    public void Ticket_bound_to_one_lease_does_not_redeem_for_another()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));
        var lease = Guid.NewGuid();
        var otherLease = Guid.NewGuid();
        var ticket = store.Mint(Guid.NewGuid(), lease);

        // Presenting the ticket at a different lease's socket is rejected...
        Assert.Null(store.Redeem(ticket.Value, otherLease));
        // ...and burns it -- a mismatched presentation is single-use too, so the right lease can't reuse it.
        Assert.Null(store.Redeem(ticket.Value, lease));
    }

    [Fact]
    public void Unknown_or_empty_ticket_does_not_redeem()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));

        Assert.Null(store.Redeem(null, Guid.NewGuid()));
        Assert.Null(store.Redeem(string.Empty, Guid.NewGuid()));
        Assert.Null(store.Redeem("tkt_never-minted", Guid.NewGuid()));
    }

    [Fact]
    public void Tickets_are_distinct_per_mint()
    {
        var store = new InMemoryShellTicketStore(new FakeTimeProvider(T0));
        var lease = Guid.NewGuid();

        var a = store.Mint(Guid.NewGuid(), lease);
        var b = store.Mint(Guid.NewGuid(), lease);

        Assert.NotEqual(a.Value, b.Value);
    }
}
