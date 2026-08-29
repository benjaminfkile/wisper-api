using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Payments.Handlers;
using Xunit;

namespace Wisper.Api.Tests.Billing;

/// <summary>
/// Unit tests for the top-up webhook handler (docs/PAYMENTS.md §3, §8) over the in-memory ledger (Grunt has
/// no Stripe/Postgres). The load-bearing guarantee: the wallet is credited <b>exactly once</b> on
/// <c>payment_intent.succeeded</c>, keyed by the Stripe event id, so a duplicate/out-of-order re-delivery is
/// a no-op. Also covers the fee split, missing-metadata no-op, and the payment-failed no-effect path.
/// </summary>
public class TopupWebhookHandlerTests
{
    private static (TopupWebhookHandler Handler, LedgerService Ledger) Build()
    {
        var ledger = new LedgerService(new InMemoryLedgerStore());
        var handler = new TopupWebhookHandler(ledger, NullLogger<TopupWebhookHandler>.Instance);
        return (handler, ledger);
    }

    private static Stripe.Event SucceededEvent(
        string eventId, Guid userId, long amountCents, long feeCents = 0, string currency = "usd")
    {
        var intent = new Stripe.PaymentIntent
        {
            Id = "pi_123",
            Amount = amountCents,
            AmountReceived = amountCents,
            Currency = currency,
            Metadata = new Dictionary<string, string>
            {
                [TopupMetadata.UserId] = userId.ToString(),
                [TopupMetadata.Purpose] = TopupMetadata.WalletTopup,
            },
        };
        if (feeCents > 0)
        {
            intent.LatestCharge = new Stripe.Charge
            {
                BalanceTransaction = new Stripe.BalanceTransaction { Fee = feeCents },
            };
        }

        return new Stripe.Event
        {
            Id = eventId,
            Type = Stripe.EventTypes.PaymentIntentSucceeded,
            Data = new Stripe.EventData { Object = intent },
        };
    }

    private static async Task<long> WalletBalanceAsync(LedgerService ledger, Guid userId) =>
        await ledger.GetWalletBalanceCentsAsync(userId);

    [Fact]
    public async Task Succeeded_event_credits_the_wallet_the_gross_amount()
    {
        var (handler, ledger) = Build();
        var user = Guid.NewGuid();

        await handler.HandleAsync(SucceededEvent("evt_1", user, amountCents: 2000, feeCents: 88));

        Assert.Equal(2000, await WalletBalanceAsync(ledger, user)); // wallet gets the GROSS, fee is absorbed
    }

    [Fact]
    public async Task Duplicate_delivery_credits_the_wallet_exactly_once()
    {
        var (handler, ledger) = Build();
        var user = Guid.NewGuid();
        var evt = SucceededEvent("evt_dup", user, amountCents: 1500);

        await handler.HandleAsync(evt);
        await handler.HandleAsync(evt); // Stripe re-delivers the SAME event id
        await handler.HandleAsync(evt); // and again

        Assert.Equal(1500, await WalletBalanceAsync(ledger, user)); // credited once, not thrice
    }

    [Fact]
    public async Task Two_distinct_events_each_credit_once()
    {
        var (handler, ledger) = Build();
        var user = Guid.NewGuid();

        await handler.HandleAsync(SucceededEvent("evt_a", user, amountCents: 1000));
        await handler.HandleAsync(SucceededEvent("evt_b", user, amountCents: 2500));

        Assert.Equal(3500, await WalletBalanceAsync(ledger, user));
    }

    [Fact]
    public async Task Topup_is_balanced_platform_cash_and_stripe_fees_absorb_the_fee()
    {
        var (handler, ledger) = Build();
        var user = Guid.NewGuid();

        await handler.HandleAsync(SucceededEvent("evt_fee", user, amountCents: 1000, feeCents: 59));

        var cash = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var fees = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
        Assert.Equal(1000, await WalletBalanceAsync(ledger, user));
        Assert.Equal(941, await ledger.GetBalanceAsync(cash.Id)); // gross - fee
        Assert.Equal(59, await ledger.GetBalanceAsync(fees.Id));
    }

    [Fact]
    public async Task Missing_user_metadata_credits_no_wallet()
    {
        var (handler, ledger) = Build();
        var intent = new Stripe.PaymentIntent { Id = "pi_x", Amount = 1000, Currency = "usd" };
        var evt = new Stripe.Event
        {
            Id = "evt_nometa",
            Type = Stripe.EventTypes.PaymentIntentSucceeded,
            Data = new Stripe.EventData { Object = intent },
        };

        await handler.HandleAsync(evt); // must not throw -- a benign no-op

        // No wallet account created for anyone; the ledger holds no topup.
        var accounts = await ledger.ReconcileAsync();
        Assert.DoesNotContain(accounts, a => a.Kind == LedgerAccountKind.UserWallet);
    }

    [Fact]
    public async Task Payment_failed_has_no_ledger_effect()
    {
        var (handler, ledger) = Build();
        var user = Guid.NewGuid();
        var intent = new Stripe.PaymentIntent
        {
            Id = "pi_fail",
            Amount = 1000,
            Currency = "usd",
            Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = user.ToString() },
        };
        var evt = new Stripe.Event
        {
            Id = "evt_failed",
            Type = Stripe.EventTypes.PaymentIntentPaymentFailed,
            Data = new Stripe.EventData { Object = intent },
        };

        await handler.HandleAsync(evt);

        Assert.Equal(0, await WalletBalanceAsync(ledger, user));
    }
}
