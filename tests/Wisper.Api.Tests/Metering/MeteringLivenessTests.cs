using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Metering;

/// <summary>
/// Unit tests for the metering tick's liveness cap (docs/TUNNEL.md §8): accrual is capped at the host's
/// last-healthy point, so a blind (silent-but-not-yet-suspended) window is structurally un-billable, and a
/// lease whose host has no live tunnel is skipped entirely until the disconnect path suspends it.
/// </summary>
public class MeteringLivenessTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeLiveness : IMeterLivenessSource
    {
        private readonly IReadOnlyDictionary<Guid, DateTimeOffset?> _map;
        public FakeLiveness(IReadOnlyDictionary<Guid, DateTimeOffset?> map) => _map = map;

        public DateTimeOffset? LastHealthyAt(Guid hostId) =>
            _map.TryGetValue(hostId, out var value) ? value : null;
    }

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLeaseUsageRepository Usage { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public Guid HostId { get; private set; }
        private readonly Guid _consumerId = Guid.NewGuid();

        public async Task<MeteringService> NewMeterAsync(DateTimeOffset? lastHealthy)
        {
            var ledger = new LedgerService(LedgerStore);
            var policy = new PlatformPolicyService(Policies, Clock);
            await policy.PublishAsync(new PlatformPolicy { FeeBps = 1500, EffectiveFrom = T0 });
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Status = HostStatus.Online,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            HostId = host.Id;
            var liveness = new FakeLiveness(new Dictionary<Guid, DateTimeOffset?> { [HostId] = lastHealthy });
            return new MeteringService(
                Leases, Usage, Hosts, ledger, policy, Clock, NullLogger<MeteringService>.Instance, liveness);
        }

        public async Task<Lease> SeedActiveLeaseAsync()
        {
            var lease = await Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = _consumerId,
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = 60, // 1¢/s
                Currency = "usd",
                Status = LeaseStatus.Active,
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });

            var wallet = await new LedgerService(LedgerStore)
                .GetOrCreateAccountAsync(LedgerAccountKind.UserWallet, _consumerId);
            var cash = await new LedgerService(LedgerStore)
                .GetOrCreateAccountAsync(LedgerAccountKind.PlatformCash, null);
            var fees = await new LedgerService(LedgerStore)
                .GetOrCreateAccountAsync(LedgerAccountKind.StripeFees, null);
            var holds = await new LedgerService(LedgerStore)
                .GetOrCreateAccountAsync(LedgerAccountKind.LeaseHolds, null);
            var ledger = new LedgerService(LedgerStore);
            await ledger.PostAsync(LedgerFlows.Topup(
                wallet.Id, cash.Id, fees.Id, grossAmountCents: 3600, stripeFeeCents: 0,
                idempotencyKey: $"topup:{lease.Id}"));
            await ledger.PostAsync(LedgerFlows.LeaseHold(
                wallet.Id, holds.Id, lease.Id, 3600, idempotencyKey: $"hold:{lease.Id}"));
            return lease;
        }
    }

    [Fact]
    public async Task Tick_caps_accrual_at_the_hosts_last_healthy_point()
    {
        var fx = new Fixture();
        // The host has gone silent: last healthy frame at T0+90, but wall-clock is already at T0+150.
        var meter = await fx.NewMeterAsync(lastHealthy: T0.AddSeconds(90));
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(150));
        await meter.RunTickAsync();

        // Only the healthy 90s is billed -- the 60s blind window is not.
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(90, stored!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(90), stored.LastMeteredAt);
    }

    [Fact]
    public async Task Tick_skips_a_lease_whose_host_has_no_live_tunnel()
    {
        var fx = new Fixture();
        var meter = await fx.NewMeterAsync(lastHealthy: null); // no live tunnel
        var lease = await fx.SeedActiveLeaseAsync();

        fx.Clock.Advance(TimeSpan.FromSeconds(120));
        var flushed = await meter.RunTickAsync();

        Assert.Equal(0, flushed);
        var stored = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.Equal(0, stored!.BillableSeconds); // nothing accrued into an unvouched window
        Assert.Empty(await fx.Usage.ListByLeaseAsync(lease.Id));
    }
}
