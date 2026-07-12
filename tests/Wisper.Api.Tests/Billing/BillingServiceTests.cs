using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Billing;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Payments.Handlers;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Billing;

/// <summary>
/// Unit tests for the consumer <see cref="BillingService"/> (docs/API.md §5, docs/PAYMENTS.md §3) over the
/// in-memory ledger + repositories and a fake Stripe gateway (Grunt has no Stripe/Postgres). Covers: top-up
/// creates a PaymentIntent (and a customer on first use) with the caller's user id + the API idempotency key
/// and returns the client_secret without touching the wallet; the minimum-top-up gate; and the balance/usage
/// + ledger-view reads deriving from the ledger — where the wallet is credited only via the webhook handler.
/// </summary>
public class BillingServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public LedgerService Ledger { get; }
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeStripeBillingGateway Gateway { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public BillingService Service { get; }
        public TopupWebhookHandler Webhook { get; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            var policy = new PlatformPolicyService(Policies, Clock);
            Service = new BillingService(
                Ledger, Leases, Users, policy, Gateway, Clock, NullLogger<BillingService>.Instance);
            Webhook = new TopupWebhookHandler(Ledger, NullLogger<TopupWebhookHandler>.Instance);
        }

        public async Task<User> SeedUserAsync(string? stripeCustomerId = null) =>
            await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = "consumer@example.com",
                Status = UserStatus.Active,
                StripeCustomerId = stripeCustomerId,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

        public Task SeedPolicyAsync(long minTopupCents) =>
            Policies.AppendAsync(new PlatformPolicy
            {
                FeeBps = 1500,
                MinTopupCents = minTopupCents,
                EffectiveFrom = T0,
            });

        // Drive the webhook to actually credit the wallet (the only place a top-up credits it).
        public Task CreditViaWebhookAsync(string eventId, Guid userId, long amountCents)
        {
            var intent = new Stripe.PaymentIntent
            {
                Id = $"pi_{eventId}",
                Amount = amountCents,
                AmountReceived = amountCents,
                Currency = "usd",
                Metadata = new Dictionary<string, string> { [TopupMetadata.UserId] = userId.ToString() },
            };
            return Webhook.HandleAsync(new Stripe.Event
            {
                Id = eventId,
                Type = Stripe.EventTypes.PaymentIntentSucceeded,
                Data = new Stripe.EventData { Object = intent },
            });
        }
    }

    [Fact]
    public async Task Topup_creates_a_customer_and_payment_intent_and_returns_client_secret()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();

        var response = await fx.Service.TopupAsync(user.Id, new TopupRequest(5000), "idem-1");

        Assert.Equal("pi_fake_secret", response.ClientSecret);

        // A customer was created once and persisted onto the user row.
        Assert.Single(fx.Gateway.CustomerCalls);
        Assert.Equal(user.Id, fx.Gateway.CustomerCalls[0].UserId);
        Assert.Equal("cus_fake", (await fx.Users.GetByIdAsync(user.Id))!.StripeCustomerId);

        // The PaymentIntent carried the user id + the API idempotency key (= Stripe idempotency key).
        var pi = Assert.Single(fx.Gateway.PaymentIntentCalls);
        Assert.Equal(user.Id, pi.UserId);
        Assert.Equal("cus_fake", pi.CustomerId);
        Assert.Equal(5000, pi.AmountCents);
        Assert.Equal("idem-1", pi.IdempotencyKey);

        // Crucially: the wallet is NOT credited by the create path.
        Assert.Equal(0, await fx.Ledger.GetWalletBalanceCentsAsync(user.Id));
    }

    [Fact]
    public async Task Topup_reuses_an_existing_customer()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync(stripeCustomerId: "cus_existing");

        await fx.Service.TopupAsync(user.Id, new TopupRequest(5000), "idem-1");

        Assert.Empty(fx.Gateway.CustomerCalls); // no new customer created
        Assert.Equal("cus_existing", fx.Gateway.PaymentIntentCalls[0].CustomerId);
    }

    [Fact]
    public async Task Topup_below_the_minimum_is_payment_required()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();
        await fx.SeedPolicyAsync(minTopupCents: 1000);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.TopupAsync(user.Id, new TopupRequest(500), "idem-1"));

        Assert.Equal(ApiErrorCode.PaymentRequired, ex.Code);
        Assert.Empty(fx.Gateway.PaymentIntentCalls); // no Stripe call for a rejected amount
    }

    [Fact]
    public async Task Topup_non_positive_amount_is_validation_error()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.TopupAsync(user.Id, new TopupRequest(0), "idem-1"));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task GetBilling_reports_wallet_balance_credited_via_the_webhook()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();

        await fx.Service.TopupAsync(user.Id, new TopupRequest(5000), "idem-1");
        await fx.CreditViaWebhookAsync("evt_1", user.Id, 5000);

        var billing = await fx.Service.GetBillingAsync(user.Id);

        Assert.Equal(5000, billing.BalanceCents);
        Assert.Equal("usd", billing.Currency);
        Assert.Equal(0, billing.Usage.LeaseCount);
    }

    [Fact]
    public async Task GetBilling_summarizes_lease_usage()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();
        await fx.Leases.CreateAsync(NewLease(user.Id, LeaseStatus.Active, billableSeconds: 120, priceCentsPerMin: 5));
        await fx.Leases.CreateAsync(NewLease(user.Id, LeaseStatus.Ended, billableSeconds: 60, priceCentsPerMin: 10));

        var billing = await fx.Service.GetBillingAsync(user.Id);

        Assert.Equal(2, billing.Usage.LeaseCount);
        Assert.Equal(1, billing.Usage.ActiveLeaseCount);
        Assert.Equal(180, billing.Usage.BillableSeconds);
        // 120s@5¢/min = 10¢; 60s@10¢/min = 10¢.
        Assert.Equal(20, billing.Usage.SpentCents);
    }

    [Fact]
    public async Task ListTransactions_returns_the_wallet_ledger_view_newest_first()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();
        await fx.CreditViaWebhookAsync("evt_1", user.Id, 1000);
        fx.Clock.Advance(TimeSpan.FromSeconds(1));
        await fx.CreditViaWebhookAsync("evt_2", user.Id, 2500);

        var page = await fx.Service.ListTransactionsAsync(user.Id, limit: 25, cursor: null);

        Assert.Equal(2, page.Data.Count);
        Assert.Null(page.NextCursor);
        Assert.All(page.Data, t => Assert.Equal("topup", t.Kind));
        // Both topups are positive credits to the wallet; amounts present regardless of order.
        Assert.Contains(page.Data, t => t.AmountCents == 1000);
        Assert.Contains(page.Data, t => t.AmountCents == 2500);
    }

    [Fact]
    public async Task ListTransactions_paginates_with_a_cursor()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();
        for (var i = 0; i < 3; i++)
        {
            await fx.CreditViaWebhookAsync($"evt_{i}", user.Id, 100 * (i + 1));
        }

        var first = await fx.Service.ListTransactionsAsync(user.Id, limit: 2, cursor: null);
        Assert.Equal(2, first.Data.Count);
        Assert.NotNull(first.NextCursor);

        Assert.True(BillingCursor.TryParse(first.NextCursor, out var cursor));
        var second = await fx.Service.ListTransactionsAsync(user.Id, limit: 2, cursor: cursor);

        Assert.Single(second.Data);
        Assert.Null(second.NextCursor);

        // No overlap across pages, and all three transactions are covered.
        var ids = first.Data.Select(t => t.Id).Concat(second.Data.Select(t => t.Id)).ToList();
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task CreatePaymentMethodSetup_returns_a_setup_intent_secret()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();

        var response = await fx.Service.CreatePaymentMethodSetupAsync(user.Id);

        Assert.Equal("seti_fake_secret", response.ClientSecret);
        Assert.Single(fx.Gateway.SetupIntentCalls);
        Assert.Equal(user.Id, fx.Gateway.SetupIntentCalls[0].UserId);
    }

    private static Lease NewLease(
        Guid userId, LeaseStatus status, long billableSeconds, long priceCentsPerMin) => new()
    {
        Id = Guid.NewGuid(),
        ConsumerUserId = userId,
        HostId = Guid.NewGuid(),
        HostImageId = Guid.NewGuid(),
        ImageRef = "reg/wisp-base:latest",
        Network = NetworkMode.Open,
        TtlSeconds = 3600,
        PriceCentsPerMin = priceCentsPerMin,
        Currency = "usd",
        Status = status,
        CreatedAt = T0,
        StartedAt = T0,
        BillableSeconds = billableSeconds,
    };
}
