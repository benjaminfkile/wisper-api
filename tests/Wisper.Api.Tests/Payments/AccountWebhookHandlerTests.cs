using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Payments.Handlers;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payments;

/// <summary>
/// Unit tests for <see cref="AccountWebhookHandler"/> (docs/PAYMENTS.md §5, §8): <c>account.updated</c>
/// recomputes <c>connect_status</c> from the account's capability flags and pins it on the owning user
/// (gating online/payouts), while re-delivery of the same state is a no-op. Backed by the in-memory user
/// repository and a fake Stripe account object (Grunt has no Stripe/Postgres).
/// </summary>
public class AccountWebhookHandlerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public AccountWebhookHandler Handler { get; }

        public Fixture()
        {
            Handler = new AccountWebhookHandler(Users, Clock, NullLogger<AccountWebhookHandler>.Instance);
        }

        public Task<User> SeedHostAsync(string accountId, ConnectStatus status) =>
            Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = "host@example.com",
                Status = UserStatus.Active,
                ConnectAccountId = accountId,
                ConnectStatus = status,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
    }

    private static Stripe.Event AccountUpdated(
        string accountId, bool charges, bool payouts, bool detailsSubmitted, string id = "evt_acct_1")
    {
        var account = new Stripe.Account
        {
            Id = accountId,
            ChargesEnabled = charges,
            PayoutsEnabled = payouts,
            DetailsSubmitted = detailsSubmitted,
            Requirements = new Stripe.AccountRequirements
            {
                DisabledReason = charges && payouts ? null : "requirements.past_due",
                CurrentlyDue = new List<string>(),
                PastDue = new List<string>(),
            },
        };
        return new Stripe.Event
        {
            Id = id,
            Type = Stripe.EventTypes.AccountUpdated,
            Data = new Stripe.EventData { Object = account },
        };
    }

    [Fact]
    public async Task Both_enabled_sets_connect_status_enabled()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync("acct_1", ConnectStatus.Pending);

        await fx.Handler.HandleAsync(AccountUpdated("acct_1", charges: true, payouts: true, detailsSubmitted: true));

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Enabled, stored!.ConnectStatus);
    }

    [Fact]
    public async Task Details_submitted_but_not_enabled_sets_restricted()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync("acct_1", ConnectStatus.Enabled);

        await fx.Handler.HandleAsync(AccountUpdated("acct_1", charges: false, payouts: true, detailsSubmitted: true));

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Restricted, stored!.ConnectStatus);
    }

    [Fact]
    public async Task Unchanged_status_does_not_rewrite_the_row()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync("acct_1", ConnectStatus.Enabled);
        fx.Clock.Advance(TimeSpan.FromHours(1)); // a rewrite would stamp this later time

        await fx.Handler.HandleAsync(AccountUpdated("acct_1", charges: true, payouts: true, detailsSubmitted: true));

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Enabled, stored!.ConnectStatus);
        Assert.Equal(T0, stored.UpdatedAt); // idempotent: no write occurred
    }

    [Fact]
    public async Task Redelivery_is_idempotent()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync("acct_1", ConnectStatus.Pending);
        var evt = AccountUpdated("acct_1", charges: true, payouts: true, detailsSubmitted: true);

        await fx.Handler.HandleAsync(evt);
        await fx.Handler.HandleAsync(evt); // Stripe re-delivers the same event

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Enabled, stored!.ConnectStatus);
    }

    [Fact]
    public async Task Unknown_account_is_a_benign_no_op()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync("acct_mine", ConnectStatus.Pending);

        // No exception, and the tracked user is untouched.
        await fx.Handler.HandleAsync(AccountUpdated("acct_stranger", charges: true, payouts: true, detailsSubmitted: true));

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Pending, stored!.ConnectStatus);
    }

    [Fact]
    public async Task Setup_intent_succeeded_is_a_no_op()
    {
        var fx = new Fixture();
        var evt = new Stripe.Event
        {
            Id = "evt_seti_1",
            Type = Stripe.EventTypes.SetupIntentSucceeded,
            Data = new Stripe.EventData { Object = new Stripe.SetupIntent { Id = "seti_1" } },
        };

        await fx.Handler.HandleAsync(evt); // must not throw
    }

    [Fact]
    public async Task Event_without_an_account_object_is_a_no_op()
    {
        var fx = new Fixture();
        var evt = new Stripe.Event
        {
            Id = "evt_bad",
            Type = Stripe.EventTypes.AccountUpdated,
            Data = new Stripe.EventData { Object = new Stripe.PaymentIntent { Id = "pi_1" } },
        };

        await fx.Handler.HandleAsync(evt); // recognised type, wrong object -- benign no-op
    }
}
