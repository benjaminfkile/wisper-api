using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Hosts;
using Wisper.Api.Infrastructure;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Hosts;

/// <summary>
/// Unit tests for <see cref="HostConnectService"/> (docs/API.md §6, docs/PAYMENTS.md §5) over the in-memory
/// user repository and a fake Connect gateway (Grunt has no Stripe/Postgres). Covers: first onboarding
/// creates an Express account, pins <c>connect_account_id</c> + moves status to <c>pending</c>, and mints an
/// Account Link with the configured URLs; a second call continues (no second account); the status read
/// derives <c>connect_status</c> from the account snapshot and reconciles the stored value.
/// </summary>
public class HostConnectServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public FakeStripeConnectGateway Gateway { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public HostConnectService Service { get; }

        public Fixture(StripeOptions? options = null)
        {
            Service = new HostConnectService(
                Gateway,
                Users,
                Options.Create(options ?? DefaultOptions()),
                Clock,
                NullLogger<HostConnectService>.Instance);
        }

        public static StripeOptions DefaultOptions() => new()
        {
            ConnectRefreshUrl = "https://host.wisper.test/connect/refresh",
            ConnectReturnUrl = "https://host.wisper.test/connect/return",
        };

        public async Task<User> SeedHostAsync(
            string? connectAccountId = null, ConnectStatus status = ConnectStatus.None) =>
            await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = "host@example.com",
                Status = UserStatus.Active,
                ConnectAccountId = connectAccountId,
                ConnectStatus = status,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
    }

    [Fact]
    public async Task First_onboarding_creates_an_account_and_pins_it_pending()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync();

        var result = await fx.Service.StartOnboardingAsync(user.Id);

        Assert.Equal(fx.Gateway.OnboardingUrl, result.OnboardingUrl);
        Assert.Equal("pending", result.ConnectStatus);

        var account = Assert.Single(fx.Gateway.AccountCalls);
        Assert.Equal(user.Id, account.UserId);
        Assert.Equal("host@example.com", account.Email);

        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal("acct_fake", stored!.ConnectAccountId);
        Assert.Equal(ConnectStatus.Pending, stored.ConnectStatus);

        // The Account Link carried the stored account id + the configured redirect URLs.
        var link = Assert.Single(fx.Gateway.LinkCalls);
        Assert.Equal("acct_fake", link.AccountId);
        Assert.Equal("https://host.wisper.test/connect/refresh", link.RefreshUrl);
        Assert.Equal("https://host.wisper.test/connect/return", link.ReturnUrl);
    }

    [Fact]
    public async Task Second_onboarding_continues_without_a_new_account()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync(connectAccountId: "acct_existing", status: ConnectStatus.Restricted);

        var result = await fx.Service.StartOnboardingAsync(user.Id);

        Assert.Empty(fx.Gateway.AccountCalls); // reused the linked account
        Assert.Equal("restricted", result.ConnectStatus);
        var link = Assert.Single(fx.Gateway.LinkCalls);
        Assert.Equal("acct_existing", link.AccountId);
    }

    [Fact]
    public async Task Onboarding_without_configured_urls_is_a_500()
    {
        var fx = new Fixture(new StripeOptions()); // no ConnectRefreshUrl/ReturnUrl
        var user = await fx.SeedHostAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => fx.Service.StartOnboardingAsync(user.Id));
        Assert.Equal(ApiErrorCode.Internal, ex.Code);
    }

    [Fact]
    public async Task Onboarding_for_a_missing_user_is_not_found()
    {
        var fx = new Fixture();

        var ex = await Assert.ThrowsAsync<ApiException>(() => fx.Service.StartOnboardingAsync(Guid.NewGuid()));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task Status_without_an_account_reports_none()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync();

        var status = await fx.Service.GetStatusAsync(user.Id);

        Assert.Equal("none", status.ConnectStatus);
        Assert.False(status.CanGoOnline);
        Assert.Empty(fx.Gateway.AccountFetches); // nothing to query Stripe for
        Assert.Empty(status.Requirements.CurrentlyDue);
    }

    [Fact]
    public async Task Status_derives_enabled_and_reconciles_the_stored_value()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync(connectAccountId: "acct_x", status: ConnectStatus.Pending);
        fx.Gateway.Snapshot = new ConnectAccountSnapshot(
            ChargesEnabled: true,
            PayoutsEnabled: true,
            DetailsSubmitted: true,
            DisabledReason: null,
            CurrentlyDue: Array.Empty<string>(),
            PastDue: Array.Empty<string>(),
            EventuallyDue: Array.Empty<string>(),
            PendingVerification: Array.Empty<string>());

        var status = await fx.Service.GetStatusAsync(user.Id);

        Assert.Equal("enabled", status.ConnectStatus);
        Assert.True(status.CanGoOnline);
        Assert.True(status.ChargesEnabled);
        Assert.True(status.PayoutsEnabled);
        Assert.Equal("acct_x", Assert.Single(fx.Gateway.AccountFetches));

        // Reconciled: the live read is persisted so the stored status matches.
        var stored = await fx.Users.GetByIdAsync(user.Id);
        Assert.Equal(ConnectStatus.Enabled, stored!.ConnectStatus);
    }

    [Fact]
    public async Task Status_surfaces_restricted_with_requirements()
    {
        var fx = new Fixture();
        var user = await fx.SeedHostAsync(connectAccountId: "acct_x", status: ConnectStatus.Enabled);
        fx.Gateway.Snapshot = new ConnectAccountSnapshot(
            ChargesEnabled: false,
            PayoutsEnabled: false,
            DetailsSubmitted: true, // finished intake, now restricted
            DisabledReason: "requirements.past_due",
            CurrentlyDue: new[] { "individual.verification.document" },
            PastDue: new[] { "individual.verification.document" },
            EventuallyDue: Array.Empty<string>(),
            PendingVerification: Array.Empty<string>());

        var status = await fx.Service.GetStatusAsync(user.Id);

        Assert.Equal("restricted", status.ConnectStatus);
        Assert.False(status.CanGoOnline);
        Assert.Equal("requirements.past_due", status.Requirements.DisabledReason);
        Assert.Contains("individual.verification.document", status.Requirements.PastDue);
    }
}
