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
/// <c>chargeback</c> (wallet may go negative), suspends the user, and audits -- all exactly once under
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
        string eventId,
        string customerId,
        long refundAmount,
        string refundId = "re_1",
        string chargeId = "ch_1",
        string currency = "usd") =>
        RefundedMulti(
            eventId, customerId, chargeId, currency,
            (refundId, refundAmount));

    private static Stripe.Event RefundedMulti(
        string eventId,
        string customerId,
        string chargeId,
        string currency,
        params (string RefundId, long Amount)[] refunds)
    {
        var charge = new Stripe.Charge
        {
            Id = chargeId,
            Currency = currency,
            CustomerId = customerId,
            AmountRefunded = refunds.Sum(r => r.Amount),
            Refunds = new Stripe.StripeList<Stripe.Refund>
            {
                Data = refunds
                    .Select(r => new Stripe.Refund { Id = r.RefundId, Amount = r.Amount })
                    .ToList(),
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

        Assert.Equal(500, await fx.WalletAsync(user.Id)); // unchanged -- blocked, not posted
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

    [Fact]
    public async Task Refunded_webhook_then_api_debits_the_wallet_exactly_once()
    {
        // Regression for the double-debit bug (task #33): the webhook path used to key by charge id /
        // event id, so an API-initiated refund posted twice -- once from the API, once from the webhook.
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 5000);

        // The webhook fires FIRST (Stripe re-order can happen -- the API HTTP hop can be slower than the
        // webhook delivery under some conditions).
        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 2000, refundId: "re_1"));
        Assert.Equal(3000, await fx.WalletAsync(user.Id));

        // Now the API path posts under the SAME refund id -- must dedupe at the ledger, no extra debit.
        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user.Id);
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        await fx.Ledger.PostAsync(LedgerFlows.Refund(
            wallet.Id, cash.Id, fees.Id, grossAmountCents: 2000, stripeFeeCents: 0,
            idempotencyKey: BillingService.RefundIdempotencyKey("re_1")));

        Assert.Equal(3000, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_api_then_webhook_debits_the_wallet_exactly_once()
    {
        // Regression for the double-debit bug (task #33): the mirror ordering to the test above -- API
        // fires first, then webhook. Both must land on the same idempotency key so only one debit posts.
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 5000);

        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user.Id);
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        await fx.Ledger.PostAsync(LedgerFlows.Refund(
            wallet.Id, cash.Id, fees.Id, grossAmountCents: 2000, stripeFeeCents: 0,
            idempotencyKey: BillingService.RefundIdempotencyKey("re_1")));
        Assert.Equal(3000, await fx.WalletAsync(user.Id));

        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 2000, refundId: "re_1"));

        Assert.Equal(3000, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_two_distinct_refunds_on_the_same_charge_each_debit_once()
    {
        // Regression for the "second refund silently dropped" bug (task #33): keying the webhook by charge
        // id (rather than refund id) collapsed distinct refunds on the same charge onto one idempotency key,
        // so the second refund's webhook debit was wrongly deduped. Each refund must post its own debit.
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 5000);

        // First refund lands (via the webhook).
        await fx.Handler.HandleAsync(Refunded("evt_1", "cus_1", refundAmount: 2000, refundId: "re_1"));
        Assert.Equal(3000, await fx.WalletAsync(user.Id));

        // A second refund on the same charge -- a new refund id, its own debit must post.
        await fx.Handler.HandleAsync(RefundedMulti(
            "evt_2", "cus_1", chargeId: "ch_1", currency: "usd",
            ("re_1", 2000),   // already posted -- must dedupe
            ("re_2", 500)));  // new -- must post

        Assert.Equal(2500, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_redelivery_of_the_same_event_is_a_no_op()
    {
        // Stripe re-delivers a charge.refunded event: the ledger dedupe on refund id must keep the wallet
        // at exactly one debit per distinct refund.
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 5000);
        var evt = Refunded("evt_dup", "cus_1", refundAmount: 1500, refundId: "re_1");

        await fx.Handler.HandleAsync(evt);
        await fx.Handler.HandleAsync(evt);
        await fx.Handler.HandleAsync(evt);

        Assert.Equal(3500, await fx.WalletAsync(user.Id));
    }

    [Fact]
    public async Task Refunded_without_expanded_refunds_is_a_no_op()
    {
        // Without the refund id we can't safely dedupe with the API path -- the handler must skip and warn
        // rather than post under a wrong key that would double-debit. (In production Stripe expands
        // data.object.refunds on charge.refunded; a missing list is a config/payload issue to flag.)
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("cus_1");
        await fx.FundWalletAsync(user.Id, 2000);

        var charge = new Stripe.Charge
        {
            Id = "ch_1",
            Currency = "usd",
            CustomerId = "cus_1",
            AmountRefunded = 1000,
            // Refunds intentionally absent -- the SDK sometimes populates only when explicitly expanded.
        };
        var evt = new Stripe.Event
        {
            Id = "evt_no_refunds",
            Type = Stripe.EventTypes.ChargeRefunded,
            Data = new Stripe.EventData { Object = charge },
        };

        await fx.Handler.HandleAsync(evt); // no throw

        Assert.Equal(2000, await fx.WalletAsync(user.Id)); // unchanged
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
