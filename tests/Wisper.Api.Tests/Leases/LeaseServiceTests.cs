using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Leases;

/// <summary>
/// Unit tests for <see cref="LeaseService"/> with a fake relay and in-memory repositories (Grunt has no
/// Postgres/tunnel): allow-list validation, the wallet-gate hook, tunnel-error surfacing, the persisted
/// snapshot, and the caller-scoped read/release paths (docs/API.md §5, docs/DATA_MODEL.md §5).
/// </summary>
public class LeaseServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public FakeTunnelRelay Relay { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public ILeaseWalletGate WalletGate { get; set; } = new AllowWalletGate();
        public InMemoryPlatformPolicyRepository Policies { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public Guid ConsumerId { get; } = Guid.NewGuid();

        public Host? Host { get; private set; }
        public HostImage? Image { get; private set; }

        public LeaseService Service() =>
            new(Leases, Hosts, Images, Relay, Capabilities, WalletGate,
                new PlatformPolicyService(Policies, Clock), Clock);

        /// <summary>Publishes a platform policy version setting the minimum isolation floor (task #418).</summary>
        public Task SetMinIsolationAsync(string? minIsolation) =>
            new PlatformPolicyService(Policies, Clock).PublishAsync(
                new PlatformPolicy { FeeBps = 0, MinIsolation = minIsolation, EffectiveFrom = T0 });

        /// <summary>Overwrites the seeded host's advertised isolation levels (task #417).</summary>
        public async Task SetHostIsolationLevelsAsync(params string[] levels)
        {
            Host = await Hosts.UpdateAsync(Host! with { IsolationLevels = levels });
        }

        /// <summary>Declares the live capability (optionally carrying the container OS) for the seeded host.</summary>
        public void SetHostOs(string? os) => Capabilities.Set(Host!.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024, Os: os));

