using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Payments;
using Wisper.Api.Payments.Handlers;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for <see cref="TransferWebhookHandler"/> (docs/PAYMENTS.md §6, §8.5): the <c>transfer.*</c>
/// events advance the resolved <c>payouts</c> row (created → in_transit; failed/reversed → failed, no ledger
/// effect so earnings are retained), and the connected <c>payout.*</c> events are informational no-ops.
/// Backed by the in-memory payout repository and a synthetic Stripe transfer (Grunt has no Stripe/Postgres).
/// </summary>
public class TransferWebhookHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryPayoutRepository Payouts { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public TransferWebhookHandler Handler { get; }

        public Fixture()
        {
            Handler = new TransferWebhookHandler(Payouts, Clock, NullLogger<TransferWebhookHandler>.Instance);
        }

        public Task<Payout> SeedPayoutAsync(PayoutStatus status, Guid? payoutTxnId = null) =>
            Payouts.CreateAsync(new Payout
            {
                Id = Guid.NewGuid(),
                HostUserId = Guid.NewGuid(),
                AmountCents = 500,
                Currency = "usd",
                Status = status,
                StripeTransferId = "tr_1",
                PayoutTxnId = payoutTxnId,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
    }

    private static Stripe.Event TransferEvent(string type, Guid? payoutId, string id = "evt_tr_1")
    {
        var metadata = payoutId is { } p
            ? new Dictionary<string, string> { [TransferMetadata.PayoutId] = p.ToString() }
            : new Dictionary<string, string>();
        return new Stripe.Event
        {
            Id = id,
            Type = type,
            Data = new Stripe.EventData
            {
                Object = new Stripe.Transfer { Id = "tr_1", Metadata = metadata },
            },
        };
    }

    [Fact]
    public async Task Transfer_created_moves_pending_to_in_transit()
    {
        var fx = new Fixture();
        var payout = await fx.SeedPayoutAsync(PayoutStatus.Pending);

        await fx.Handler.HandleAsync(TransferEvent(Stripe.EventTypes.TransferCreated, payout.Id));

        var stored = await fx.Payouts.GetByIdAsync(payout.Id);
        Assert.Equal(PayoutStatus.InTransit, stored!.Status);
    }

    [Fact]
    public async Task Transfer_failed_marks_failed_and_retains_earnings()
    {
        var fx = new Fixture();
        // A payout that has NOT committed a ledger txn (payout_txn_id null) — failing it retains earnings.
        var payout = await fx.SeedPayoutAsync(PayoutStatus.Pending);

        await fx.Handler.HandleAsync(TransferEvent(TransferWebhookHandler.TransferFailed, payout.Id));

        var stored = await fx.Payouts.GetByIdAsync(payout.Id);
        Assert.Equal(PayoutStatus.Failed, stored!.Status);
        Assert.Null(stored.PayoutTxnId); // no ledger txn was committed
        Assert.NotNull(stored.Error);
    }

    [Fact]
    public async Task Transfer_reversed_marks_failed()
    {
        var fx = new Fixture();
        var payout = await fx.SeedPayoutAsync(PayoutStatus.Pending);

        await fx.Handler.HandleAsync(TransferEvent(Stripe.EventTypes.TransferReversed, payout.Id));

        var stored = await fx.Payouts.GetByIdAsync(payout.Id);
        Assert.Equal(PayoutStatus.Failed, stored!.Status);
    }

    [Fact]
    public async Task Redelivery_of_transfer_created_is_idempotent()
    {
        var fx = new Fixture();
        var payout = await fx.SeedPayoutAsync(PayoutStatus.InTransit);
        fx.Clock.Advance(TimeSpan.FromHours(1));

        await fx.Handler.HandleAsync(TransferEvent(Stripe.EventTypes.TransferCreated, payout.Id));

        var stored = await fx.Payouts.GetByIdAsync(payout.Id);
        Assert.Equal(PayoutStatus.InTransit, stored!.Status);
        Assert.Equal(T0, stored.UpdatedAt); // already in_transit ⇒ no write
    }

    [Fact]
    public async Task Unknown_payout_is_a_benign_no_op()
    {
        var fx = new Fixture();

        // No exception even though no payout matches the metadata id.
        await fx.Handler.HandleAsync(TransferEvent(Stripe.EventTypes.TransferCreated, Guid.NewGuid()));
    }

    [Fact]
    public async Task Transfer_without_payout_metadata_is_a_benign_no_op()
    {
        var fx = new Fixture();

        await fx.Handler.HandleAsync(TransferEvent(Stripe.EventTypes.TransferCreated, payoutId: null));
    }

    [Fact]
    public async Task Connected_payout_events_are_informational_no_ops()
    {
        var fx = new Fixture();
        var payout = await fx.SeedPayoutAsync(PayoutStatus.InTransit);

        var evt = new Stripe.Event
        {
            Id = "evt_payout_1",
            Type = Stripe.EventTypes.PayoutPaid,
            Data = new Stripe.EventData { Object = new Stripe.Payout { Id = "po_1" } },
        };
        await fx.Handler.HandleAsync(evt);

        var stored = await fx.Payouts.GetByIdAsync(payout.Id);
        Assert.Equal(PayoutStatus.InTransit, stored!.Status); // untouched
    }
}
