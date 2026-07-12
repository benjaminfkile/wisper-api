using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// Unit tests for <see cref="TunnelDisconnectCoordinator"/> — the glue that drives the docs/TUNNEL.md §8
/// policy from the tunnel lifecycle onto <see cref="LeaseReconciliationService"/>. A controllable delay
/// stands in for the grace timer so the expiry / reconnect race is deterministic (no wall-clock sleeps).
/// </summary>
public class TunnelDisconnectCoordinatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A grace timer whose expiry fires only when the test calls <see cref="Fire"/> (or ct cancels).</summary>
    private sealed class ManualGrace
    {
        private readonly TaskCompletionSource _fire = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            _fire.Task.ContinueWith(
                _ =>
                {
                    registration.Dispose();
                    tcs.TrySetResult();
                },
                TaskScheduler.Default);
            return tcs.Task;
        }

        public void Fire() => _fire.TrySetResult();
    }

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryLeaseUsageRepository Usage { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryLedgerStore LedgerStore { get; } = new();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public ManualGrace Grace { get; } = new();

        public TunnelDisconnectCoordinator Coordinator { get; }

        public Guid HostId { get; private set; }
        public string HostKey => HostId.ToString();

        public Fixture()
        {
            var ledger = new LedgerService(LedgerStore);
            var policy = new PlatformPolicyService(Policies, Clock);
            var meter = new MeteringService(
                Leases, Usage, Hosts, ledger, policy, Clock, NullLogger<MeteringService>.Instance);
            var reconciler = new LeaseReconciliationService(
                Leases, meter, Clock, NullLogger<LeaseReconciliationService>.Instance);
            var options = new StaticOptionsMonitor<TunnelOptions>(new TunnelOptions { GraceSeconds = 90 });
            Coordinator = new TunnelDisconnectCoordinator(
                reconciler, options, Clock, NullLogger<TunnelDisconnectCoordinator>.Instance, Grace.Delay);
        }

        public async Task SeedAsync()
        {
            // Free (price-0) leases exercise the state moves without any charge/policy plumbing.
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
        }

        public Task<Lease> SeedActiveLeaseAsync() =>
            Leases.CreateAsync(new Lease
            {
                Id = Guid.NewGuid(),
                ConsumerUserId = Guid.NewGuid(),
                HostId = HostId,
                HostImageId = Guid.NewGuid(),
                ImageRef = "reg/wisp-base:latest",
                Network = NetworkMode.Open,
                TtlSeconds = 3600,
                PriceCentsPerMin = 0, // free image: no wallet/hold plumbing needed to exercise state moves
                Currency = "usd",
                Status = LeaseStatus.Active,
                CreatedAt = T0,
                StartedAt = T0,
                LastMeteredAt = T0,
                BillableSeconds = 0,
            });

        public Task<Lease?> ReloadAsync(Guid leaseId) => Leases.GetByIdAsync(leaseId);
    }

    private static async Task<Fixture> ReadyAsync()
    {
        var fx = new Fixture();
        await fx.SeedAsync();
        return fx;
    }

    [Fact]
    public async Task Disconnect_suspends_the_hosts_leases_and_arms_grace()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        _ = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        Assert.Equal(LeaseStatus.Suspended, (await fx.ReloadAsync(lease.Id))!.Status);
        Assert.True(fx.Coordinator.HasPendingGrace(fx.HostKey));
    }

    [Fact]
    public async Task Grace_expiry_with_no_reconnect_ends_the_leases_host_disconnect()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        // No reconnect: fire the grace timer and let the expiry run to completion.
        fx.Grace.Fire();
        await graceTask;

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, ended.EndReason);
        Assert.Equal(T0, ended.EndedAt);
        Assert.False(fx.Coordinator.HasPendingGrace(fx.HostKey));
    }

    [Fact]
    public async Task Reconnect_cancels_grace_and_the_heartbeat_resumes_a_present_lease()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        // Agent reconnects within grace: the timer is cancelled and the pending entry survives for the
        // heartbeat to reconcile.
        fx.Coordinator.OnReconnected(fx.HostKey);
        await graceTask; // the cancelled grace window completes without ending anything

        // The first heartbeat reports the lease still live → resume (same id).
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        var resumed = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, resumed!.Status);
        Assert.False(fx.Coordinator.HasPendingGrace(fx.HostKey));
    }

    [Fact]
    public async Task Reconnect_heartbeat_ends_a_vanished_lease_container_lost()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);
        fx.Coordinator.OnReconnected(fx.HostKey);
        await graceTask;

        // The heartbeat does NOT report the lease → the container died during the outage.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, Array.Empty<Guid>());

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, ended.EndReason);
    }

    [Fact]
    public async Task A_heartbeat_with_no_pending_grace_does_not_reconcile()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Steady-state heartbeat (no prior disconnect) must leave an active lease untouched.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, Array.Empty<Guid>());

        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task A_non_guid_host_id_is_a_no_op()
    {
        var fx = await ReadyAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync("dev-host-alpha", T0);
        fx.Coordinator.OnReconnected("dev-host-alpha");
        await fx.Coordinator.OnHeartbeatAsync("dev-host-alpha", Array.Empty<Guid>());
        await graceTask;

        Assert.False(fx.Coordinator.HasPendingGrace("dev-host-alpha"));
    }
}
