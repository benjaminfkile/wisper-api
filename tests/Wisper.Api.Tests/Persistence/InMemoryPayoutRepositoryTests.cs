using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Payouts;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IPayoutRepository"/> against the in-memory double (Grunt has no Postgres).
/// Covers create + round-trip, per-host listing newest-first, status/transfer advancement, and the unique
/// <c>stripe_transfer_id</c> that stops a double-pay (docs/DATA_MODEL.md §9, docs/PAYMENTS.md §6).
/// </summary>
public class InMemoryPayoutRepositoryTests
{
    private static Payout NewPayout(Guid host, long amount = 10_000) => new()
    {
        HostUserId = host,
        AmountCents = amount,
    };

    [Fact]
    public async Task Create_assigns_id_and_defaults_pending()
    {
        var repo = new InMemoryPayoutRepository();
        var host = Guid.NewGuid();

        var created = await repo.CreateAsync(NewPayout(host));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(PayoutStatus.Pending, created.Status);
        Assert.Equal(created, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Update_advances_status_and_records_the_transfer()
    {
        var repo = new InMemoryPayoutRepository();
        var created = await repo.CreateAsync(NewPayout(Guid.NewGuid()));

        var updated = await repo.UpdateAsync(created with
        {
            Status = PayoutStatus.InTransit,
            StripeTransferId = "tr_1",
            PayoutTxnId = Guid.NewGuid(),
        });

        Assert.Equal(PayoutStatus.InTransit, updated.Status);
        Assert.Equal("tr_1", updated.StripeTransferId);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Duplicate_stripe_transfer_id_is_rejected()
    {
        var repo = new InMemoryPayoutRepository();
        var host = Guid.NewGuid();
        var a = await repo.CreateAsync(NewPayout(host));
        var b = await repo.CreateAsync(NewPayout(host));

        await repo.UpdateAsync(a with { StripeTransferId = "tr_dup" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(b with { StripeTransferId = "tr_dup" }));
    }

    [Fact]
    public async Task ListByHost_scopes_and_orders_newest_first()
    {
        var repo = new InMemoryPayoutRepository();
        var host = Guid.NewGuid();
        var early = await repo.CreateAsync(NewPayout(host) with { CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) });
        var late = await repo.CreateAsync(NewPayout(host) with { CreatedAt = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero) });
        await repo.CreateAsync(NewPayout(Guid.NewGuid()));

        var rows = await repo.ListByHostAsync(host);

        Assert.Equal(new[] { late.Id, early.Id }, rows.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task Update_on_unknown_payout_throws()
    {
        var repo = new InMemoryPayoutRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(NewPayout(Guid.NewGuid()) with { Id = Guid.NewGuid() }));
    }
}
