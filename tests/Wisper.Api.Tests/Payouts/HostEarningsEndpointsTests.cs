using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Auth;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payouts;

/// <summary>
/// Integration tests over the real app host for the host earnings + payout surface (docs/API.md §6,
/// docs/PAYMENTS.md §6): the host-role gate, <c>GET /v1/earnings</c> + <c>/earnings/payouts</c>, and
/// <c>POST /v1/payouts</c> (required <c>Idempotency-Key</c>, <c>connect_incomplete</c> → 403, and the accrued
/// balance draining via a Connect transfer). All externals are in-memory doubles / fakes (Grunt has no
/// Stripe/Postgres).
/// </summary>
public class HostEarningsEndpointsTests
{
    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryPayoutRepository Payouts { get; } = new();
        public InMemoryLedgerStore Ledger { get; } = new();
        public InMemoryIdempotencyKeyRepository Idempotency { get; } = new();
        public FakeStripeConnectGateway Gateway { get; } = new();
        public FakeJwtValidator Validator { get; } = new()
        {
            Principal = WisperPrincipal.Create("host-sub", "host@example.com", new[] { "host" }),
        };

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IPayoutRepository>();
                    services.AddSingleton<IPayoutRepository>(Payouts);
                    services.RemoveAll<ILedgerStore>();
                    services.AddSingleton<ILedgerStore>(Ledger);
                    services.RemoveAll<IIdempotencyKeyRepository>();
                    services.AddSingleton<IIdempotencyKeyRepository>(Idempotency);
                    services.RemoveAll<IStripeConnectGateway>();
                    services.AddSingleton<IStripeConnectGateway>(Gateway);
                }));

        public Guid BootstrappedUserId() => Users.GetByCognitoSubAsync("host-sub").Result!.Id;

        /// <summary>Marks the bootstrapped host Connect-enabled and accrues <paramref name="amountCents"/> of earnings.</summary>
        public async Task EnableAndAccrueAsync(Guid hostUserId, long amountCents)
        {
            var user = await Users.GetByIdAsync(hostUserId);
            await Users.UpdateAsync(user! with
            {
                ConnectAccountId = "acct_host",
                ConnectStatus = ConnectStatus.Enabled,
            });

            var ledger = new LedgerService(Ledger);
            var consumer = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var wallet = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
            var platformCash = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var stripeFees = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            var holds = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var earnings = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, hostUserId);
            var revenue = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

            await ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, platformCash.Id, stripeFees.Id, amountCents, 0, $"topup-{Guid.NewGuid():N}"));
            await ledger.PostAsync(LedgerFlows.LeaseHold(wallet.Id, holds.Id, leaseId, amountCents));
            await ledger.PostAsync(LedgerFlows.LeaseCharge(
                holds.Id, earnings.Id, revenue.Id, leaseId, amountCents, 0));
        }
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    private static HttpRequestMessage Payout(string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payouts");
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task Earnings_requires_the_host_role()
    {
        var fx = new Fixture();
        fx.Validator.Principal = WisperPrincipal.Create("consumer-sub", "c@example.com", Array.Empty<string>());
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/earnings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Earnings_reports_the_accrued_balance()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        await client.GetAsync("/v1/earnings"); // bootstrap the user
        await fx.EnableAndAccrueAsync(fx.BootstrappedUserId(), 500);

        var earnings = await client.GetFromJsonAsync<EarningsDto>("/v1/earnings");

        Assert.Equal(500, earnings!.AccruedCents);
        Assert.Equal(0, earnings.PaidCents);
        Assert.True(earnings.CanReceivePayouts);
    }

    [Fact]
    public async Task Payout_without_an_idempotency_key_is_400()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.GetAsync("/v1/earnings");
        await fx.EnableAndAccrueAsync(fx.BootstrappedUserId(), 500);

        var response = await client.SendAsync(Payout(idempotencyKey: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fx.Gateway.TransferCalls);
    }

    [Fact]
    public async Task On_demand_payout_drains_earnings_and_lists_in_history()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.GetAsync("/v1/earnings");
        var userId = fx.BootstrappedUserId();
        await fx.EnableAndAccrueAsync(userId, 500);

        var response = await client.SendAsync(Payout("payout-key-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await response.Content.ReadFromJsonAsync<PayoutDto>();
        Assert.Equal(500, view!.AmountCents);
        Assert.Equal("in_transit", view.Status);
        Assert.NotNull(view.StripeTransferId);
        Assert.Single(fx.Gateway.TransferCalls);

        // Earnings drained; history shows the row.
        var earnings = await client.GetFromJsonAsync<EarningsDto>("/v1/earnings");
        Assert.Equal(0, earnings!.AccruedCents);
        Assert.Equal(500, earnings.PaidCents);

        var history = await client.GetFromJsonAsync<PayoutListDto>("/v1/earnings/payouts");
        Assert.Single(history!.Data);
    }

    [Fact]
    public async Task On_demand_payout_replays_the_same_key_without_a_second_transfer()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.GetAsync("/v1/earnings");
        await fx.EnableAndAccrueAsync(fx.BootstrappedUserId(), 500);

        var first = await client.SendAsync(Payout("payout-key-1"));
        var second = await client.SendAsync(Payout("payout-key-1"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<PayoutDto>();
        var b = await second.Content.ReadFromJsonAsync<PayoutDto>();
        Assert.Equal(a!.Id, b!.Id);
        Assert.Single(fx.Gateway.TransferCalls); // exactly one transfer
    }

    [Fact]
    public async Task On_demand_payout_with_incomplete_connect_is_403()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);
        await client.GetAsync("/v1/earnings"); // bootstrap; connect_status stays `none`

        var response = await client.SendAsync(Payout("payout-key-1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(fx.Gateway.TransferCalls);
    }

    private sealed record EarningsDto(
        [property: JsonPropertyName("accrued_cents")] long AccruedCents,
        [property: JsonPropertyName("paid_cents")] long PaidCents,
        [property: JsonPropertyName("can_receive_payouts")] bool CanReceivePayouts);

    private sealed record PayoutDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("amount_cents")] long AmountCents,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("stripe_transfer_id")] string? StripeTransferId);

    private sealed record PayoutListDto(
        [property: JsonPropertyName("data")] IReadOnlyList<PayoutDto> Data);
}
