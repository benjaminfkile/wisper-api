using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Policy;

/// <summary>
/// Unit tests for the day-one fraud guards (docs/PAYMENTS.md §7, §13) over the in-memory ledger + repos
/// (Grunt has no Postgres). Covers all three <c>platform_policy</c> knobs enforced at top-up / lease start:
/// the first-top-up hold, the new-account top-up velocity, and the per-user daily spend cap — each a no-op
/// when unset or when no policy is configured, and a <c>limit_exceeded</c> (429) on breach.
/// </summary>
public class FraudGuardServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public LedgerService Ledger { get; }
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public PlatformPolicyService Policy { get; }
        public FraudGuardService Guard { get; }

        public Guid UserId { get; } = Guid.NewGuid();

        public Fixture()
        {
            Ledger = new LedgerService(LedgerStore);
            Policy = new PlatformPolicyService(Policies, Clock);
            Guard = new FraudGuardService(
                Ledger, Leases, Policy, Clock, NullLogger<FraudGuardService>.Instance);
        }

        public Task PublishAsync(PlatformPolicy policy) =>
            Policy.PublishAsync(policy with { EffectiveFrom = T0 });

        public User NewUser(DateTimeOffset createdAt) => new()
        {
            Id = UserId,
            CognitoSub = $"sub-{Guid.NewGuid():N}",
            Email = "consumer@example.com",
            Status = UserStatus.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

        // Credit the wallet with a top-up ledger txn (the history the top-up guards read).
        public async Task SeedTopupAsync(long cents)
        {
            var wallet = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, UserId);
            var cash = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await Ledger.GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            await Ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: cents, stripeFeeCents: 0,
                idempotencyKey: $"topup:{Guid.NewGuid():N}"));
        }

        public Task SeedLeaseAsync(int ttlSeconds, long priceCentsPerMin, DateTimeOffset createdAt) =>
            Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = UserId,
                HostId = Guid.NewGuid(),
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = ttlSeconds,
                PriceCentsPerMin = priceCentsPerMin,
                Currency = "usd",
                Status = LeaseStatus.Active,
                CreatedAt = createdAt,
                StartedAt = createdAt,
            });
    }

    // ---- first-top-up hold ------------------------------------------------------------------------

    [Fact]
    public async Task First_topup_above_the_cap_is_limit_exceeded()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500, FirstTopupMaxCents = 5000 });

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 6000));

        Assert.Equal(ApiErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public async Task First_topup_at_the_cap_is_allowed()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500, FirstTopupMaxCents = 5000 });

        await fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 5000); // no throw
    }

    [Fact]
    public async Task First_topup_cap_only_applies_to_the_very_first_topup()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500, FirstTopupMaxCents = 5000 });
        await fx.SeedTopupAsync(5000); // a top-up already exists

        // A later top-up is not capped by the first-top-up hold.
        await fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 9000); // no throw
    }

    // ---- new-account top-up velocity --------------------------------------------------------------

    [Fact]
    public async Task New_account_topup_velocity_blocks_over_the_daily_cap()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy
        {
            FeeBps = 1500,
            NewAccountWindowHours = 24,
            NewAccountMaxTopupCentsPerDay = 10000,
        });
        await fx.SeedTopupAsync(6000); // already funded 6000 in the window

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 5000)); // 6000 + 5000 > 10000

        Assert.Equal(ApiErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public async Task New_account_velocity_does_not_apply_to_an_old_account()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy
        {
            FeeBps = 1500,
            NewAccountWindowHours = 24,
            NewAccountMaxTopupCentsPerDay = 10000,
        });
        await fx.SeedTopupAsync(6000);

        // Account created 48h ago — past the new-account window, so the velocity cap does not apply.
        await fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0 - TimeSpan.FromHours(48)), 9000); // no throw
    }

    // ---- per-user daily spend cap -----------------------------------------------------------------

    [Fact]
    public async Task Spend_cap_blocks_when_committed_holds_plus_new_hold_exceed_it()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500, MaxSpendCentsPerDay = 10000 });
        // Two leases, each a 60s @ 4000¢/min lease → hold 4000 apiece = 8000 committed in the window.
        await fx.SeedLeaseAsync(ttlSeconds: 60, priceCentsPerMin: 4000, createdAt: T0);
        await fx.SeedLeaseAsync(ttlSeconds: 60, priceCentsPerMin: 4000, createdAt: T0);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Guard.EnforceLeaseSpendAllowedAsync(fx.UserId, projectedHoldCents: 3000)); // 8000 + 3000 > 10000

        Assert.Equal(ApiErrorCode.LimitExceeded, ex.Code);
    }

    [Fact]
    public async Task Spend_cap_allows_when_under_the_limit()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500, MaxSpendCentsPerDay = 10000 });
        await fx.SeedLeaseAsync(ttlSeconds: 60, priceCentsPerMin: 4000, createdAt: T0);

        await fx.Guard.EnforceLeaseSpendAllowedAsync(fx.UserId, projectedHoldCents: 3000); // 4000 + 3000 ≤ 10000
    }

    // ---- unset / no policy ------------------------------------------------------------------------

    [Fact]
    public async Task No_policy_allows_everything()
    {
        var fx = new Fixture();

        await fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 100_000);
        await fx.Guard.EnforceLeaseSpendAllowedAsync(fx.UserId, 100_000);
    }

    [Fact]
    public async Task Unset_limits_allow_everything()
    {
        var fx = new Fixture();
        await fx.PublishAsync(new PlatformPolicy { FeeBps = 1500 }); // all fraud knobs null

        await fx.Guard.EnforceTopupAllowedAsync(fx.NewUser(T0), 100_000);
        await fx.Guard.EnforceLeaseSpendAllowedAsync(fx.UserId, 100_000);
    }
}