        public async Task<HostImage> SeedImageAsync(
            HostStatus hostStatus = HostStatus.Online,
            long price = 5,
            int maxTtl = 14400,
            bool enabled = true,
            NetworkMode[]? networks = null,
            decimal? maxCpus = 4,
            int? maxMemoryMb = 8192,
            int? maxPids = 1024)
        {
            var host = await Hosts.CreateAsync(new Host
            {
                Id = Guid.NewGuid(),
                OwnerUserId = Guid.NewGuid(),
                Name = "home-server-1",
                Label = "us",
                Status = hostStatus,
                AgentTokenHash = "hash",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Host = host;
            var image = await Images.CreateAsync(new HostImage
            {
                HostId = host.Id,
                ImageRef = "reg/wisp-base:latest",
                PriceCentsPerMin = price,
                Networks = networks ?? new[] { NetworkMode.None, NetworkMode.Open },
                MaxTtlSeconds = maxTtl,
                MaxCpus = maxCpus,
                MaxMemoryMb = maxMemoryMb,
                MaxPids = maxPids,
                Enabled = enabled,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
            Image = image;
            return image;
        }

        public CreateLeaseRequest Request(
            string? network = "open",
            int? ttlSeconds = 3600,
            LeaseResourcesRequest? resources = null,
            Dictionary<string, string>? env = null,
            string? isolation = null) => new(
            HostId: Host!.Id.ToString(),
            HostImageId: Image!.Id.ToString(),
            Network: network,
            Resources: resources,
            TtlSeconds: ttlSeconds,
            Userdata: "apt-get install -y git",
            Env: env,
            Isolation: isolation);
    }

    [Fact]
    public async Task Create_validates_provisions_and_persists_a_snapshot()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);

        var result = await fx.Service().CreateAsync(
            fx.ConsumerId,
            fx.Request(network: "open", ttlSeconds: 3600,
                resources: new LeaseResourcesRequest(2, 4096, 1024)));

        // Provisioned over the tunnel with the image snapshot, addressed by the host id.
        var (hostId, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal(fx.Host!.Id.ToString(), hostId);
        Assert.Equal("reg/wisp-base:latest", spec.Image);
        Assert.Equal("open", spec.Network);
        Assert.Equal(3600, spec.TtlSeconds);
        Assert.Equal(2, spec.Resources.Cpus);

        // 201 body: price + hold snapshot. hold = ceil(3600/60) * 5 = 300.
        Assert.Equal(300, result.HoldCents);
        Assert.Equal(5, result.Lease.PriceCentsPerMin);

        // Persisted row: immutable snapshots, active + metering started, owned by the caller.
        var stored = await fx.Leases.GetByIdAsync(result.Lease.Id);
        Assert.NotNull(stored);
        Assert.Equal(fx.ConsumerId, stored!.ConsumerUserId);
        Assert.Equal(fx.Host.Id, stored.HostId);
        Assert.Equal(fx.Image!.Id, stored.HostImageId);
        Assert.Equal("reg/wisp-base:latest", stored.ImageRef);
        Assert.Equal(NetworkMode.Open, stored.Network);
        Assert.Equal(2m, stored.Cpus);
        Assert.Equal(4096, stored.MemoryMb);
        Assert.Equal(1024, stored.Pids);
        Assert.Equal(5, stored.PriceCentsPerMin);
        Assert.Equal(LeaseStatus.Active, stored.Status);
        Assert.Equal(T0, stored.StartedAt);
        Assert.Equal("wisp-contract-1", stored.WispContractId);
    }

    [Fact]
    public async Task Create_hold_estimate_rounds_ttl_up_to_the_minute()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 10);

        // 61s → ceil(61/60) = 2 minutes → 20 cents.
        var result = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: 61));

        Assert.Equal(20, result.HoldCents);
    }

    [Fact]
    public async Task Create_uses_the_relay_lease_id_as_the_primary_key()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();

        var result = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        // The relay-minted lease_<guid> id is the stored row's Guid primary key and its external id.
        Assert.Equal(fx.Relay.LastLeaseId, TunnelLeaseId.Format(result.Lease.Id));
    }

    [Theory]
    [InlineData("not-a-guid", "3600", "host_id")]
    public async Task Create_rejects_a_malformed_host_id(string hostId, string ttl, string _)
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var request = new CreateLeaseRequest(hostId, fx.Image!.Id.ToString(), "open", null, int.Parse(ttl), null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, request));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Create_rejects_a_missing_ttl()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: null)));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Create_is_image_not_allowed_for_an_unknown_image()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var request = new CreateLeaseRequest(
            fx.Host!.Id.ToString(), Guid.NewGuid().ToString(), "open", null, 3600, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, request));
        Assert.Equal(ApiErrorCode.ImageNotAllowed, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls); // never touched the tunnel
    }

    [Fact]
    public async Task Create_is_image_not_allowed_when_the_image_belongs_to_another_host()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var request = new CreateLeaseRequest(
            Guid.NewGuid().ToString(), fx.Image!.Id.ToString(), "open", null, 3600, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, request));
        Assert.Equal(ApiErrorCode.ImageNotAllowed, ex.Code);
    }

    [Fact]
    public async Task Create_is_image_not_allowed_for_a_disabled_image()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(enabled: false);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(ApiErrorCode.ImageNotAllowed, ex.Code);
    }

    [Fact]
    public async Task Create_is_image_not_allowed_for_a_suspended_host()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(hostStatus: HostStatus.Suspended);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(ApiErrorCode.ImageNotAllowed, ex.Code);
    }

    [Fact]
    public async Task Create_rejects_a_network_the_image_does_not_permit()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(networks: new[] { NetworkMode.None });

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(network: "open")));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Create_rejects_a_ttl_over_the_image_maximum()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(maxTtl: 3600);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(ttlSeconds: 7200)));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Create_rejects_resources_over_the_image_ceilings()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(maxCpus: 2);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(
                fx.ConsumerId, fx.Request(resources: new LeaseResourcesRequest(4, null, null))));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task Create_denied_by_the_wallet_gate_is_insufficient_funds_and_never_provisions()
    {
        var fx = new Fixture { WalletGate = new DenyingWalletGate(requiredCents: 300, availableCents: 120) };
        await fx.SeedImageAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(ApiErrorCode.InsufficientFunds, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls); // gated BEFORE any lease.create frame
    }

    [Theory]
    [InlineData(ApiErrorCode.HostOffline)]
    [InlineData(ApiErrorCode.UpstreamTimeout)]
    [InlineData(ApiErrorCode.LeaseFailed)]
    public async Task Create_surfaces_relay_errors_and_persists_nothing(ApiErrorCode code)
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        fx.Relay.CreateError = new ApiException(code, "relay failure");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(code, ex.Code);
        Assert.Empty(await fx.Leases.ListByConsumerAsync(fx.ConsumerId));
    }

    [Fact]
    public async Task Create_forwards_env_to_the_lease_create_frame()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var env = new Dictionary<string, string> { ["API_TOKEN"] = "s3cr3t", ["REGION"] = "eu" };

        await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(env: env));

        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.NotNull(spec.Env);
        Assert.Equal("s3cr3t", spec.Env!["API_TOKEN"]);
        Assert.Equal("eu", spec.Env["REGION"]);
    }

    [Fact]
    public async Task Create_omits_env_from_the_frame_when_absent()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();

        await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(env: null));

        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Null(spec.Env);
    }

    [Fact]
    public async Task Create_env_never_lands_on_the_persisted_lease_row()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var env = new Dictionary<string, string> { ["API_TOKEN"] = "top-secret-value" };

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(env: env));

        // The lease snapshot stores everything EXCEPT env (it may carry secrets); the value must not be
        // recoverable from the persisted row under any field.
        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.NotNull(stored);
        var serialized = System.Text.Json.JsonSerializer.Serialize(stored);
        Assert.DoesNotContain("top-secret-value", serialized);
        Assert.DoesNotContain("API_TOKEN", serialized);
    }

    [Fact]
    public async Task Create_rejects_env_over_the_entry_cap_and_never_provisions()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var env = Enumerable.Range(0, 129).ToDictionary(i => $"K{i}", _ => "v");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(env: env)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls); // guarded before any lease.create frame
    }

    [Fact]
    public async Task Create_rejects_env_over_the_size_cap_and_never_echoes_the_value()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        // A single entry whose value alone exceeds 256 KiB serialized.
        var big = new string('x', 256 * 1024 + 1);
        var env = new Dictionary<string, string> { ["BLOB"] = big };

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(env: env)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls);
        // The env value is a secret-in-transit — it must never surface in the error message or details.
        var rendered = ex.Message + System.Text.Json.JsonSerializer.Serialize(ex.Details);
        Assert.DoesNotContain(big, rendered);
    }

    [Fact]
    public async Task Create_defaults_isolation_to_shared_when_omitted()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: null));

        Assert.Equal("shared", created.Lease.Isolation);
        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal("shared", spec.Isolation); // passes through the tunnel frame
    }

    [Theory]
    [InlineData("sandboxed")]
    [InlineData("vm")]
    public async Task Create_accepts_a_known_isolation_level(string level)
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        await fx.SetHostIsolationLevelsAsync("shared", "sandboxed", "vm");

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: level));

        Assert.Equal(level, created.Lease.Isolation);
        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal(level, spec.Isolation);
    }

    [Theory]
    [InlineData("confidential")]
    [InlineData("bogus")]
    public async Task Create_rejects_confidential_and_unknown_isolation(string level)
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: level)));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls); // rejected before any lease.create frame
    }

    [Fact]
    public async Task Create_enforces_the_min_isolation_policy_floor()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        await fx.SetHostIsolationLevelsAsync("shared", "sandboxed", "vm");
        await fx.SetMinIsolationAsync("sandboxed"); // policy floor

        // 'shared' is below the floor → rejected.
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: "shared")));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls);

        // At/above the floor is allowed.
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: "vm"));
        Assert.Equal("vm", created.Lease.Isolation);
    }

    [Fact]
    public async Task Create_rejects_a_level_the_target_host_cannot_provide()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        await fx.SetHostIsolationLevelsAsync("shared"); // host offers only shared

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: "vm")));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(fx.Relay.CreateCalls);
    }

    [Fact]
    public async Task Create_passes_through_when_the_host_advertises_no_isolation_levels()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        await fx.SetHostIsolationLevelsAsync(); // none recorded → wisp is the real boundary

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: "vm"));

        Assert.Equal("vm", created.Lease.Isolation);
    }

    [Fact]
    public async Task Create_persists_isolation_on_the_snapshot_and_surfaces_it_in_the_view()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        await fx.SetHostIsolationLevelsAsync("shared", "sandboxed");

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request(isolation: "sandboxed"));

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal("sandboxed", stored!.Isolation);

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);
        Assert.Equal("sandboxed", view!.Isolation);
    }

    [Fact]
    public async Task Create_result_carries_the_hosts_container_os()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        fx.SetHostOs("linux"); // host is online and advertises a Linux container OS

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Equal("linux", created.Os);
    }

    [Fact]
    public async Task Create_result_os_is_null_when_the_host_is_offline_or_pre_os()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        // No capability declared for the host — offline (or a legacy agent that never advertised os).

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Null(created.Os);
    }

    [Fact]
    public async Task Get_returns_the_lease_for_its_owner()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);

        Assert.NotNull(view);
        Assert.Equal(TunnelLeaseId.Format(created.Lease.Id), view!.Id);
        Assert.Equal("active", view.Status);
    }

    [Fact]
    public async Task Get_carries_the_hosts_container_os_from_the_live_capability()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        fx.SetHostOs("windows"); // host is online and advertises a Windows container OS

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);

        Assert.NotNull(view);
        Assert.Equal("windows", view!.Os);
    }

    [Fact]
    public async Task Get_os_is_null_when_the_host_is_offline_or_pre_os()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        // No capability declared for the host — offline (or a legacy agent that never advertised os).

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);

        Assert.NotNull(view);
        Assert.Null(view!.Os);
    }

    [Fact]
    public async Task Get_is_null_for_a_lease_owned_by_another_user()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var view = await fx.Service().GetAsync(Guid.NewGuid(), created.Lease.Id);

        Assert.Null(view); // ownership failures are indistinguishable from missing
    }

    [Fact]
    public async Task List_filters_by_status_and_excludes_other_users()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var svc = fx.Service();
        var mine = await svc.CreateAsync(fx.ConsumerId, fx.Request());
        await svc.CreateAsync(Guid.NewGuid(), fx.Request()); // another user's lease

        var active = await svc.ListAsync(fx.ConsumerId, new LeaseListQuery { Status = LeaseStatus.Active, Limit = 25 });
        Assert.Equal(TunnelLeaseId.Format(mine.Lease.Id), Assert.Single(active.Data).Id);

        var ended = await svc.ListAsync(fx.ConsumerId, new LeaseListQuery { Status = LeaseStatus.Ended, Limit = 25 });
        Assert.Empty(ended.Data);
    }

    [Fact]
    public async Task List_paginates_with_a_cursor()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var svc = fx.Service();
        for (var i = 0; i < 3; i++)
        {
            fx.Clock.Advance(TimeSpan.FromSeconds(1)); // distinct created_at for a stable order
            await svc.CreateAsync(fx.ConsumerId, fx.Request());
        }

        var first = await svc.ListAsync(fx.ConsumerId, new LeaseListQuery { Limit = 2 });
        Assert.Equal(2, first.Data.Count);
        Assert.NotNull(first.NextCursor);

        Assert.True(LeaseCursor.TryParse(first.NextCursor, out var cursor));
        var second = await svc.ListAsync(fx.ConsumerId, new LeaseListQuery { Limit = 2, Cursor = cursor });
        Assert.Single(second.Data);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Release_sends_lease_release_and_marks_the_lease_ended()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        fx.Clock.Advance(TimeSpan.FromMinutes(5));

        var view = await fx.Service().ReleaseAsync(fx.ConsumerId, created.Lease.Id);

        var (hostId, leaseId) = Assert.Single(fx.Relay.ReleaseCalls);
        Assert.Equal(fx.Host!.Id.ToString(), hostId);
        Assert.Equal(TunnelLeaseId.Format(created.Lease.Id), leaseId);

        Assert.Equal("ended", view!.Status);
        Assert.Equal("released", view.EndReason);
        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal(LeaseStatus.Ended, stored!.Status);
        Assert.Equal(LeaseEndReason.Released, stored.EndReason);
        Assert.NotNull(stored.EndedAt);
    }

    [Fact]
    public async Task Release_is_idempotent_on_an_already_ended_lease()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        await fx.Service().ReleaseAsync(fx.ConsumerId, created.Lease.Id);

        var again = await fx.Service().ReleaseAsync(fx.ConsumerId, created.Lease.Id);

        Assert.Equal("ended", again!.Status);
        Assert.Single(fx.Relay.ReleaseCalls); // the second release did not re-hit the tunnel
    }

    [Fact]
    public async Task Release_marks_ended_even_when_the_host_is_offline()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.HostOffline, "no live tunnel");

        var view = await fx.Service().ReleaseAsync(fx.ConsumerId, created.Lease.Id);

        Assert.Equal("ended", view!.Status); // host gone → container gone → safe to end
    }

    [Fact]
    public async Task Release_propagates_an_upstream_timeout()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.UpstreamTimeout, "no response");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().ReleaseAsync(fx.ConsumerId, created.Lease.Id));
        Assert.Equal(ApiErrorCode.UpstreamTimeout, ex.Code);
    }

    [Fact]
    public async Task Release_is_null_for_a_lease_the_caller_does_not_own()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var view = await fx.Service().ReleaseAsync(Guid.NewGuid(), created.Lease.Id);

        Assert.Null(view);
        Assert.Empty(fx.Relay.ReleaseCalls);
    }

    [Fact]
    public async Task ResolveExecTarget_returns_the_host_and_lease_token_for_an_active_lease()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var target = await fx.Service().ResolveExecTargetAsync(fx.ConsumerId, created.Lease.Id);

        Assert.NotNull(target);
        Assert.Equal(fx.Host!.Id.ToString(), target!.HostId);
        Assert.Equal(TunnelLeaseId.Format(created.Lease.Id), target.LeaseId);
    }

    [Fact]
    public async Task ResolveExecTarget_is_null_for_a_lease_the_caller_does_not_own()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var target = await fx.Service().ResolveExecTargetAsync(Guid.NewGuid(), created.Lease.Id);

        Assert.Null(target); // ownership failures are indistinguishable from missing (→ 404)
    }

    [Fact]
    public async Task ResolveExecTarget_throws_lease_not_ready_before_the_lease_is_active()
    {
        var fx = new Fixture();
        await fx.SeedImageAsync();
        // A provisioning lease is not yet ready for exec — seed it directly (create always yields active).
        var lease = await fx.Leases.CreateAsync(new Lease
        {
            Id = Guid.NewGuid(),
            ConsumerUserId = fx.ConsumerId,
            HostId = fx.Host!.Id,
            HostImageId = fx.Image!.Id,
            ImageRef = fx.Image.ImageRef,
            Network = NetworkMode.Open,
            TtlSeconds = 3600,
            PriceCentsPerMin = 5,
            Currency = "usd",
            Status = LeaseStatus.Provisioning,
            WispContractId = "wisp-contract-1",
            CreatedAt = T0,
            LastMeteredAt = T0,
        });

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().ResolveExecTargetAsync(fx.ConsumerId, lease.Id));
        Assert.Equal(ApiErrorCode.LeaseNotReady, ex.Code);
    }

    private sealed class DenyingWalletGate : ILeaseWalletGate
    {
        private readonly long _required;
        private readonly long _available;

        public DenyingWalletGate(long requiredCents, long availableCents)
        {
            _required = requiredCents;
            _available = availableCents;
        }

        public Task<WalletGateDecision> AuthorizeHoldAsync(
            Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default) =>
            Task.FromResult(WalletGateDecision.Deny(_required, _available));

        public Task<Guid?> PlaceHoldAsync(
            Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(null); // never reached — the deny gates before provisioning

        public Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
