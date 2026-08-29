using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Audit;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Payments;
using Wisper.Api.Payouts;
using Wisper.Api.Persistence.Audit;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Payouts;

/// <summary>
/// Unit tests for <see cref="PayoutService"/> (docs/API.md §6, docs/PAYMENTS.md §6) over the in-memory ledger
/// + payout/user repositories and a fake Connect gateway (Grunt has no Stripe/Postgres). Covers: the scheduled
/// + on-demand run draining host_earnings via a Transfer (with a payouts row + `payout` ledger txn), the
/// idempotency key = payouts.id, the minimum + connect gates, and the failed-transfer path retaining earnings.
/// </summary>
public class PayoutServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public LedgerService Ledger { get; }
        public InMemoryPayoutRepository Payouts { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public FakeStripeConnectGateway Gateway { get; } = new();
        public InMemoryAuditLogRepository AuditLog { get; } = new();
        public AuditService Audit { get; }
        public FakeTimeProvider Clock { get; } = new(T0);
        public PayoutOptions Options { get; } = new() { PayoutMinCents = 100 };
        public PayoutService Service { get; }

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Audit = new AuditService(AuditLog, Clock);
            Service = new PayoutService(
                Ledger, Payouts, Users, Gateway, Audit,
                Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<PayoutService>.Instance);
        }

        public Task<User> SeedHostAsync(
            ConnectStatus connectStatus = ConnectStatus.Enabled, string? accountId = "acct_host") =>
            Users.CreateAsync(new User
            {
                CognitoSub = $"sub-{Guid.NewGuid():N}",
                Email = "host@example.com",
                Status = UserStatus.Active,
                ConnectAccountId = accountId,
                ConnectStatus = connectStatus,
                CreatedAt = T0,
                UpdatedAt = T0,
            });

        /// <summary>Accrues <paramref name="amountCents"/> into the host's host_earnings via topup → hold → charge (fee 0).</summary>
        public async Task AccrueEarningsAsync(Guid hostUserId, long amountCents)
        {
            var consumer = Guid.NewGuid();
            var leaseId = Guid.NewGuid();
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
            var platformCash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var stripeFees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var earnings = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.HostEarnings, hostUserId);
            var revenue = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, platformCash.Id, stripeFees.Id, amountCents, 0, $"topup-{Guid.NewGuid():N}"));
            await Ledger.PostAsync(LedgerFlows.LeaseHold(wallet.Id, holds.Id, leaseId, amountCents));
            // fee 0 ⇒ the whole charge lands in host_earnings.
            await Ledger.PostAsync(LedgerFlows.LeaseCharge(
                holds.Id, earnings.Id, revenue.Id, leaseId, amountCents, 0));
        }

        public Task<long> EarningsAsync(Guid hostUserId) => Ledger.GetHostEarningsCentsAsync(hostUserId);
    }

    [Fact]
    public async Task Scheduled_run_drains_earnings_via_transfer_with_payout_row_and_ledger_txn()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);

        var paid = await fx.Service.RunScheduledPayoutsAsync();

        Assert.Equal(1, paid);
        // host_earnings drained to zero (the payout ledger txn debited it).
        Assert.Equal(0, await fx.EarningsAsync(host.Id));

        var payouts = await fx.Payouts.ListByHostAsync(host.Id);
        var payout = Assert.Single(payouts);
        Assert.Equal(500, payout.AmountCents);
        Assert.Equal(PayoutStatus.InTransit, payout.Status);
        Assert.NotNull(payout.StripeTransferId);
        Assert.NotNull(payout.PayoutTxnId);

        // The Transfer carried the payouts.id as its idempotency key + destination account (docs/PAYMENTS.md §6).
        var call = Assert.Single(fx.Gateway.TransferCalls);
        Assert.Equal(payout.Id.ToString(), call.IdempotencyKey);
        Assert.Equal("acct_host", call.ConnectAccountId);
        Assert.Equal(500, call.AmountCents);
    }

    [Fact]
    public async Task Scheduled_run_skips_hosts_below_minimum()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 99); // below the 100¢ minimum

        var paid = await fx.Service.RunScheduledPayoutsAsync();

        Assert.Equal(0, paid);
        Assert.Empty(fx.Gateway.TransferCalls);
        Assert.Equal(99, await fx.EarningsAsync(host.Id)); // retained
    }

    [Fact]
    public async Task Scheduled_run_holds_payouts_for_non_enabled_connect()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.Restricted);
        await fx.AccrueEarningsAsync(host.Id, 500);

        var paid = await fx.Service.RunScheduledPayoutsAsync();

        Assert.Equal(0, paid);
        Assert.Empty(fx.Gateway.TransferCalls);
        Assert.Equal(500, await fx.EarningsAsync(host.Id)); // earnings keep accruing, none lost
    }

    [Fact]
    public async Task On_demand_payout_drains_earnings()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 750);

        var payout = await fx.Service.PayoutOnDemandAsync(host.Id);

        Assert.Equal(750, payout.AmountCents);
        Assert.Equal(PayoutStatus.InTransit, payout.Status);
        Assert.Equal(0, await fx.EarningsAsync(host.Id));
        Assert.Single(fx.Gateway.TransferCalls);
    }

    [Fact]
    public async Task On_demand_payout_rejects_incomplete_connect_with_403()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(ConnectStatus.Pending);
        await fx.AccrueEarningsAsync(host.Id, 500);

        var ex = await Assert.ThrowsAsync<ApiException>(() => fx.Service.PayoutOnDemandAsync(host.Id));

        Assert.Equal(ApiErrorCode.ConnectIncomplete, ex.Code);
        Assert.Empty(fx.Gateway.TransferCalls);
        Assert.Equal(500, await fx.EarningsAsync(host.Id));
    }

    [Fact]
    public async Task On_demand_payout_rejects_below_minimum()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 50);

        var ex = await Assert.ThrowsAsync<ApiException>(() => fx.Service.PayoutOnDemandAsync(host.Id));

        Assert.Equal(ApiErrorCode.PaymentRequired, ex.Code);
        Assert.Empty(fx.Gateway.TransferCalls);
    }

    [Fact]
    public async Task Failed_transfer_records_failed_and_retains_earnings_without_a_ledger_txn()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);
        fx.Gateway.TransferError = new InvalidOperationException("insufficient platform balance");

        var payout = await fx.Service.PayoutOnDemandAsync(host.Id);

        Assert.Equal(PayoutStatus.Failed, payout.Status);
        Assert.Null(payout.PayoutTxnId);                 // no ledger txn posted
        Assert.Equal(500, await fx.EarningsAsync(host.Id)); // earnings retained
        Assert.Contains("insufficient platform balance", payout.Error);
    }

    [Fact]
    public async Task Second_scheduled_run_does_not_double_pay_after_the_first_drained_earnings()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);

        Assert.Equal(1, await fx.Service.RunScheduledPayoutsAsync());
        // Nothing left to pay — the balance is zero, below the minimum.
        Assert.Equal(0, await fx.Service.RunScheduledPayoutsAsync());

        Assert.Single(await fx.Payouts.ListByHostAsync(host.Id));
        Assert.Single(fx.Gateway.TransferCalls);
    }

    [Fact]
    public async Task Earnings_report_shows_accrued_then_paid_after_a_payout()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);

        var before = await fx.Service.GetEarningsAsync(host.Id);
        Assert.Equal(500, before.AccruedCents);
        Assert.Equal(0, before.PaidCents);
        Assert.True(before.CanReceivePayouts);
        Assert.Equal("enabled", before.ConnectStatus);

        await fx.Service.PayoutOnDemandAsync(host.Id);

        var after = await fx.Service.GetEarningsAsync(host.Id);
        Assert.Equal(0, after.AccruedCents);
        Assert.Equal(500, after.PaidCents);
    }

    [Fact]
    public async Task Payout_history_lists_the_row()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);
        await fx.Service.PayoutOnDemandAsync(host.Id);

        var history = await fx.Service.ListPayoutsAsync(host.Id);

        var view = Assert.Single(history.Data);
        Assert.Equal(500, view.AmountCents);
        Assert.Equal("in_transit", view.Status);
        Assert.NotNull(view.StripeTransferId);
    }

    [Fact]
    public async Task Scheduled_run_records_a_payout_settled_audit_row_with_system_actor()
    {
        // Task #185: scheduled payouts must leave the same audit trail as refunds/chargebacks. The actor
        // is null (system), target is the host user, and the meta carries the amounts and Stripe transfer id.
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);

        await fx.Service.RunScheduledPayoutsAsync();

        var rows = await fx.AuditLog.ListByTargetAsync("user", host.Id);
        var entry = Assert.Single(rows);
        Assert.Equal("payout.settled", entry.Action);
        Assert.Null(entry.ActorUserId);
        Assert.NotNull(entry.Meta);
        Assert.Contains("\"amount_cents\":500", entry.Meta!);
        Assert.Contains("\"currency\":\"usd\"", entry.Meta!);
        Assert.Contains("\"stripe_transfer_id\":", entry.Meta!);
        Assert.Contains("\"trigger\":\"scheduled\"", entry.Meta!);
    }

    [Fact]
    public async Task On_demand_payout_records_the_host_as_the_audit_actor()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 750);

        await fx.Service.PayoutOnDemandAsync(host.Id);

        var rows = await fx.AuditLog.ListByTargetAsync("user", host.Id);
        var entry = Assert.Single(rows);
        Assert.Equal("payout.settled", entry.Action);
        Assert.Equal(host.Id, entry.ActorUserId);
        Assert.Contains("\"trigger\":\"on_demand\"", entry.Meta!);
    }

    [Fact]
    public async Task Failed_transfer_records_a_payout_failed_audit_row_and_retains_earnings()
    {
        // A failed transfer must still leave an audit row; an operator needs to see the attempt and the
        // error even when no money moved (host_earnings is retained and the next run will retry).
        var fx = new Fixture();
        var host = await fx.SeedHostAsync();
        await fx.AccrueEarningsAsync(host.Id, 500);
        fx.Gateway.TransferError = new InvalidOperationException("insufficient platform balance");

        await fx.Service.PayoutOnDemandAsync(host.Id);

        var rows = await fx.AuditLog.ListByTargetAsync("user", host.Id);
        var entry = Assert.Single(rows);
        Assert.Equal("payout.failed", entry.Action);
        Assert.Equal(host.Id, entry.ActorUserId);
        Assert.NotNull(entry.Meta);
        Assert.Contains("insufficient platform balance", entry.Meta!);
        Assert.Contains("\"amount_cents\":500", entry.Meta!);
    }
}
