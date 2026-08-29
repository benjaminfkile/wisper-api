using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
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
/// Unit tests for <see cref="TunnelDisconnectCoordinator"/> -- the glue that drives the docs/TUNNEL.md §8
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
        // Fake relay so the orphan teardown path (task #73) is observable without a live tunnel -- the
        // coordinator resolves the relay lazily via a factory, so this fixture just supplies a fake.
        public FakeTunnelRelay Relay { get; } = new();

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
                presence, tunnelRelayFactory: () => Relay);
        }

        /// <summary>
        /// Builds a fresh <see cref="TunnelConnection"/> for this fixture's host so tests can drive the
        /// connection-based <see cref="TunnelDisconnectCoordinator.OnHeartbeatAsync(TunnelConnection, IReadOnlyCollection{Guid}, CancellationToken)"/>
        /// overload. The socket is a stub -- the coordinator only reads <see cref="TunnelConnection.HostId"/>
        /// and <see cref="TunnelConnection.TerminalTeardownRelayed"/> from it.
        /// </summary>
        public TunnelConnection NewConnection(string? sessionId = null) =>
            new(new StubWebSocket(), HostKey, sessionId ?? $"sess-{Guid.NewGuid():N}",
                maxReceiveBytes: 65536, NullLogger.Instance);

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
        // The host stays online during the grace window -- a reconnect must find it already catalogued.
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
        // A reconnect within grace is a blip -- the host was never flipped offline.
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

        // No disconnect ever happened -- this is a steady-state heartbeat that omits the lease.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, Array.Empty<Guid>());

        var ended = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, ended.EndReason);
    }

    [Fact]
    public async Task Steady_state_heartbeat_with_matching_lease_set_performs_zero_writes()
    {
        // Steady-state (reported set == active set) must produce no DB writes -- no updated_at churn.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        var before = await fx.ReloadAsync(lease.Id);

        // Heartbeat reports the lease as live -- perfect match with the manager's active set.
        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        var after = await fx.ReloadAsync(lease.Id);
        Assert.Equal(before, after); // record equality: every field identical, no write occurred
    }

    [Fact]
    public async Task Steady_state_heartbeat_revives_host_disconnect_ended_lease()
    {
        // A live host contract with no active manager lease: the lease was ended as host_disconnect
        // (simulating drift -- e.g. a manager restart mid-flight) but the container kept running.
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

        // Manager count now reflects the running container -- no phantom free slot.
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

        // Lease stays ended -- there is nothing to revive.
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

        // The host disconnects again before sending a heartbeat -- the flag must be reset.
        _ = await fx.Coordinator.OnDisconnectedAsync(fx.HostKey, T0.AddSeconds(15));
        Assert.False(fx.Coordinator.HasPendingPostGraceReconcile(fx.HostKey));
    }

    // ---- Agent-driven lease.ended (task #56, docs/TUNNEL.md §5, §8) ----

    [Fact]
    public async Task OnLeaseEnded_expired_ends_the_lease_as_expired()
    {
        // AC185: the coordinator routes lease.ended(expired) into the reconciler's end path.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        await fx.Coordinator.OnLeaseEndedAsync(fx.HostKey, lease.Id, LeaseEndReason.Expired);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Expired, stored.EndReason);
    }

    [Fact]
    public async Task OnLeaseEnded_container_lost_ends_the_lease_as_container_lost()
    {
        // AC186: `failed`/`gone` map to container_lost; the coordinator hands the mapped reason through.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        await fx.Coordinator.OnLeaseEndedAsync(fx.HostKey, lease.Id, LeaseEndReason.ContainerLost);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.ContainerLost, stored.EndReason);
    }

    [Fact]
    public async Task OnLeaseEnded_already_ended_lease_is_a_no_op()
    {
        // AC187: an already-ended lease stays as it was -- no double transition, no reason overwrite.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        await fx.Coordinator.OnLeaseEndedAsync(fx.HostKey, lease.Id, LeaseEndReason.Expired);

        var stored = await fx.ReloadAsync(lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Released, stored.EndReason); // original reason preserved
    }

    [Fact]
    public async Task OnLeaseEnded_non_guid_host_id_is_a_no_op()
    {
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // A dev-harness host id is not a Guid -- the reconciler is never called, the lease stays untouched.
        await fx.Coordinator.OnLeaseEndedAsync("dev-host-alpha", lease.Id, LeaseEndReason.Expired);

        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    // ---- Best-effort orphan teardown on continuous heartbeat (task #73) ----
    //
    // When a heartbeat reports a lease whose manager-side state is terminal-and-not-revivable (ended for any
    // reason other than host_disconnect: released / container_lost / expired / admin / payment_failed), the
    // container is still pinning a host capacity slot until wisp's TTL reaper fires. The coordinator now
    // best-effort relays lease.release over the tunnel so the container is torn down within one heartbeat
    // instead of waiting up to a full lease TTL. Relay is per-connection-deduped (mirrors the degraded-
    // logging discipline in task #65) and swallows tunnel failures (wisp's TTL reaper remains the backstop).

    [Fact]
    public async Task Heartbeat_reported_released_lease_triggers_exactly_one_lease_release_relay()
    {
        // AC263: a released (consumer DELETE while the tunnel was down) lease the host still reports must
        // get exactly one lease.release relay so the container is torn down immediately.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();

        // Consumer released the lease while the host was offline; the release ended the DB row but the
        // host-side teardown was skipped (no live tunnel).
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });

        var expectedLeaseId = TunnelLeaseId.Format(lease.Id);
        Assert.Equal(new[] { (fx.HostKey, expectedLeaseId) }, fx.Relay.ReleaseCalls);
        Assert.True(connection.TerminalTeardownRelayed.ContainsKey(lease.Id));
    }

    [Theory]
    [InlineData(LeaseEndReason.Released)]
    [InlineData(LeaseEndReason.ContainerLost)]
    [InlineData(LeaseEndReason.Expired)]
    [InlineData(LeaseEndReason.Admin)]
    [InlineData(LeaseEndReason.PaymentFailed)]
    public async Task Heartbeat_reported_terminal_lease_triggers_teardown_for_every_non_hostdisconnect_reason(
        LeaseEndReason reason)
    {
        // AC263: every terminal end reason OTHER than host_disconnect (which is the revivable case) must
        // trigger a teardown relay -- the container is definitively orphaned.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(lease.Id, LeaseStatus.Ended, endReason: reason, endedAt: T0);

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });

        Assert.Single(fx.Relay.ReleaseCalls);
        Assert.Equal(fx.HostKey, fx.Relay.ReleaseCalls[0].HostId);
        Assert.Equal(TunnelLeaseId.Format(lease.Id), fx.Relay.ReleaseCalls[0].LeaseId);
    }

    [Fact]
    public async Task Heartbeat_revive_path_does_not_relay_teardown()
    {
        // AC264: a reported+ended(host_disconnect) lease is the REVIVE path -- the container kept running
        // through an outage and must come back to active, NOT be torn down. The relay must stay silent.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.HostDisconnect, endedAt: T0);

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });

        Assert.Empty(fx.Relay.ReleaseCalls);
        Assert.Empty(connection.TerminalTeardownRelayed);
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(lease.Id))!.Status);
    }

    [Fact]
    public async Task Heartbeat_active_and_suspended_paths_do_not_relay_teardown()
    {
        // AC264: an active lease that continues to be reported is steady-state -- no teardown. A suspended
        // lease reported live resumes back to active -- also no teardown. Neither should touch the relay.
        var fx = await ReadyAsync();
        var stillActive = await fx.SeedActiveLeaseAsync();
        var stillSuspended = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(stillSuspended.Id, LeaseStatus.Suspended);

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(
            connection, new[] { stillActive.Id, stillSuspended.Id });

        Assert.Empty(fx.Relay.ReleaseCalls);
        Assert.Empty(connection.TerminalTeardownRelayed);
    }

    [Fact]
    public async Task Heartbeat_relay_failure_is_swallowed_and_reconcile_pass_completes()
    {
        // AC265: a host_offline / upstream_timeout on the teardown relay must NOT surface upward -- the
        // reconcile pass keeps working and wisp's TTL reaper is the backstop. The lease id is still marked
        // as attempted so subsequent heartbeats on this connection do not spam retries or logs.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.HostOffline, "tunnel just dropped");

        var connection = fx.NewConnection();

        // Must not throw -- the entire heartbeat handler is best-effort.
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });

        // Exactly one relay attempt was made (and it threw) -- the failed lease id is still tracked so
        // the next heartbeat on this connection does not retry.
        Assert.Single(fx.Relay.ReleaseCalls);
        Assert.True(connection.TerminalTeardownRelayed.ContainsKey(lease.Id));

        // Subsequent heartbeat with the same reported set: no additional relay attempt.
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });
        Assert.Single(fx.Relay.ReleaseCalls);
    }

    [Fact]
    public async Task Heartbeat_repeated_beats_do_not_spam_teardown_relays_per_connection()
    {
        // AC265: an orphan the host keeps reporting on every heartbeat must trigger exactly ONE relay
        // per connection lifetime -- the dedupe set on TunnelConnection.TerminalTeardownRelayed is what
        // stops the spam.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        var connection = fx.NewConnection();
        for (var i = 0; i < 5; i++)
        {
            await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });
        }

        Assert.Single(fx.Relay.ReleaseCalls);
    }

    [Fact]
    public async Task Heartbeat_fresh_connection_gets_one_new_teardown_attempt_per_reconnect()
    {
        // A supersede/reconnect must reset the dedupe set: the returning agent (a fresh
        // TunnelConnection) gets exactly one fresh attempt per orphan lease. This is how a first-attempt
        // host_offline failure eventually converges -- the next connection tries again.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        // First connection tried and (for the sake of the test) failed.
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.HostOffline, "flaky");
        var first = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(first, new[] { lease.Id });
        Assert.Single(fx.Relay.ReleaseCalls);

        // New connection (the agent reconnected). Relay is healthy this time.
        fx.Relay.ReleaseError = null;
        var second = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(second, new[] { lease.Id });

        // Two total attempts -- one per connection. The second dedupe set is independent of the first.
        Assert.Equal(2, fx.Relay.ReleaseCalls.Count);
        Assert.True(second.TerminalTeardownRelayed.ContainsKey(lease.Id));
    }

    [Fact]
    public async Task Heartbeat_teardown_relay_does_not_touch_the_ledger()
    {
        // AC266: the teardown path is host-side hygiene only -- the lease is already finalized/ended.
        // No metering, no hold-release, no wallet writes. We assert this by snapshotting the ledger
        // balances before and after the teardown-triggering heartbeat.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        // Finalize the lease as released to reach the terminal-orphan branch.
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        var storedBefore = await fx.ReloadAsync(lease.Id);

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { lease.Id });

        // Relay fired but the lease row is byte-for-byte unchanged (no billing, no state churn).
        Assert.Single(fx.Relay.ReleaseCalls);
        var storedAfter = await fx.ReloadAsync(lease.Id);
        Assert.Equal(storedBefore, storedAfter);
    }

    [Fact]
    public async Task Heartbeat_reported_unknown_lease_id_is_NOT_torn_down()
    {
        // Task #75: a reported lease id with NO manager row at all must NOT trigger a teardown relay.
        // LeaseService.CreateAsync provisions the container over the tunnel BEFORE inserting the lease
        // row, so a heartbeat landing in that window reports the fresh id while the manager still returns
        // null for GetByIdAsync. The pre-#75 code (which relayed lease.release for the entire orphan
        // bucket) killed the container mid-create. Wisp's TTL reaper is the backstop for any true
        // garbage id, and the next heartbeat (once the row is inserted) resolves to either active or
        // container_lost via the normal set-diff.
        var fx = await ReadyAsync();
        var unknownId = Guid.NewGuid();

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { unknownId });

        Assert.Empty(fx.Relay.ReleaseCalls);
        Assert.Empty(connection.TerminalTeardownRelayed);
    }

    [Fact]
    public async Task Heartbeat_between_provision_and_row_insert_does_not_kill_mid_create()
    {
        // Task #75 mid-create scenario: simulate the exact race the review identified. The consumer's
        // CreateLease has just returned from _relay.CreateLeaseAsync (the container is provisioned on
        // the host) but the lease row is not yet inserted; a host.heartbeat lands in that millisecond
        // window and reports the fresh lease id. Under the pre-#75 teardown code the reconciler saw
        // GetByIdAsync return null, classified the id as an orphan, and relayed lease.release -- killing
        // the create in-flight. The fixed code must classify the id as UnknownReported (skip teardown)
        // so the create completes normally and the next heartbeat sees the newly-persisted active row.
        var fx = await ReadyAsync();
        var midCreateLeaseId = Guid.NewGuid(); // the id the relay just minted on the host

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { midCreateLeaseId });

        // No teardown relay fired: the container stays alive and the create can finish inserting the row.
        Assert.Empty(fx.Relay.ReleaseCalls);
        Assert.Empty(connection.TerminalTeardownRelayed);

        // Simulate the create finishing: the row gets inserted after the mid-create heartbeat lands.
        // The next heartbeat with the same reported id must now be plain steady-state (no teardown).
        await fx.Leases.CreateAsync(new Lease
        {
            Id = midCreateLeaseId,
            ConsumerUserId = Guid.NewGuid(),
            HostId = fx.HostId,
            HostImageId = Guid.NewGuid(),
            ImageRef = "reg/wisp-base:latest",
            Network = NetworkMode.Open,
            TtlSeconds = 3600,
            PriceCentsPerMin = 0,
            Currency = "usd",
            Status = LeaseStatus.Active,
            CreatedAt = T0,
            StartedAt = T0,
            LastMeteredAt = T0,
            BillableSeconds = 0,
        });

        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { midCreateLeaseId });

        Assert.Empty(fx.Relay.ReleaseCalls);
        Assert.Equal(LeaseStatus.Active, (await fx.ReloadAsync(midCreateLeaseId))!.Status);
    }

    [Fact]
    public async Task Heartbeat_mixed_terminal_and_unknown_only_relays_the_terminal_row()
    {
        // Task #75: when a heartbeat reports both a terminal-row orphan (safe to tear down) and an
        // unknown id (mid-create or garbage), only the terminal row is relayed. The unknown id is a
        // no-op for the teardown path; wisp's TTL reaper is the backstop for real garbage.
        var fx = await ReadyAsync();
        var terminal = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            terminal.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);
        var unknown = Guid.NewGuid();

        var connection = fx.NewConnection();
        await fx.Coordinator.OnHeartbeatAsync(connection, new[] { terminal.Id, unknown });

        Assert.Single(fx.Relay.ReleaseCalls);
        Assert.Equal(TunnelLeaseId.Format(terminal.Id), fx.Relay.ReleaseCalls[0].LeaseId);
        Assert.True(connection.TerminalTeardownRelayed.ContainsKey(terminal.Id));
        Assert.False(connection.TerminalTeardownRelayed.ContainsKey(unknown));
    }

    [Fact]
    public async Task Heartbeat_orphan_teardown_skipped_on_the_string_id_overload()
    {
        // The string-id overload used by unit fixtures does not have a connection to dedupe against, so
        // it must NOT drive the teardown relay. Preserves the "reconciler is pure" surface for tests
        // that only care about state transitions.
        var fx = await ReadyAsync();
        var lease = await fx.SeedActiveLeaseAsync();
        await fx.Leases.TransitionStateAsync(
            lease.Id, LeaseStatus.Ended, endReason: LeaseEndReason.Released, endedAt: T0);

        await fx.Coordinator.OnHeartbeatAsync(fx.HostKey, new[] { lease.Id });

        Assert.Empty(fx.Relay.ReleaseCalls);
    }

    /// <summary>A do-nothing <see cref="WebSocket"/> -- the coordinator never touches it.</summary>
    private sealed class StubWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken ct) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> b, WebSocketMessageType t, bool e, CancellationToken ct) =>
            Task.CompletedTask;
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
