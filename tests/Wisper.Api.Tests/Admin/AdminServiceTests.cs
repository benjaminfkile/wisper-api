using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Admin;
using Wisper.Api.Audit;
using Wisper.Api.Billing;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
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

    /// <summary>Parses the wire <c>lease_&lt;guid&gt;</c> token back to its Guid for lease-set assertions.</summary>
    private static Guid ParseLease(string token) =>
        TunnelLeaseId.TryParse(token, out var id) ? id : throw new InvalidOperationException($"bad lease id: {token}");

    private sealed class Fixture
    {
        public InMemoryUserRepository Users { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLeaseUsageRepository Usage { get; } = new();
        public InMemoryAuditLogRepository AuditLog { get; } = new();
        public InMemoryPlatformPolicyRepository PolicyRepo { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public FakeStripeBillingGateway Stripe { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public LedgerService Ledger { get; }
        public AuditService Audit { get; }
        public MeteringService Meter { get; }
        public WalletLeaseGate WalletGate { get; }
        public LedgerReconcileMonitor ReconcileMonitor { get; } = new();
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
            Meter = new MeteringService(
                Leases, Usage, Hosts, Ledger, policy, Clock, NullLogger<MeteringService>.Instance);
            WalletGate = new WalletLeaseGate(
                Ledger, Leases, policy, fraud, NullLogger<WalletLeaseGate>.Instance);
            Service = new AdminService(
                Users, Hosts, Leases, Ledger, policy, Audit, billing,
                Meter, WalletGate, Relay, ReconcileMonitor, Clock, NullLogger<AdminService>.Instance);
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

        /// <summary>
        /// Seeds a lease in <paramref name="status"/> with a funded up-front hold sized against a 1-hour TTL
        /// at 60¢/min — the readable price where 1s = 1¢. Also publishes the active policy so charges land.
        /// Returns the persisted <see cref="Lease"/>.
        /// </summary>
        public async Task<Lease> SeedLeaseAsync(
            Guid consumerId,
            Guid hostId,
            LeaseStatus status,
            DateTimeOffset startedAt,
            int ttlSeconds = 3600,
            long priceCentsPerMin = 60,
            long billableSeconds = 0,
            DateTimeOffset? lastMeteredAt = null,
            DateTimeOffset? suspendedAt = null)
        {
            // Publish an active policy so metering can compute the fee split when finalize charges the tail.
            if (await PolicyRepo.GetActiveAsync(T0) is null)
            {
                await PolicyRepo.AppendAsync(new PlatformPolicy { FeeBps = 1500, EffectiveFrom = T0 });
            }

            var holdCents = LeaseHoldPricing.EstimateHoldCents(ttlSeconds, priceCentsPerMin);
            await FundWalletAsync(consumerId, holdCents, $"pi_{Guid.NewGuid():N}");

            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumerId);
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);

            var leaseId = Guid.NewGuid();
            var posted = await Ledger.PostAsync(LedgerFlows.LeaseHold(
                wallet.Id, holds.Id, leaseId, holdCents, idempotencyKey: WalletLeaseGate.HoldIdempotencyKey(leaseId)));

            var lease = await Leases.CreateAsync(new Lease
            {
                Id = leaseId,
                ConsumerUserId = consumerId,
                HostId = hostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = ttlSeconds,
                PriceCentsPerMin = priceCentsPerMin,
                Currency = "usd",
                Status = status,
                HoldTxnId = posted.Transaction.Id,
                CreatedAt = startedAt,
                StartedAt = startedAt,
                LastMeteredAt = lastMeteredAt ?? startedAt,
                BillableSeconds = billableSeconds,
                SuspendedAt = suspendedAt,
            });
            return lease;
        }

        /// <summary>Signed net amount posted against <c>lease_holds</c> for a single lease id.</summary>
        public async Task<long> HoldsCentsForAsync(Guid leaseId)
        {
            var holds = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var entries = await Ledger.ListEntriesForAccountAsync(holds.Id);
            long sum = 0;
            foreach (var entry in entries.Where(e => e.LeaseId == leaseId))
            {
                // lease_holds is credit-normal: credits grow the earmark, debits shrink it.
                sum += entry.CreditCents - entry.DebitCents;
            }
            return sum;
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
        // Default before any reconcile pass has run: empty signal, no drift, no timestamp.
        Assert.Null(overview.LedgerReconcile.RanAt);
        Assert.False(overview.LedgerReconcile.HasDrift);
        Assert.Equal(0, overview.LedgerReconcile.DriftAccountCount);
    }

    [Fact]
    public async Task Overview_surfaces_ledger_drift_from_the_reconcile_monitor()
    {
        // Task #183: the admin overview must expose drift the scheduled reconciler observed, so an
        // operator sees the incident without waiting for the next log line.
        var fx = new Fixture();
        fx.ReconcileMonitor.Record(new LedgerReconcileSummary(
            RanAt: T0.AddMinutes(-1), AccountsChecked: 12, DriftAccountCount: 2,
            TotalAbsoluteDriftCents: 137));

        var overview = await fx.Service.GetOverviewAsync();

        Assert.Equal(T0.AddMinutes(-1), overview.LedgerReconcile.RanAt);
        Assert.Equal(12, overview.LedgerReconcile.AccountsChecked);
        Assert.Equal(2, overview.LedgerReconcile.DriftAccountCount);
        Assert.Equal(137, overview.LedgerReconcile.TotalAbsoluteDriftCents);
        Assert.True(overview.LedgerReconcile.HasDrift);
        Assert.Equal("ledger_drift", overview.Health);
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

    // ---- admin force-end (task #57) --------------------------------------------------------------

    [Fact]
    public async Task Force_end_on_an_active_lease_finalizes_billing_ends_admin_releases_hold_and_relays_release()
    {
        // Acceptance criterion #189: active lease with a connected host — billing finalized, ended(admin),
        // hold released, release relayed to the host.
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var consumer = await fx.SeedUserAsync("c@example.com");
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner);
        var lease = await fx.SeedLeaseAsync(consumer, host.Id, LeaseStatus.Active, T0);

        // 90 seconds of healthy runtime, then the admin force-ends the lease.
        fx.Clock.Advance(TimeSpan.FromSeconds(90));

        var view = await fx.Service.ForceEndLeaseAsync(admin, lease.Id);

        // Wire view reflects ended(admin) with the metered tail flushed.
        Assert.Equal("ended", view.Status);
        Assert.Equal("admin", view.EndReason);

        // The row itself is terminal, and billing was finalized to the full 90-second healthy interval —
        // FinalizeLeaseAsync ran with a null liveness source, so `now` was the cap (task #54).
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Admin, stored.EndReason);
        Assert.Equal(T0.AddSeconds(90), stored.EndedAt);
        Assert.Equal(90, stored.BillableSeconds);

        // Hold released: net earmark for this lease against lease_holds is zero (hold posted, then
        // charge + release drained it). The wallet balance is restored minus the charged 90¢.
        Assert.Equal(0, await fx.HoldsCentsForAsync(lease.Id));
        var walletAcct = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        // Wallet started at 3600¢ (the hold cost), was drained to place the hold, then received
        // (hold - charge) = 3510¢ back on release.
        Assert.Equal(3510, walletAcct.BalanceCents);

        // Release relayed to the host — the fake relay recorded exactly one lease.release for this lease.
        var call = Assert.Single(fx.Relay.ReleaseCalls);
        Assert.Equal(host.Id.ToString(), call.HostId);
        Assert.Equal(TunnelLeaseId.Format(lease.Id), call.LeaseId);

        // Audit trail carries the acting admin + before/after.
        var audit = await fx.AuditLog.ListByTargetAsync("lease", lease.Id);
        var entry = Assert.Single(audit);
        Assert.Equal("lease.admin_end", entry.Action);
        Assert.Equal(admin, entry.ActorUserId);
    }

    [Fact]
    public async Task Force_end_on_a_suspended_lease_succeeds_without_a_live_host_and_releases_the_hold()
    {
        // Acceptance criterion #190: suspended lease with no connected host — succeeds, finalizes to
        // last-healthy watermark (already flushed at suspend), releases hold. wisp's TTL reaper covers the
        // container so the tunnel notify is a no-op when the relay reports host_offline.
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var consumer = await fx.SeedUserAsync("c@example.com");
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner);

        // Suspended at T0+60 with 60s already billed (mirrors the SuspendHostLeases path).
        var lease = await fx.SeedLeaseAsync(
            consumer, host.Id, LeaseStatus.Suspended, T0,
            billableSeconds: 60, lastMeteredAt: T0.AddSeconds(60), suspendedAt: T0.AddSeconds(60));

        // No live tunnel: the relay throws host_offline. The admin end must not fail on this.
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.HostOffline, "no live tunnel");

        // 5 minutes later, admin ends it. The lease was already flushed at last-healthy at suspend time,
        // so no additional meter tick runs and billable_seconds does not change.
        fx.Clock.Advance(TimeSpan.FromMinutes(5));
        var view = await fx.Service.ForceEndLeaseAsync(admin, lease.Id);

        Assert.Equal("ended", view.Status);
        Assert.Equal("admin", view.EndReason);

        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Admin, stored.EndReason);
        Assert.Equal(60, stored.BillableSeconds); // pinned at last-healthy — the gap never billed

        // Hold released: the wallet gets back exactly (hold - charged-cents-so-far) = 3600 - 60 = 3540¢
        // (a suspended lease was already flushed at suspend time, so no fresh finalize runs here). Wallet
        // started at 3600¢ (funding), was drained by the hold post, then this release credits 3540¢ back.
        var walletAcct = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        Assert.Equal(3540, walletAcct.BalanceCents);

        // Tunnel notify was attempted (best-effort) and the host_offline was swallowed — the operation
        // still succeeded. Recorded once so we can prove we did try.
        Assert.Single(fx.Relay.ReleaseCalls);
    }

    [Fact]
    public async Task Force_end_on_an_already_ended_lease_is_idempotent_no_op_no_double_release()
    {
        // Acceptance criterion #191: an already-ended lease is a no-op — no double hold release, no
        // duplicate audit row, no fresh tunnel notify.
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");
        var consumer = await fx.SeedUserAsync("c@example.com");
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner);
        var lease = await fx.SeedLeaseAsync(consumer, host.Id, LeaseStatus.Active, T0);

        fx.Clock.Advance(TimeSpan.FromSeconds(60));

        // First end: does the full flush + release + notify.
        await fx.Service.ForceEndLeaseAsync(admin, lease.Id);
        var walletAfterFirst = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        var walletBalanceAfterFirst = walletAfterFirst.BalanceCents;
        var relayCallsAfterFirst = fx.Relay.ReleaseCalls.Count;
        var auditAfterFirst = (await fx.AuditLog.ListByTargetAsync("lease", lease.Id)).Count;

        // Second end: no wallet movement, no fresh relay call, no fresh audit row.
        fx.Clock.Advance(TimeSpan.FromSeconds(60));
        var replay = await fx.Service.ForceEndLeaseAsync(admin, lease.Id);
        Assert.Equal("ended", replay.Status);
        Assert.Equal("admin", replay.EndReason);

        var walletAfterSecond = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, consumer);
        Assert.Equal(walletBalanceAfterFirst, walletAfterSecond.BalanceCents);
        Assert.Equal(relayCallsAfterFirst, fx.Relay.ReleaseCalls.Count);
        Assert.Equal(auditAfterFirst, (await fx.AuditLog.ListByTargetAsync("lease", lease.Id)).Count);
    }

    [Fact]
    public async Task Force_end_on_an_unknown_lease_is_404()
    {
        var fx = new Fixture();
        var admin = await fx.SeedUserAsync("admin@example.com");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service.ForceEndLeaseAsync(admin, Guid.NewGuid()));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    // ---- admin lease listing (task #57) ----------------------------------------------------------

    [Fact]
    public async Task List_leases_surfaces_suspended_and_past_ttl_active_and_excludes_terminal()
    {
        // Acceptance criterion #192: the admin listing surfaces suspended AND past-TTL active leases so
        // an operator can find stuck leases without SQL. Terminal (ended/failed) rows are excluded.
        var fx = new Fixture();
        var consumer = await fx.SeedUserAsync("c@example.com");
        var owner = await fx.SeedUserAsync("owner@example.com");
        var host = await fx.SeedHostAsync(owner);

        // A fresh active lease (well within its 1h TTL) — will NOT appear under past_ttl=true.
        var freshActive = await fx.SeedLeaseAsync(consumer, host.Id, LeaseStatus.Active, T0);
        // An active lease that started 3 hours ago — past its 1h TTL.
        var pastTtlActive = await fx.SeedLeaseAsync(
            consumer, host.Id, LeaseStatus.Active, T0.AddHours(-3));
        // A suspended lease still within grace.
        var suspended = await fx.SeedLeaseAsync(
            consumer, host.Id, LeaseStatus.Suspended, T0.AddMinutes(-30),
            billableSeconds: 60, lastMeteredAt: T0.AddMinutes(-30).AddSeconds(60),
            suspendedAt: T0.AddMinutes(-30).AddSeconds(60));

        // A terminal (ended) lease that must NOT appear on the listing.
        var ended = await fx.SeedLeaseAsync(consumer, host.Id, LeaseStatus.Active, T0.AddHours(-2));
        await fx.Leases.TransitionStateAsync(
            ended.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0.AddHours(-1));

        // Default listing: every non-terminal lease (active + suspended), oldest first.
        var all = await fx.Service.ListLeasesAsync(status: null, pastTtl: false, limit: 50, offset: 0);
        var allIds = all.Data.Select(v => ParseLease(v.Id)).ToHashSet();
        Assert.Equal(3, all.Data.Count);
        Assert.Contains(freshActive.Id, allIds);
        Assert.Contains(pastTtlActive.Id, allIds);
        Assert.Contains(suspended.Id, allIds);
        Assert.DoesNotContain(ended.Id, allIds);

        // status=suspended narrows to just the suspended lease.
        var suspendedOnly = await fx.Service.ListLeasesAsync(
            status: LeaseStatus.Suspended, pastTtl: false, limit: 50, offset: 0);
        var only = Assert.Single(suspendedOnly.Data);
        Assert.Equal(suspended.Id, ParseLease(only.Id));

        // past_ttl=true surfaces exactly the leases whose started_at + ttl has elapsed on the fake clock —
        // both the past-TTL active AND (given seed data) the suspended row that was seeded 30m ago with a
        // 1h TTL is still within TTL, so it must NOT be included.
        var pastTtlOnly = await fx.Service.ListLeasesAsync(
            status: null, pastTtl: true, limit: 50, offset: 0);
        var pastIds = pastTtlOnly.Data.Select(v => ParseLease(v.Id)).ToHashSet();
        Assert.Contains(pastTtlActive.Id, pastIds);
        Assert.DoesNotContain(freshActive.Id, pastIds);
        Assert.DoesNotContain(suspended.Id, pastIds);
    }

    // ---- admin ledger-account listing (task #194) ------------------------------------------------

    [Fact]
    public async Task List_ledger_accounts_filters_by_kind()
    {
        // An operator finding the platform_revenue singleton (the credit-side of an adjustment) must be
        // able to narrow the listing to one kind and skip past every user wallet.
        var fx = new Fixture();
        var alice = await fx.SeedUserAsync("alice@example.com");
        var bob = await fx.SeedUserAsync("bob@example.com");
        // Force both user wallets to exist.
        _ = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, alice);
        _ = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, bob);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

        var page = await fx.Service.ListLedgerAccountsAsync(
            LedgerAccountKind.PlatformRevenue, ownerUserId: null, limit: 50, offset: 0);

        var row = Assert.Single(page.Data);
        Assert.Equal(revenue.Id, row.Id);
        Assert.Equal("platform_revenue", row.Kind);
        Assert.Null(row.OwnerUserId);
        Assert.Null(row.OwnerEmail);
        Assert.Equal("usd", row.Currency);
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public async Task List_ledger_accounts_filters_by_owner_user_id_and_joins_email()
    {
        // Finding Alice's wallet by her user id: the row's owner_user_id and owner_email must both be
        // populated, and Bob's wallet must not appear.
        var fx = new Fixture();
        var alice = await fx.SeedUserAsync("alice@example.com");
        var bob = await fx.SeedUserAsync("bob@example.com");
        var aliceWallet = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, alice);
        _ = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, bob);
        _ = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);
        // Fund Alice so the joined balance carries a non-zero value the listing can display.
        await fx.FundWalletAsync(alice, 750);

        var page = await fx.Service.ListLedgerAccountsAsync(
            kind: null, ownerUserId: alice, limit: 50, offset: 0);

        var row = Assert.Single(page.Data);
        Assert.Equal(aliceWallet.Id, row.Id);
        Assert.Equal("user_wallet", row.Kind);
        Assert.Equal(alice, row.OwnerUserId);
        Assert.Equal("alice@example.com", row.OwnerEmail);
        Assert.Equal(750, row.BalanceCents);
    }

    [Fact]
    public async Task List_ledger_accounts_paginates_with_next_offset_and_stops_at_last_page()
    {
        // Paging discipline: over-fetch one to know whether another page exists; the last page reports
        // next_offset = null. Order is stable (created_at, id) so paging never dupes or skips.
        var fx = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            var user = await fx.SeedUserAsync($"consumer{i}@example.com");
            _ = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, user);
        }

        // Page 1: two wallets, more to come.
        var page1 = await fx.Service.ListLedgerAccountsAsync(
            LedgerAccountKind.UserWallet, ownerUserId: null, limit: 2, offset: 0);
        Assert.Equal(2, page1.Data.Count);
        Assert.Equal(2, page1.NextOffset);

        // Page 2: the last wallet, next_offset null → done.
        var page2 = await fx.Service.ListLedgerAccountsAsync(
            LedgerAccountKind.UserWallet, ownerUserId: null, limit: 2, offset: 2);
        Assert.Single(page2.Data);
        Assert.Null(page2.NextOffset);

        // No duplicated ids across the two pages.
        var seen = new HashSet<Guid>(page1.Data.Select(a => a.Id));
        foreach (var row in page2.Data)
        {
            Assert.True(seen.Add(row.Id));
        }
    }

    [Fact]
    public async Task List_ledger_accounts_returns_platform_singletons_with_null_owner_email()
    {
        // The two platform singletons (owner NULL) needed to build an adjustment appear on an unfiltered
        // listing and carry null owner_user_id + owner_email.
        var fx = new Fixture();
        var cash = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
        var revenue = await fx.Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformRevenue, null);

        var page = await fx.Service.ListLedgerAccountsAsync(
            kind: null, ownerUserId: null, limit: 50, offset: 0);

        Assert.Equal(2, page.Data.Count);
        Assert.All(page.Data, a => Assert.Null(a.OwnerUserId));
        Assert.All(page.Data, a => Assert.Null(a.OwnerEmail));
        Assert.Contains(page.Data, a => a.Id == cash.Id);
        Assert.Contains(page.Data, a => a.Id == revenue.Id);
    }
}
