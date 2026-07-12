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
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Admin;

/// <summary>
/// Integration tests over the real app host for the admin API (docs/API.md §8, P7.2): the admin-role gate,
/// the overview, versioned policy (audited), host/user search + suspend/unsuspend (audited), the manual
/// refund and balanced ledger adjustment (both <c>Idempotency-Key</c>-guarded + audited), the audit view,
/// and read-only ledger forensics. All externals are in-memory doubles / fakes (Grunt has no Stripe/Postgres).
/// </summary>
public class AdminEndpointsTests
{
    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLedgerStore Ledger { get; } = new();
        public InMemoryAuditLogRepository AuditLog { get; } = new();
        public InMemoryPlatformPolicyRepository Policy { get; } = new();
        public InMemoryIdempotencyKeyRepository Idempotency { get; } = new();
        public FakeStripeBillingGateway Stripe { get; } = new();

        public FakeJwtValidator Validator { get; } = new()
        {
            Principal = WisperPrincipal.Create("admin-sub", "admin@example.com", new[] { "admin" }),
        };

        public WebApplicationFactory<Program> Build() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IJwtValidator>();
                    services.AddSingleton<IJwtValidator>(Validator);
                    services.RemoveAll<IUserRepository>();
                    services.AddSingleton<IUserRepository>(Users);
                    services.RemoveAll<IHostRepository>();
                    services.AddSingleton<IHostRepository>(Hosts);
                    services.RemoveAll<ILeaseRepository>();
                    services.AddSingleton<ILeaseRepository>(Leases);
                    services.RemoveAll<ILedgerStore>();
                    services.AddSingleton<ILedgerStore>(Ledger);
                    services.RemoveAll<IAuditLogRepository>();
                    services.AddSingleton<IAuditLogRepository>(AuditLog);
                    services.RemoveAll<IPlatformPolicyRepository>();
                    services.AddSingleton<IPlatformPolicyRepository>(Policy);
                    services.RemoveAll<IIdempotencyKeyRepository>();
                    services.AddSingleton<IIdempotencyKeyRepository>(Idempotency);
                    services.RemoveAll<IStripeBillingGateway>();
                    services.AddSingleton<IStripeBillingGateway>(Stripe);
                }));

        public async Task<Guid> SeedUserAsync(string email)
        {
            var user = await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = email,
                CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            });
            return user.Id;
        }

        public Task<Host> SeedHostAsync(Guid owner, string name) =>
            Hosts.CreateAsync(new Host
            {
                OwnerUserId = owner,
                Name = name,
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            });

        public async Task FundWalletAsync(Guid userId, long amountCents, string paymentIntent)
        {
            var ledger = new LedgerService(Ledger);
            var wallet = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId);
            var cash = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, amountCents, 0, $"topup-{Guid.NewGuid():N}", externalRef: paymentIntent));
        }
    }

    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer good");
        return client;
    }

    [Fact]
    public async Task Admin_surface_requires_the_admin_role()
    {
        var fx = new Fixture();
        fx.Validator.Principal = WisperPrincipal.Create("host-sub", "h@example.com", new[] { "host" });
        using var factory = fx.Build();

        var response = await Authed(factory).GetAsync("/v1/admin/overview");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Overview_reports_counts()
    {
        var fx = new Fixture();
        var owner = await fx.SeedUserAsync("owner@example.com");
        await fx.SeedHostAsync(owner, "h1");
        using var factory = fx.Build();

        var overview = await Authed(factory).GetFromJsonAsync<OverviewDto>("/v1/admin/overview");

        // The admin caller is bootstrapped on the first authenticated call, so users >= 2.
        Assert.True(overview!.UserCount >= 2);
        Assert.Equal(1, overview.HostCount);
        Assert.Equal(1, overview.OnlineHostCount);
    }

    [Fact]
    public async Task Put_policy_publishes_a_version_and_audits()
    {
        var fx = new Fixture();
        using var factory = fx.Build();
        var client = Authed(factory);

        var put = await client.PutAsJsonAsync("/v1/admin/policy", new { fee_bps = 1500, min_topup_cents = 500 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var view = await client.GetFromJsonAsync<PolicyHistoryDto>("/v1/admin/policy");
        Assert.NotNull(view!.Active);
        Assert.Equal(1500, view.Active!.FeeBps);

        var audit = await fx.AuditLog.ListAsync(new AuditLogQuery { Action = "policy.update" });
        Assert.Single(audit);
    }

    [Fact]
    public async Task Suspend_host_flips_status_and_appears_in_the_audit_view()
    {
        var fx = new Fixture();
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner, "h1");
        using var factory = fx.Build();
        var client = Authed(factory);

        var suspend = await client.PostAsync($"/v1/admin/hosts/{host.Id}/suspend", null);
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        var stored = await fx.Hosts.GetByIdAsync(host.Id);
        Assert.Equal(HostStatus.Suspended, stored!.Status);

        var audit = await client.GetFromJsonAsync<AuditPageDto>("/v1/admin/audit?action=host.suspend");
        Assert.Single(audit!.Data);
        Assert.Equal(host.Id, audit.Data[0].TargetId);
    }

    [Fact]
    public async Task User_search_narrows_by_query()
    {
        var fx = new Fixture();
        await fx.SeedUserAsync("alice@example.com");
        await fx.SeedUserAsync("bob@example.com");
        using var factory = fx.Build();

        var page = await Authed(factory).GetFromJsonAsync<UserPageDto>("/v1/admin/users?query=alice");

        Assert.Single(page!.Data);
        Assert.Equal("alice@example.com", page.Data[0].Email);
    }

    [Fact]
    public async Task Refund_requires_an_idempotency_key()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("c@example.com");
        using var factory = fx.Build();

        var response = await Authed(factory).PostAsJsonAsync(
            "/v1/admin/refunds", new { user_id = user, amount_cents = 100 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fx.Stripe.RefundCalls);
    }

    [Fact]
    public async Task Refund_debits_the_wallet_and_replays_on_the_same_key()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("c@example.com");
        await fx.FundWalletAsync(user, 1000, "pi_topup");
        using var factory = fx.Build();
        var client = Authed(factory);

        var first = await client.SendAsync(Json(HttpMethod.Post, "/v1/admin/refunds",
            new { user_id = user, amount_cents = 400, payment_intent = "pi_topup" }, "refund-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<RefundDto>();
        Assert.Equal(400, body!.AmountCents);
        Assert.Equal(600, body.BalanceCents);

        var second = await client.SendAsync(Json(HttpMethod.Post, "/v1/admin/refunds",
            new { user_id = user, amount_cents = 400, payment_intent = "pi_topup" }, "refund-1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(fx.Stripe.RefundCalls); // replayed, not refunded twice
    }

    [Fact]
    public async Task Adjustment_posts_a_balanced_txn()
    {
        var fx = new Fixture();
        var ledger = new LedgerService(fx.Ledger);
        var cash = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var revenue = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
        using var factory = fx.Build();
        var client = Authed(factory);

        var response = await client.SendAsync(Json(HttpMethod.Post, "/v1/admin/adjustments",
            new { debit_account_id = cash.Id, credit_account_id = revenue.Id, amount_cents = 250, reason = "fix" },
            "adj-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AdjustmentDto>();
        Assert.Equal(250, body!.AmountCents);
        Assert.Equal(250, await ledger.GetBalanceAsync(cash.Id));

        var audit = await fx.AuditLog.ListAsync(new AuditLogQuery { Action = "ledger.adjustment" });
        Assert.Single(audit);
    }

    [Fact]
    public async Task Ledger_forensics_returns_account_and_entries()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync("c@example.com");
        await fx.FundWalletAsync(user, 500, "pi_topup");
        var ledger = new LedgerService(fx.Ledger);
        var wallet = await ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user);
        using var factory = fx.Build();

        var forensics = await Authed(factory)
            .GetFromJsonAsync<ForensicsDto>($"/v1/admin/ledger/accounts/{wallet.Id}");

        Assert.Equal(wallet.Id, forensics!.Account.Id);
        Assert.Equal(500, forensics.Account.BalanceCents);
        Assert.NotEmpty(forensics.Entries);
    }

    private static HttpRequestMessage Json(HttpMethod method, string path, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private sealed record OverviewDto(
        [property: JsonPropertyName("revenue_cents")] long RevenueCents,
        [property: JsonPropertyName("host_count")] int HostCount,
        [property: JsonPropertyName("online_host_count")] int OnlineHostCount,
        [property: JsonPropertyName("user_count")] int UserCount);

    private sealed record PolicyHistoryDto(
        [property: JsonPropertyName("active")] PolicyDto? Active,
        [property: JsonPropertyName("versions")] IReadOnlyList<PolicyDto> Versions);

    private sealed record PolicyDto([property: JsonPropertyName("fee_bps")] int FeeBps);

    private sealed record UserPageDto([property: JsonPropertyName("data")] IReadOnlyList<UserDto> Data);

    private sealed record UserDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("email")] string Email);

    private sealed record AuditPageDto([property: JsonPropertyName("data")] IReadOnlyList<AuditDto> Data);

    private sealed record AuditDto(
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("target_id")] Guid? TargetId);

    private sealed record RefundDto(
        [property: JsonPropertyName("amount_cents")] long AmountCents,
        [property: JsonPropertyName("balance_cents")] long BalanceCents);

    private sealed record AdjustmentDto(
        [property: JsonPropertyName("amount_cents")] long AmountCents,
        [property: JsonPropertyName("transaction_id")] Guid TransactionId);

    private sealed record ForensicsDto(
        [property: JsonPropertyName("account")] AccountDto Account,
        [property: JsonPropertyName("entries")] IReadOnlyList<EntryDto> Entries);

    private sealed record AccountDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("balance_cents")] long BalanceCents);

    private sealed record EntryDto([property: JsonPropertyName("id")] long Id);
}
