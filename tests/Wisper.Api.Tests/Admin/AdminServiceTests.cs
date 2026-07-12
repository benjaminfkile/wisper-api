using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Admin;
using Wisper.Api.Audit;
using Wisper.Api.Billing;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Admin;

/// <summary>
/// Unit tests for <see cref="AdminService"/> (docs/API.md §8, P7.2) over the in-memory repositories + fakes
/// (Grunt has no Postgres/Stripe): the overview counts/revenue, versioned policy publish (audited),
/// host/user search + suspend/unsuspend (audited), the balanced ledger <c>adjustment</c> (the only
/// hand-correction, audited), manual refunds (actor = admin), the audit query, and ledger forensics. The
/// invariant under test throughout: <b>every admin write records an audit_log row</b>.
/// </summary>
public class AdminServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryAuditLogRepository AuditLog { get; } = new();
        public InMemoryPlatformPolicyRepository PolicyRepo { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public FakeStripeBillingGateway Stripe { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public LedgerService Ledger { get; }
        public AuditService Audit { get; }
        public AdminService Service { get; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Audit = new AuditService(AuditLog, Clock);
            var policy = new PlatformPolicyService(PolicyRepo, Clock);
            var fraud = new FraudGuardService(
                Ledger, Leases, policy, Clock, NullLogger<FraudGuardService>.Instance);
            var billing = new BillingService(
                Ledger, Leases, Users, policy, fraud, Audit, Stripe, Clock,
                NullLogger<BillingService>.Instance);
            Service = new AdminService(
                Users, Hosts, Leases, Ledger, policy, Audit, billing, Clock);
        }

        public async Task<Guid> SeedUserAsync(string email = "user@example.com")
        {
            var user = await Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = email,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            return user.Id;
        }

        public Task<Host> SeedHostAsync(Guid owner, string name = "home-server") =>
            Hosts.CreateAsync(new Host
            {
                OwnerUserId = owner,
                Name = name,
                Label = "us",
                Status = HostStatus.Offline,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });

        /// <summary>Funds <paramref name="userId"/>'s wallet from a top-up with a known PaymentIntent ref.</summary>
        public async Task FundWalletAsync(Guid userId, long amountCents, string paymentIntent = "pi_seed")
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, userId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, amountCents, 0, $"topup-{Guid.NewGuid():N}", externalRef: paymentIntent));
        }
    }

    // ---- overview ---------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_reports_counts_and_ledger_revenue()
    {
        var fx = new Fixture();
        var consumer = await fx.SeedUserAsync("c@example.com");
        await fx.SeedUserAsync("d@example.com");
        await fx.SeedHostAsync(consumer);

        // Accrue platform revenue: fund → hold → charge (fee 100¢, host 400¢).
        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        var holds = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
        var earnings = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, consumer);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
        await fx.FundWalletAsync(consumer, 500);
        var leaseId = Guid.NewGuid();
        await fx.Ledger.PostAsync(LedgerFlows.LeaseHold(wallet.Id, holds.Id, leaseId, 500));
        await fx.Ledger.PostAsync(LedgerFlows.LeaseCharge(holds.Id, earnings.Id, revenue.Id, leaseId, 500, 100));

        var overview = await fx.Service.GetOverviewAsync();

        Assert.Equal(100, overview.RevenueCents);
        Assert.Equal(400, overview.HostEarningsCents);
        Assert.Equal(2, overview.UserCount);
        Assert.Equal(1, overview.HostCount);
        Assert.Equal(0, overview.ActiveLeaseCount);
        Assert.Equal("ok", overview.Health);
    }

    // ---- policy -----------------------------------------------------------------------------------

    [Fact]
    public async Task Publish_policy_appends_a_version_and_audits()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");

        var published = await fx.Service.PublishPolicyAsync(
            admin, new PolicyUpdateRequest(FeeBps: 1500, MinTopupCents: 500));

        Assert.Equal(1500, published.FeeBps);
        Assert.Equal(admin, published.CreatedBy);

        var view = await fx.Service.GetPolicyAsync();
        Assert.NotNull(view.Active);
        Assert.Equal(1500, view.Active!.FeeBps);
        Assert.Single(view.Versions);

        var audit = await fx.AuditLog.ListAsync(new AuditLogQuery { Action = "policy.update" });
        var entry = Assert.Single(audit);
        Assert.Equal(admin, entry.ActorUserId);
    }

    [Fact]
    public async Task Publish_policy_rejects_an_out_of_range_fee()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.PublishPolicyAsync(admin, new PolicyUpdateRequest(FeeBps: 20_000)));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    // ---- moderation -------------------------------------------------------------------------------

    [Fact]
    public async Task Suspend_user_sets_status_and_audits_before_after()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var target = await fx.SeedUserAsync("t@example.com");

        var suspended = await fx.Service.SetUserSuspendedAsync(admin, target, suspend: true);
        Assert.Equal("suspended", suspended.Status);

        var reinstated = await fx.Service.SetUserSuspendedAsync(admin, target, suspend: false);
        Assert.Equal("active", reinstated.Status);

        var audit = await fx.AuditLog.ListByTargetAsync("user", target);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, a => a.Action == "user.suspend" && a.ActorUserId == admin);
        Assert.Contains(audit, a => a.Action == "user.unsuspend");
    }

    [Fact]
    public async Task Suspend_host_removes_it_from_online_and_audits()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner);

        var suspended = await fx.Service.SetHostSuspendedAsync(admin, host.Id, suspend: true);
        Assert.Equal("suspended", suspended.Status);

        var stored = await fx.Hosts.GetByIdAsync(host.Id);
        Assert.Equal(HostStatus.Suspended, stored!.Status);

        var audit = await fx.AuditLog.ListByTargetAsync("host", host.Id);
        Assert.Single(audit);
        Assert.Equal("host.suspend", audit[0].Action);
    }

    [Fact]
    public async Task Suspend_unknown_user_is_404()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.SetUserSuspendedAsync(admin, Guid.NewGuid(), suspend: true));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    // ---- search -----------------------------------------------------------------------------------

    [Fact]
    public async Task User_search_matches_email_and_paginates()
    {
        var fx = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            await fx.SeedUserAsync($"alice{i}@example.com");
        }

        await fx.SeedUserAsync("bob@example.com");

        var alices = await fx.Service.SearchUsersAsync("alice", limit: 2, offset: 0);
        Assert.Equal(2, alices.Data.Count);
        Assert.Equal(2, alices.NextOffset);
        Assert.All(alices.Data, u => Assert.Contains("alice", u.Email));

        var page2 = await fx.Service.SearchUsersAsync("alice", limit: 2, offset: 2);
        Assert.Single(page2.Data);
        Assert.Null(page2.NextOffset);
    }

    // ---- adjustments ------------------------------------------------------------------------------

    [Fact]
    public async Task Adjustment_posts_a_balanced_txn_and_audits()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

        var result = await fx.Service.AdjustAsync(
            admin,
            new AdjustmentRequest(cash.Id, revenue.Id, 250, "manual correction"),
            "adj-key-1");

        Assert.Equal(250, result.AmountCents);
        Assert.Equal(250, await fx.Ledger.GetBalanceAsync(cash.Id));
        Assert.Equal(250, await fx.Ledger.GetBalanceAsync(revenue.Id));

        // The posted transaction is a balanced `adjustment`.
        var txn = await fx.LedgerStore.FindTransactionByIdempotencyKeyAsync(
            AdminService.AdjustmentIdempotencyKey("adj-key-1"));
        Assert.NotNull(txn);
        Assert.Equal(LedgerTxnKind.Adjustment, txn!.Kind);

        var audit = await fx.AuditLog.ListAsync(new AuditLogQuery { Action = "ledger.adjustment" });
        var entry = Assert.Single(audit);
        Assert.Equal(admin, entry.ActorUserId);
        Assert.Equal(txn.Id, entry.TargetId);
    }

    [Fact]
    public async Task Adjustment_is_idempotent_on_the_key()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
        var request = new AdjustmentRequest(cash.Id, revenue.Id, 250, "correction");

        var first = await fx.Service.AdjustAsync(admin, request, "adj-key-1");
        var second = await fx.Service.AdjustAsync(admin, request, "adj-key-1");

        Assert.Equal(first.TransactionId, second.TransactionId);
        // Posted once: the balance did not move twice.
        Assert.Equal(250, await fx.Ledger.GetBalanceAsync(cash.Id));
    }

    [Fact]
    public async Task Adjustment_that_overdraws_a_wallet_is_insufficient_funds()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();
        var user = await fx.SeedUserAsync("poor@example.com");
        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.AdjustAsync(admin, new AdjustmentRequest(wallet.Id, revenue.Id, 100, "clawback"), "k"));
        Assert.Equal(ApiErrorCode.InsufficientFunds, ex.Code);
    }

    [Fact]
    public async Task Adjustment_between_identical_accounts_is_validation_error()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.AdjustAsync(admin, new AdjustmentRequest(cash.Id, cash.Id, 100, null), "k"));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Adjustment_against_unknown_account_is_404()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.AdjustAsync(admin, new AdjustmentRequest(cash.Id, Guid.NewGuid(), 100, null), "k"));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    // ---- refunds ----------------------------------------------------------------------------------

    [Fact]
    public async Task Refund_debits_the_target_wallet_and_audits_the_admin_as_actor()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var user = await fx.SeedUserAsync("consumer@example.com");
        await fx.FundWalletAsync(user, 1000, "pi_topup");

        var result = await fx.Service.RefundAsync(
            admin, new AdminRefundRequest(user, 400, "pi_topup", "goodwill"), "refund-key-1");

        Assert.Equal(400, result.AmountCents);
        Assert.Equal(600, result.BalanceCents);
        Assert.Single(fx.Stripe.RefundCalls);

        var audit = await fx.AuditLog.ListByTargetAsync("user", user);
        var entry = Assert.Single(audit);
        Assert.Equal("admin.refund", entry.Action);
        Assert.Equal(admin, entry.ActorUserId);
    }

    [Fact]
    public async Task Refund_without_a_user_id_is_validation_error()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.RefundAsync(admin, new AdminRefundRequest(null, 100), "k"));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    // ---- audit query + forensics ------------------------------------------------------------------

    [Fact]
    public async Task Audit_query_filters_by_action()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var user = await fx.SeedUserAsync("t@example.com");
        await fx.Service.SetUserSuspendedAsync(admin, user, suspend: true);
        await fx.Service.PublishPolicyAsync(admin, new PolicyUpdateRequest(FeeBps: 1000));

        var suspends = await fx.Service.QueryAuditAsync(new AuditLogQuery { Action = "user.suspend" });
        Assert.Single(suspends);

        var byActor = await fx.Service.QueryAuditAsync(new AuditLogQuery { ActorUserId = admin });
        Assert.Equal(2, byActor.Count);
    }

    [Fact]
    public async Task Ledger_forensics_returns_the_account_and_its_entries()
    {
        var fx = new Fixture();
        var user = await fx.SeedUserAsync();
        await fx.FundWalletAsync(user, 500);
        var wallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user);

        var forensics = await fx.Service.GetLedgerAccountAsync(wallet.Id);

        Assert.Equal(wallet.Id, forensics.Account.Id);
        Assert.Equal("user_wallet", forensics.Account.Kind);
        Assert.Equal(500, forensics.Account.BalanceCents);
        Assert.NotEmpty(forensics.Entries);
    }

    [Fact]
    public async Task Ledger_forensics_for_unknown_account_is_404()
    {
        var fx = new Fixture();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.GetLedgerAccountAsync(Guid.NewGuid()));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }
}
