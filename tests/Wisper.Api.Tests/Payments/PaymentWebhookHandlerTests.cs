using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Audit;
using Wisper.Api.Billing;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Payments.Handlers;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for the refund + dispute webhook handler (docs/PAYMENTS.md §7, §8) over the in-memory ledger,
/// user repository, and audit log (Grunt has no Stripe/Postgres). The load-bearing guarantees: a
/// <c>charge.refunded</c> posts a <c>refund</c> keyed by the refund id (deduping with the API path) and a
/// refund that would overdraw the wallet is blocked, not wedged; a <c>charge.dispute.created</c> posts a
/// <c>chargeback</c> (wallet may go negative), suspends the user, and audits — all exactly once under
/// re-delivery.
/// </summary>
public class PaymentWebhookHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public LedgerService Ledger { get; }
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryAuditLogRepository AuditLog { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public PaymentWebhookHandler Handler { get; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            var audit = new AuditService(AuditLog, Clock);
            Handler = new PaymentWebhookHandler(
                Ledger, Users, audit, Clock, NullLogger<PaymentWebhookHandler>.Instance);
        }

        public Task<User> SeedUserAsync(string customerId, UserStatus status = UserStatus.Active) =>
            Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = "consumer@example.com",
                Status = status,
                StripeCustomerId = customerId,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

        public async Task FundWalletAsync(Guid userId, long cents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: cents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{Guid.NewGuid():N}"));
        }

        public Task<long> WalletAsync(Guid userId) => Ledger.GetWalletBalanceCentsAsync(userId);
    }

    private static Stripe.Event Refunded(
        string eventId, string customerId, long refundAmount, string refundId = "re_1", string currency = "usd")
    {
        var charge = new Stripe.Charge
        {
            Id = "ch_1",
            Currency = currency,
            CustomerId = customerId,
            AmountRefunded = refundAmount,
            Refunds = new Stripe.StripeList<Stripe.Refund>
            {
                Data = new List<Stripe.Refund> { new() { Id = refundId, Amount = refundAmount } },
            },
        };
        return new Stripe.Event
        {
            Id = eventId,
            Type = Stripe.EventTypes.ChargeRefunded,
            Data = new Stripe.EventData { Object = charge },
        };
    }

    private static Stripe.Event DisputeCreated(
        string eventId, string customerId, long amount, string disputeId = "dp_1", string currency = "usd")
    {
        var dispute = new Stripe.Dispute
        {
            Id = disputeId,
            Amount = amount,
            Currency = currency,
            Charge = new Stripe.Charge { Id = "ch_1", CustomerId = customerId },
        };
        return new Stripe.Event
        {
            Id = eventId,
            Type = Stripe.EventTypes.ChargeDisputeCreated,
            Data = new Stripe.EventData { Object = dispute },
        };
    }

    // ---- charge.refunded --------------------------------------------------------------------------

    [Fact]
    public async Task Refunded_posts_a_refund_and_debits_the_wallet()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 2000);

        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 1000));

        Assert.Equal(1000, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_dedupes_against_the_api_initiated_refund()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 2000);

        // Simulate the API path having already posted the refund keyed by the same refund id.
        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user.Id);
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        await fx.Ledger.PostAsync(LedgerFlows.Refund(
            wallet.Id, cash.Id, fees.Id, grossAmountCents: 1000, stripeFeeCents: 0,
            idempotencyKey: BillingService.RefundIdempotencyKey("re_1")));
        Assert.Equal(1000, await fx.WalletAsync(user.Id));

        // The webhook for the same refund id must not debit a second time.
        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 1000, refundId: "re_1"));

        Assert.Equal(1000, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_exceeding_the_wallet_is_blocked_not_wedged()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 500);

        // A refund larger than the unspent balance must not drive the wallet negative and must not throw
        // (a chargeback is the only negative-wallet case; a spent-credit refund is an admin adjustment).
        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 1000));

        Assert.Equal(500, await fx.WalletAsync(user.Id)); // unchanged — blocked, not posted
    }

    [Fact]
    public async Task Refunded_for_an_unknown_customer_is_a_benign_no_op()
    {
        var fx = new Fixture();
        await fx.SeedUserAsync("cus_mine");

        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_stranger", refundAmount: 1000)); // no throw

        var accounts = await fx.Ledger.ReconcileAsync();
        Assert.DoesNotContain(accounts, a => a.Kind == LedgerAccountKind.UserWallet);
    }

    // ---- charge.dispute.created -------------------------------------------------------------------

    [Fact]
    public async Task Dispute_posts_a_chargeback_that_can_drive_the_wallet_negative()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 600); // less than the disputed amount (credits partly spent)

        await fx.Handler.HandleAsync(DisputeCreated("evt_1", "cus_1", amount: 1000));

        Assert.Equal(-400, await fx.WalletAsync(user.Id)); // a genuine debt
    }

    [Fact]
    public async Task Dispute_suspends_the_user_and_audits()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 1000);

        await fx.Handler.HandleAsync(DisputeCreated("evt_1", "cus_1", amount: 1000));

        Assert.Equal(UserStatus.Suspended, (await fx.Users.GetByIdAsync(user.Id))!.Status);

        var audit = await fx.AuditLog.ListByTargetAsync("user", user.Id);
        var entry = Assert.Single(audit);
        Assert.Equal("user.chargeback_suspend", entry.Action);
        Assert.Null(entry.ActorUserId); // system action
    }

    [Fact]
    public async Task Dispute_redelivery_is_idempotent()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 1000);
        var evt = DisputeCreated("evt_dup", "cus_1", amount: 1000);

        await fx.Handler.HandleAsync(evt);
        await fx.Handler.HandleAsync(evt); // Stripe re-delivers the same dispute event
        await fx.Handler.HandleAsync(evt);

        Assert.Equal(0, await fx.WalletAsync(user.Id));        // charged back once (1000 − 1000), not thrice
        Assert.Single(await fx.AuditLog.ListByTargetAsync("user", user.Id)); // audited once
        Assert.Equal(UserStatus.Suspended, (await fx.Users.GetByIdAsync(user.Id))!.Status);
    }

    [Fact]
    public async Task Dispute_for_an_unknown_customer_is_a_benign_no_op()
    {
        var fx = new Fixture();
        await fx.SeedUserAsync("cus_mine");

        await fx.Handler.HandleAsync(DisputeCreated("evt_1", "cus_stranger", amount: 1000)); // no throw

        var accounts = await fx.Ledger.ReconcileAsync();
        Assert.DoesNotContain(accounts, a => a.Kind == LedgerAccountKind.UserWallet);
    }

    // ---- charge.dispute.closed --------------------------------------------------------------------

    [Fact]
    public async Task Dispute_closed_is_a_no_op()
    {
        var fx = new Fixture();
        var evt = new Stripe.Event
        {
            Id = "evt_closed",
            Type = Stripe.EventTypes.ChargeDisputeClosed,
            Data = new Stripe.EventData { Object = new Stripe.Dispute { Id = "dp_1", Status = "won" } },
        };

        await fx.Handler.HandleAsync(evt); // recognised, informational, must not throw
    }
}
