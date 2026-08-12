using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Metering;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Persistence.Users;
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
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
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
            var fraud = new FraudGuardService(
                ledger, Leases, policy, Clock, NullLogger<FraudGuardService>.Instance);
            var walletGate = new WalletLeaseGate(
                ledger, Leases, policy, fraud, NullLogger<WalletLeaseGate>.Instance);
            var reconciler = new LeaseReconciliationService(
                Leases, meter, walletGate, Clock, NullLogger<LeaseReconciliationService>.Instance);
            var options = new StaticOptionsMonitor<TunnelOptions>(new TunnelOptions { GraceSeconds = 90 });
            // The real presence hook so a durable disconnect flips the host offline (task #392); a blip or
            // superseding reconnect never reaches it, so the host stays online across those.
            var presence = new HostPresenceService(
                Hosts, Images, Users, Clock, NullLogger<HostPresenceService>.Instance);
            Coordinator = new TunnelDisconnectCoordinator(
                reconciler, options, Clock, NullLogger<TunnelDisconnectCoordinator>.Instance, Grace.Delay,
                presence);
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

        public async Task<HostStatus> HostStatusAsync() => (await Hosts.GetByIdAsync(HostId))!.Status;

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
        // The host stays online during the grace window — a reconnect must find it already catalogued.
        Assert.Equal(HostStatus.Online, await fx.HostStatusAsync());
    }

    [Fact]
    public async Task Disconnect_with_no_leases_flips_the_host_offline_immediately()
    {
        var fx = await ReadyAsync();

        // No active leases: nothing to protect, so there is no grace window and the host goes offline now.
        _ = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        Assert.False(fx.Coordinator.HasPendingGrace(fx.HostKey));
        Assert.Equal(HostStatus.Offline, await fx.HostStatusAsync());
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
        // The loss is now durable → the host is flipped offline and drops out of the catalog.
        Assert.Equal(HostStatus.Offline, await fx.HostStatusAsync());
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
        // A reconnect within grace is a blip — the host was never flipped offline.
        Assert.Equal(HostStatus.Online, await fx.HostStatusAsync());
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
    public async Task Steady_state_heartbeat_ends_active_unreported_lease_as_container_lost()
    {
        // A silently-dead container: the manager has an active lease but the host does not report it.
        // The continuous reconciliation path must end it as container_lost WITHOUT a tunnel disconnect.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // No disconnect ever happened — this is a steady-state heartbeat that omits the lease.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, Array.Empty<Guid>());

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, ended.EndReason);
    }

    [Fact]
    public async Task Steady_state_heartbeat_with_matching_lease_set_performs_zero_writes()
    {
        // Steady-state (reported set == active set) must produce no DB writes — no updated_at churn.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var before = await fx.ReloadAsync(lease.Id);

        // Heartbeat reports the lease as live — perfect match with the manager's active set.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        var after = await fx.ReloadAsync(lease.Id);
        Assert.Equal(before, after); // record equality: every field identical, no write occurred
    }

    [Fact]
    public async Task Steady_state_heartbeat_revives_host_disconnect_ended_lease()
    {
        // A live host contract with no active manager lease: the lease was ended as host_disconnect
        // (simulating drift — e.g. a manager restart mid-flight) but the container kept running.
        // A steady-state heartbeat (no pending grace or post-grace flags) must revive it.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Directly set the lease to Ended/host_disconnect to simulate drift without going through the
        // coordinator's disconnect path (no pending grace or post-grace flags remain).
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.HostDisconnect, endedAt: T0);
        Assert.False(fx.Coordinator.HasPendingGrace(fx.HostKey));
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));

        // Steady-state heartbeat reports the lease as still live → continuous path must revive it.
        fx.Clock.Advance(TimeSpan.FromSeconds(15));
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, revived!.Status);
        Assert.Null(revived.EndReason);
        Assert.Null(revived.EndedAt);
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

    // ---- Post-grace reconnect reconciliation (docs/TUNNEL.md §8) ----

    [Fact]
    public async Task Grace_expiry_marks_the_host_for_post_grace_reconciliation()
    {
        var fx = await ReadyAsync();
        await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        // No reconnect: grace fires and the lease is ended.
        fx.Grace.Fire();
        await graceTask;

        Assert.False(fx.Coordinator.HasPendingGrace(fx.HostKey));
        Assert.True(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }

    [Fact]
    public async Task PostGrace_reconnect_with_live_contract_revives_the_lease()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Disconnect → grace → grace expires → lease ended as host_disconnect.
        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);
        fx.Grace.Fire();
        await graceTask;

        var endedLease = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, endedLease!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, endedLease.EndReason);
        Assert.Equal(HostStatus.Offline, await fx.HostStatusAsync());

        // Host reconnects after grace (container still running).
        fx.Coordinator.OnReconnected(fx.HostKey);
        Assert.True(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));

        // First heartbeat reports the lease as still live → revive.
        fx.Clock.Advance(TimeSpan.FromSeconds(15));
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        var revived = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Active, revived!.Status);
        Assert.Null(revived.EndReason);
        Assert.Null(revived.EndedAt);
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }

    [Fact]
    public async Task PostGrace_reconnect_capacity_count_matches_after_revival()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Grace expiry: lease ended, capacity appears free.
        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);
        fx.Grace.Fire();
        await graceTask;
        Assert.Equal(0, await fx.Leases.CountActiveByHostAsync(fx.HostId));

        // Post-grace reconnect with live contract.
        fx.Coordinator.OnReconnected(fx.HostKey);
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        // Manager count now reflects the running container — no phantom free slot.
        Assert.Equal(1, await fx.Leases.CountActiveByHostAsync(fx.HostId));
    }

    [Fact]
    public async Task PostGrace_reconnect_with_no_live_contracts_leaves_lease_ended()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);
        fx.Grace.Fire();
        await graceTask;

        // Host reconnects but reports no live contracts (container actually died).
        fx.Coordinator.OnReconnected(fx.HostKey);
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, Array.Empty<Guid>());

        // Lease stays ended — there is nothing to revive.
        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.HostDisconnect, stored.EndReason);
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }

    [Fact]
    public async Task PostGrace_flag_is_cleared_on_a_fresh_disconnect()
    {
        var fx = await ReadyAsync();
        await fx.SeedActiveLeaseAsync();

        // First cycle: grace fires, post-grace flag is set.
        var graceTask1 = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);
        fx.Grace.Fire();
        await graceTask1;
        Assert.True(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));

        // The host disconnects again before sending a heartbeat — the flag must be reset.
        _ = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0.AddSeconds(15));
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }

    [Fact]
    public async Task Grace_expiry_without_ended_leases_does_not_set_post_grace_flag()
    {
        // If the host had no live leases when grace fired (e.g. all were already released), there is
        // nothing to revive, so the post-grace flag must not be set.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var graceTask = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0);

        // Release the lease while the grace timer is running (consumer cancelled it).
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        fx.Grace.Fire();
        await graceTask;

        // Grace fires but nothing was suspended to end, so ended = 0 → no post-grace flag.
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }
}
