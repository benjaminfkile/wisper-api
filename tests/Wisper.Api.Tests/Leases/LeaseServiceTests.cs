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

        /// <summary>
        /// Declares the seeded host's live capability with a concurrent-contract ceiling (task #571). A ceiling
        /// of 0 leaves the host unlimited — the absent-block/pre-#571 behavior.
        /// </summary>
        public void SetHostMaxContracts(int maxContracts) => Capabilities.Set(Host!.Id, new HostCapabilitySnapshot(
            Array.Empty<string>(), Array.Empty<NetworkMode>(),
            MaxTtlSeconds: 3600, MaxCpus: 4, MaxMemoryMb: 8192, MaxPids: 1024, MaxContracts: maxContracts));

        /// <summary>
        /// Declares the seeded host's live capability with the given advertised per-lease caps (task #578).
        /// The defaults (4 cores / 8192 MB) are a sized cap; pass 0/0 for a host that advertises no cap.
        /// </summary>
        public void SetHostLimits(double maxCpus = 4, long maxMemoryMb = 8192) =>
            Capabilities.Set(Host!.Id, new HostCapabilitySnapshot(
                Array.Empty<string>(), Array.Empty<NetworkMode>(),
                MaxTtlSeconds: 3600, MaxCpus: maxCpus, MaxMemoryMb: maxMemoryMb, MaxPids: 1024));

        /// <summary>Seeds <paramref name="count"/> live (active) leases on the seeded host to fill its capacity.</summary>
        public async Task SeedActiveLeasesOnHostAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Leases.CreateAsync(new Lease
                {
                    Id = Guid.NewGuid(),
                    ConsumerUserId = Guid.NewGuid(),
                    HostId = Host!.Id,
                    HostImageId = Image!.Id,
                    ImageRef = Image.ImageRef,
                    Network = NetworkMode.Open,
                    TtlSeconds = 3600,
                    PriceCentsPerMin = 5,
                    Currency = "usd",
                    Status = LeaseStatus.Active,
                    WispContractId = $"wisp-contract-seed-{i}",
                    CreatedAt = T0,
                    StartedAt = T0,
                    LastMeteredAt = T0,
                });
            }
        }

        public async Task<HostImage> SeedImageAsync(
            HostStatus hostStatus = HostStatus.Online,
            long price = 5,
            int maxTtl = 14400,
            bool enabled = true,
            NetworkMode[]? networks = null,
            decimal? maxCpus = 4,
            int? maxMemoryMb = 8192,
            int? maxPids = 1024,
            int? cpus = null,
            int? memoryMb = null,
            int gpus = 0)
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
                Cpus = cpus,
                MemoryMb = memoryMb,
                Gpus = gpus,
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
            string? isolation = null,
            int? gpus = null) => new(
            HostId: Host!.Id.ToString(),
            HostImageId: Image!.Id.ToString(),
            Network: network,
            Resources: resources,
            TtlSeconds: ttlSeconds,
            Userdata: "apt-get install -y git",
            Env: env,
            Isolation: isolation,
            Gpus: gpus);
    }

    [Fact]
    public async Task Create_provisions_the_offer_profile_and_persists_a_snapshot()
    {
        // The lease provisions EXACTLY the selected offer's sized profile (task #570): the consumer sends no
        // resources, and the offer's cpus/memory_mb/gpus travel down the tunnel and snapshot on the row.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5, cpus: 2, memoryMb: 4096, gpus: 0);

        var result = await fx.Service().CreateAsync(
            fx.ConsumerId, fx.Request(network: "open", ttlSeconds: 3600));

        // Provisioned over the tunnel with the image + profile snapshot, addressed by the host id.
        var (hostId, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal(fx.Host!.Id.ToString(), hostId);
        Assert.Equal("reg/wisp-base:latest", spec.Image);
        Assert.Equal("open", spec.Network);
        Assert.Equal(3600, spec.TtlSeconds);
        Assert.Equal(2d, spec.Resources.Cpus);        // the offer's exact profile, not a consumer ask
        Assert.Equal(4096, spec.Resources.MemoryMb);

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
        Assert.Equal(2, stored.Cpus);                 // stamped from the offer profile
        Assert.Equal(4096, stored.MemoryMb);
        Assert.Equal(5, stored.PriceCentsPerMin);
        Assert.Equal(LeaseStatus.Active, stored.Status);
        Assert.Equal(T0, stored.StartedAt);
        Assert.Equal("wisp-contract-1", stored.WispContractId);

        // The read view exposes exactly the stamped profile so the consumer sees what the flat price bought.
        var view = await fx.Service().GetAsync(fx.ConsumerId, result.Lease.Id);
        Assert.Equal(2, view!.Resources.Cpus);
        Assert.Equal(4096, view.Resources.MemoryMb);
        Assert.Equal(0, view.Resources.Gpus);
    }

    [Fact]
    public async Task Create_omits_cpus_and_memory_from_the_frame_for_a_null_profile_offer()
    {
        // A NULL cpus/memory_mb profile means the offer defers to the host's own per-lease policy default: the
        // frame omits those keys entirely (WhenWritingDefault), and the snapshot records null.
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: null, memoryMb: null, gpus: 0);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        var resources = System.Text.Json.JsonDocument
            .Parse(ControlJson.Serialize(spec)).RootElement.GetProperty("resources");
        Assert.False(resources.TryGetProperty("cpus", out _));
        Assert.False(resources.TryGetProperty("memory_mb", out _));

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Null(stored!.Cpus);
        Assert.Null(stored.MemoryMb);
    }

    [Fact]
    public async Task Create_stamps_the_host_per_lease_cap_for_a_null_profile_offer()
    {
        // task #578: a NULL-profile offer no longer records an unknown size. It stamps the host's advertised
        // per-lease cap (limits.max_cpus/max_memory_mb) onto the row so the consumer can see what they leased
        // even after the fact — while the lease.create frame STILL omits cpus/memory (host defaults apply).
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: null, memoryMb: null, gpus: 0);
        fx.SetHostLimits(maxCpus: 4, maxMemoryMb: 8192);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        // The frame is unchanged — the offer pinned nothing, so cpus/memory_mb are omitted (host defaults).
        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        var resources = System.Text.Json.JsonDocument
            .Parse(ControlJson.Serialize(spec)).RootElement.GetProperty("resources");
        Assert.False(resources.TryGetProperty("cpus", out _));
        Assert.False(resources.TryGetProperty("memory_mb", out _));

        // But the row records the resolved size from the host cap — never NULL when a cap exists.
        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal(4, stored!.Cpus);
        Assert.Equal(8192, stored.MemoryMb);

        // The read view carries the resolved effective profile too.
        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);
        Assert.Equal(4m, view!.Resources.EffectiveCpus);
        Assert.Equal(8192, view.Resources.EffectiveMemoryMb);
    }

    [Fact]
    public async Task Create_prefers_the_offer_profile_over_the_host_cap()
    {
        // task #578 precedence: offer beats host cap. A sized offer stamps its own cpus/memory even when the
        // host advertises a (larger) per-lease cap.
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: 2, memoryMb: 4096, gpus: 0);
        fx.SetHostLimits(maxCpus: 4, maxMemoryMb: 8192);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal(2, stored!.Cpus);      // the offer's value, not the host cap
        Assert.Equal(4096, stored.MemoryMb);

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);
        Assert.Equal(2m, view!.Resources.EffectiveCpus);
        Assert.Equal(4096, view.Resources.EffectiveMemoryMb);
        Assert.Equal("offer", view.Resources.ResourcesSource);
    }

    [Fact]
    public async Task Create_leaves_the_profile_null_when_offline_and_the_offer_is_null()
    {
        // task #578: an offline host advertises no cap, so a NULL-profile offer degrades to a genuinely unknown
        // size — the row stays NULL (no cap to record) and the view marks resources_source "unknown".
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: null, memoryMb: null, gpus: 0);
        // No capability declared for the host ⇒ offline (no per-lease cap available).

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Null(stored!.Cpus);
        Assert.Null(stored.MemoryMb);

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);
        Assert.Null(view!.Resources.EffectiveCpus);
        Assert.Null(view.Resources.EffectiveMemoryMb);
        Assert.Equal("unknown", view.Resources.ResourcesSource);
    }

    [Fact]
    public async Task Get_resolves_a_legacy_null_stamped_row_from_the_live_host_cap()
    {
        // task #578: an existing NULL-stamped row (created before this task, or under an offline host) still
        // serializes. On read, its effective size is resolved compute-on-read from the host's live per-lease
        // cap so the consumer is no longer shown a blank — no migration, no re-stamp of the raw column.
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: null, memoryMb: null);
        var legacy = await fx.Leases.CreateAsync(new Lease
        {
            Id = Guid.NewGuid(),
            ConsumerUserId = fx.ConsumerId,
            HostId = fx.Host!.Id,
            HostImageId = fx.Image!.Id,
            ImageRef = fx.Image.ImageRef,
            Network = NetworkMode.Open,
            Cpus = null,          // legacy NULL stamp
            MemoryMb = null,
            TtlSeconds = 3600,
            PriceCentsPerMin = 5,
            Currency = "usd",
            Status = LeaseStatus.Active,
            CreatedAt = T0,
            StartedAt = T0,
            LastMeteredAt = T0,
        });
        fx.SetHostLimits(maxCpus: 4, maxMemoryMb: 8192);

        var view = await fx.Service().GetAsync(fx.ConsumerId, legacy.Id);

        Assert.NotNull(view);
        Assert.Null(view!.Resources.Cpus);                  // the raw column is untouched
        Assert.Null(view.Resources.MemoryMb);
        Assert.Equal(4m, view.Resources.EffectiveCpus);     // resolved on read from the host cap
        Assert.Equal(8192, view.Resources.EffectiveMemoryMb);
        Assert.Equal("host_cap", view.Resources.ResourcesSource);
    }

    [Fact]
    public async Task Create_of_a_free_offer_provisions_without_a_hold()
    {
        // Zero-price flow unchanged (task #570): a free offer still provisions the profile and places no hold.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 0, cpus: 2, memoryMb: 4096);

        var result = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Equal(0, result.HoldCents);
        var stored = await fx.Leases.GetByIdAsync(result.Lease.Id);
        Assert.Equal(LeaseStatus.Active, stored!.Status);
        Assert.Null(stored.HoldTxnId);            // a free offer earmarks nothing
        Assert.Equal(2, stored.Cpus);
        Assert.Equal(4096, stored.MemoryMb);
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
    public async Task Create_rejects_a_request_that_carries_a_resources_object()
    {
        // Resources are fixed by the offer now (task #570): a request still carrying a `resources` object is
        // rejected with validation_error before any tunnel frame, never silently ignored.
        var fx = new Fixture();
        await fx.SeedImageAsync(cpus: 2, memoryMb: 4096);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(
                fx.ConsumerId, fx.Request(resources: new LeaseResourcesRequest(4, 8192, 1024))));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Contains("fixed by the selected offer", ex.Message);
        Assert.Empty(fx.Relay.CreateCalls); // rejected before any tunnel frame
    }

    [Fact]
    public async Task Create_rejects_a_request_that_carries_a_gpu_count()
    {
        // A top-level gpu-count knob is likewise refused — the offer fixes the GPU count, the consumer cannot.
        var fx = new Fixture();
        await fx.SeedImageAsync(gpus: 2);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request(gpus: 2)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Contains("fixed by the selected offer", ex.Message);
        Assert.Empty(fx.Relay.CreateCalls); // rejected before any tunnel frame
    }

    [Fact]
    public async Task Create_provisions_the_offer_gpu_count_downstream_and_on_the_snapshot()
    {
        // GPU is priced into the offer (task #522, #570): the offer's exact gpus count travels down the
        // lease.create frame, snapshots on the row, and surfaces in the read view — no consumer choice involved.
        var fx = new Fixture();
        await fx.SeedImageAsync(gpus: 2);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal(2, spec.Resources.Gpus); // downstream frame carries the offer's gpus verbatim

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal(2, stored!.Gpus); // immutable snapshot on the lease row

        var view = await fx.Service().GetAsync(fx.ConsumerId, created.Lease.Id);
        Assert.Equal(2, view!.Resources.Gpus); // surfaced on the read view
    }

    [Fact]
    public async Task Create_omits_gpus_from_the_frame_for_a_gpu_less_offer()
    {
        // The gpus=0 path is untouched: a GPU-less offer → gpus omitted on the frame, 0 on the snapshot/view.
        var fx = new Fixture();
        await fx.SeedImageAsync(gpus: 0);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        var (_, spec) = Assert.Single(fx.Relay.CreateCalls);
        Assert.Equal(0, spec.Resources.Gpus);
        var resources = System.Text.Json.JsonDocument
            .Parse(ControlJson.Serialize(spec)).RootElement.GetProperty("resources");
        Assert.False(resources.TryGetProperty("gpus", out _));

        var stored = await fx.Leases.GetByIdAsync(created.Lease.Id);
        Assert.Equal(0, stored!.Gpus);
        Assert.Equal(0, created.Lease.Gpus);
    }

    [Fact]
    public async Task Create_fast_fails_at_capacity_when_the_host_contract_ceiling_is_reached()
    {
        // task #571: the host advertises max_contracts=2 and already runs 2 live leases. The create is refused
        // with at_capacity (409) BEFORE the wallet gate — the fixture's AllowWalletGate would otherwise provision.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        fx.SetHostMaxContracts(2);
        await fx.SeedActiveLeasesOnHostAsync(2);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));

        Assert.Equal(ApiErrorCode.AtCapacity, ex.Code);
        // The message distinguishes host-at-capacity from the per-user concurrency cap that shares the code.
        Assert.Contains("host", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fx.Relay.CreateCalls); // no tunnel frame — the fast-fail is before provisioning
    }

    [Fact]
    public async Task Create_at_capacity_posts_no_hold_and_leaves_no_lease_row()
    {
        // The fast-fail must post no wallet hold and persist nothing — a recording gate proves PlaceHold is
        // never called, and only the seeded live leases remain (no new row for the refused create).
        var gate = new RecordingWalletGate();
        var fx = new Fixture { WalletGate = gate };
        await fx.SeedImageAsync(price: 5);
        fx.SetHostMaxContracts(1);
        await fx.SeedActiveLeasesOnHostAsync(1);

        await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));

        Assert.Equal(0, gate.AuthorizeCalls); // refused before the wallet gate even authorizes
        Assert.Equal(0, gate.PlaceCalls);     // and so no hold is posted
        Assert.Equal(1, await fx.Leases.CountActiveByHostAsync(fx.Host!.Id)); // only the seeded lease remains
    }

    [Fact]
    public async Task Create_admits_when_the_host_contract_count_is_below_the_ceiling()
    {
        // Boundary: count < max admits. max_contracts=2 with a single live lease leaves room for one more.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        fx.SetHostMaxContracts(2);
        await fx.SeedActiveLeasesOnHostAsync(1);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Equal(LeaseStatus.Active, (await fx.Leases.GetByIdAsync(created.Lease.Id))!.Status);
        Assert.Single(fx.Relay.CreateCalls); // provisioned — the ceiling was not yet reached
    }

    [Theory]
    [InlineData(1, 1, false)] // count == max → the boundary rejects
    [InlineData(0, 1, true)]  // count < max  → admits
    [InlineData(2, 3, true)]  // headroom     → admits
    [InlineData(3, 3, false)] // at the ceiling → rejects
    public async Task Create_capacity_boundary_admits_below_max_and_rejects_at_max(
        int existing, int max, bool admits)
    {
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        fx.SetHostMaxContracts(max);
        await fx.SeedActiveLeasesOnHostAsync(existing);

        if (admits)
        {
            var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());
            Assert.Equal(LeaseStatus.Active, (await fx.Leases.GetByIdAsync(created.Lease.Id))!.Status);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ApiException>(() =>
                fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
            Assert.Equal(ApiErrorCode.AtCapacity, ex.Code);
            Assert.Empty(fx.Relay.CreateCalls);
        }
    }

    [Fact]
    public async Task Create_treats_a_host_with_no_advertised_ceiling_as_unlimited()
    {
        // No capacity block (an older agent, or a host that advertises none) ⇒ unlimited: many live leases
        // never trip the fast-fail. This is the pre-#571 behavior wisp still enforces authoritatively.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        fx.SetHostMaxContracts(0); // advertised, but 0 = unlimited
        await fx.SeedActiveLeasesOnHostAsync(10);

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Equal(LeaseStatus.Active, (await fx.Leases.GetByIdAsync(created.Lease.Id))!.Status);
    }

    [Fact]
    public async Task Create_treats_an_offline_host_with_no_live_capability_as_unlimited()
    {
        // A host with no live capability snapshot (offline) has no advertised ceiling, so the manager-side
        // fast-fail never fires — wisp is the enforcer. (The relay itself would surface host_offline here.)
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        await fx.SeedActiveLeasesOnHostAsync(5);
        // No capability declared for the host.

        var created = await fx.Service().CreateAsync(fx.ConsumerId, fx.Request());

        Assert.Equal(LeaseStatus.Active, (await fx.Leases.GetByIdAsync(created.Lease.Id))!.Status);
    }

    [Fact]
    public async Task Create_maps_an_agent_reported_at_capacity_to_409_and_persists_nothing()
    {
        // Race window (task #571): the manager admitted, but wisp reports 409 → the relay raises at_capacity.
        // It must surface as the 409 at_capacity API error (not lease_failed/upstream), and — since the frame
        // failed before any lease row or hold — nothing is persisted.
        var fx = new Fixture();
        await fx.SeedImageAsync(price: 5);
        fx.Relay.CreateError = new ApiException(ApiErrorCode.AtCapacity, "wisp reported 409 at capacity");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));

        Assert.Equal(ApiErrorCode.AtCapacity, ex.Code);
        Assert.Empty(await fx.Leases.ListByConsumerAsync(fx.ConsumerId));
        Assert.Empty(fx.Relay.ReleaseCalls); // the frame failed pre-provision — nothing to tear down
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

    [Fact]
    public async Task Create_tears_down_the_contract_and_marks_the_lease_failed_when_the_hold_post_fails()
    {
        // task #540: the hold posts AFTER the lease row is persisted and the container is already live on the
        // host. If it fails (e.g. the wallet drained in the AuthorizeHold→PlaceHold race), the downstream
        // contract must be torn down (no zombie riding out its TTL) and the lease row marked failed.
        var fx = new Fixture
        {
            WalletGate = new HoldFailsWalletGate(
                new ApiException(ApiErrorCode.InsufficientFunds, "wallet drained between gate and hold")),
        };
        await fx.SeedImageAsync(price: 5);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(ApiErrorCode.InsufficientFunds, ex.Code);

        // The container was provisioned, so exactly one lease.create reached the host — and its contract was
        // torn down with a matching lease.release addressed by the same lease id.
        var (_, createdSpec) = Assert.Single(fx.Relay.CreateCalls);
        var (releaseHostId, releaseLeaseId) = Assert.Single(fx.Relay.ReleaseCalls);
        Assert.Equal(fx.Host!.Id.ToString(), releaseHostId);
        Assert.Equal(fx.Relay.LastLeaseId, releaseLeaseId);

        // The persisted row exists but is failed (not left active/zombie), keyed by the same relay lease id.
        Assert.True(TunnelLeaseId.TryParse(fx.Relay.LastLeaseId, out var leaseGuid));
        var stored = await fx.Leases.GetByIdAsync(leaseGuid);
        Assert.NotNull(stored);
        Assert.Equal(LeaseStatus.Failed, stored!.Status);
        Assert.Equal(LeaseEndReason.PaymentFailed, stored.EndReason);
        Assert.NotNull(stored.EndedAt);
    }

    [Fact]
    public async Task Create_still_fails_when_the_hold_fails_and_the_host_is_already_offline_for_teardown()
    {
        // The teardown is best-effort: if the host has since gone offline, the container is already gone, so a
        // host_offline on lease.release must not mask the original hold failure — the create still fails and
        // the lease row is still marked failed.
        var fx = new Fixture
        {
            WalletGate = new HoldFailsWalletGate(
                new ApiException(ApiErrorCode.InsufficientFunds, "wallet drained")),
        };
        await fx.SeedImageAsync(price: 5);
        fx.Relay.ReleaseError = new ApiException(ApiErrorCode.HostOffline, "no live tunnel");

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            fx.Service().CreateAsync(fx.ConsumerId, fx.Request()));
        Assert.Equal(ApiErrorCode.InsufficientFunds, ex.Code); // the hold failure, not the teardown's offline

        Assert.True(TunnelLeaseId.TryParse(fx.Relay.LastLeaseId, out var leaseGuid));
        var stored = await fx.Leases.GetByIdAsync(leaseGuid);
        Assert.Equal(LeaseStatus.Failed, stored!.Status);
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

    /// <summary>
    /// A permissive gate that records whether it was asked to authorize/place a hold — used to prove the
    /// per-host fast-fail (task #571) refuses BEFORE the wallet gate is consulted (no authorize, no hold).
    /// </summary>
    private sealed class RecordingWalletGate : ILeaseWalletGate
    {
        public int AuthorizeCalls { get; private set; }
        public int PlaceCalls { get; private set; }

        public Task<WalletGateDecision> AuthorizeHoldAsync(
            Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(WalletGateDecision.Allow());
        }

        public Task<Guid?> PlaceHoldAsync(
            Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default)
        {
            PlaceCalls++;
            return Task.FromResult<Guid?>(null);
        }

        public Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default) => Task.CompletedTask;
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

    /// <summary>
    /// A gate that authorizes the hold (so the create provisions the container) but then throws when the hold
    /// is placed — the post-provision failure the task-#540 teardown must handle (tear the contract down, mark
    /// the lease failed). Models the rare AuthorizeHold→PlaceHold drain race and any ledger error.
    /// </summary>
    private sealed class HoldFailsWalletGate : ILeaseWalletGate
    {
        private readonly Exception _failure;

        public HoldFailsWalletGate(Exception failure) => _failure = failure;

        public Task<WalletGateDecision> AuthorizeHoldAsync(
            Guid consumerUserId, long holdCents, string currency, CancellationToken ct = default) =>
            Task.FromResult(WalletGateDecision.Allow());

        public Task<Guid?> PlaceHoldAsync(
            Guid consumerUserId, Guid leaseId, long holdCents, string currency, CancellationToken ct = default) =>
            throw _failure;

        public Task ReleaseHoldAsync(Guid leaseId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
