using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Domain;
using Wisper.Api.Hosts;
using Wisper.Api.Infrastructure;
using Wisper.Api.Ledger;
using Wisper.Api.Payouts;
using Wisper.Api.Persistence.HostImages;
using Wisper.Api.Persistence.Hosts;
using Wisper.Api.Persistence.Leases;
using Wisper.Api.Persistence.Payouts;
using Wisper.Api.Persistence.Users;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Wisper.Api.Tunnel.Backplane;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Hosts;

/// <summary>
/// Unit tests for <see cref="HostService"/> (docs/API.md §6, P7.1) over the in-memory repositories + fakes
/// (Grunt has no Postgres/tunnel): register issuing a hashed agent token shown once, <c>GET /v1/hosts/mine</c>,
/// token rotation revoking + closing the tunnel (4402), and the priced allow-list validated live against the
/// host's advertised wisp capability.
/// </summary>
public class HostServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture
    {
        public InMemoryHostRepository Hosts { get; } = new();
        public InMemoryHostImageRepository Images { get; } = new();
        public InMemoryLeaseRepository Leases { get; } = new();
        public InMemoryUserRepository Users { get; } = new();
        public FakeHostRegistry Registry { get; } = new();
        public InMemoryHostPresenceStore PresenceStore { get; } = new();
        public FakeHostCapabilitySource Capabilities { get; } = new();
        public FakeAgentTunnelCloser TunnelCloser { get; } = new();
        public FakeUserRoleGranter RoleGranter { get; } = new();
        public FakeTimeProvider Clock { get; } = new(T0);
        public TunnelOptions Tunnel { get; } = new() { ManagerWebSocketUrl = "wss://wisper.test/agent" };
        public PayoutService Payouts { get; }
        public HostService Service { get; }

        public Fixture()
        {
            var ledger = new LedgerService(new InMemoryLedgerStore());
            Payouts = new PayoutService(
                ledger, new InMemoryPayoutRepository(), Users, Hosts, new FakeStripeConnectGateway(),
                new Wisper.Api.Audit.AuditService(
                    new Wisper.Api.Persistence.Audit.InMemoryAuditLogRepository(), Clock),
                Options.Create(new PayoutOptions()), Clock, NullLogger<PayoutService>.Instance);
            Service = new HostService(
                Hosts, Images, Leases, Users, Registry, PresenceStore, Capabilities, TunnelCloser, Payouts,
                RoleGranter, Options.Create(Tunnel), Clock, NullLogger<HostService>.Instance);
        }

        /// <summary>
        /// Seeds a stored host owned by <paramref name="owner"/> with a known token hash. The owner's
        /// <see cref="ConnectStatus"/> defaults to <see cref="ConnectStatus.Enabled"/> so priced allow-lists
        /// are accepted; a caller pricing a non-zero image for a non-Connect owner passes the state under test.
        /// </summary>
        public async Task<Host> SeedHostAsync(
            Guid owner, string token = "wht_live_seed", ConnectStatus connect = ConnectStatus.Enabled)
        {
            await EnsureOwnerAsync(owner, connect);
            return await Hosts.CreateAsync(new Host
            {
                OwnerUserId = owner,
                Name = "home-server-1",
                Label = "us",
                Status = HostStatus.Offline,
                AgentTokenHash = HostAgentToken.Hash(token),
                AgentTokenPrefix = "wht_live_seed",
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }

        /// <summary>Ensures a <see cref="User"/> row exists for <paramref name="owner"/> with a given Connect state.</summary>
        public async Task EnsureOwnerAsync(Guid owner, ConnectStatus connect)
        {
            if (await Users.GetByIdAsync(owner) is { } existing)
            {
                if (existing.ConnectStatus != connect)
                {
                    await Users.UpdateAsync(existing with { ConnectStatus = connect, UpdatedAt = T0 });
                }

                return;
            }

            await Users.CreateAsync(new User
            {
                Id = owner,
                CognitoSub = $"sub-{owner}",
                Email = $"{owner}@hosts.test",
                ConnectStatus = connect,
                CreatedAt = T0,
                UpdatedAt = T0,
            });
        }

        /// <summary>A generous capability so valid entries pass; callers override for negative cases.</summary>
        public HostCapabilitySnapshot Capability(params string[] images) => Capability(0, images);

        /// <summary>As <see cref="Capability(string[])"/> but advertising <paramref name="maxGpus"/> GPU devices.</summary>
        public HostCapabilitySnapshot Capability(int maxGpus, params string[] images) => new(
            Images: images.Length == 0 ? new[] { "alpine:latest" } : images,
            Networks: new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds: 14400,
            MaxCpus: 8,
            MaxMemoryMb: 16384,
            MaxPids: 4096,
            MaxGpus: maxGpus);

        /// <summary>
        /// A capability advertising specific per-lease cpu/memory caps (wisp <c>limits.max_cpus</c>/
        /// <c>max_memory_mb</c>). A <c>0</c> cap means the host advertises no bound on that dimension (task #583).
        /// </summary>
        public HostCapabilitySnapshot CappedCapability(
            double maxCpus, long maxMemoryMb, params string[] images) => new(
            Images: images.Length == 0 ? new[] { "alpine:latest" } : images,
            Networks: new[] { NetworkMode.None, NetworkMode.Open },
            MaxTtlSeconds: 14400,
            MaxCpus: maxCpus,
            MaxMemoryMb: maxMemoryMb,
            MaxPids: 4096);
    }

    /// <summary>Reads a named property off an <see cref="ApiException.Details"/> anonymous payload.</summary>
    private static object? Detail(ApiException ex, string name) =>
        ex.Details?.GetType().GetProperty(name)?.GetValue(ex.Details);

    [Fact]
    public async Task Register_issues_token_once_and_stores_only_the_hash()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();

        var result = await fx.Service.RegisterAsync(owner, new RegisterHostRequest("home-server-1", "us"));

        Assert.StartsWith(HostAgentToken.Prefix, result.AgentToken);
        Assert.StartsWith(HostAgentToken.Prefix, result.AgentTokenPrefix);
        Assert.Equal("wss://wisper.test/agent", result.ManagerWs);
        Assert.Equal("offline", result.Status);

        var stored = await fx.Hosts.GetByIdAsync(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(owner, stored!.OwnerUserId);
        // Only the hash + prefix are persisted; the clear token never lands in the row.
        Assert.Equal(HostAgentToken.Hash(result.AgentToken), stored.AgentTokenHash);
        Assert.Equal(result.AgentTokenPrefix, stored.AgentTokenPrefix);
        Assert.DoesNotContain(result.AgentToken, stored.AgentTokenHash);
    }

    [Fact]
    public async Task Register_grants_the_host_role_to_the_owner()
    {
        // Becoming a host is additive (docs/API.md §184): a successful register grants the owning subject the
        // host group so their next token carries it.
        var fx = new Fixture();
        var owner = Guid.NewGuid();

        await fx.Service.RegisterAsync(owner, new RegisterHostRequest("h1", null), "cognito-owner");

        Assert.True(fx.RoleGranter.HostGrants.TryDequeue(out var grantedSub));
        Assert.Equal("cognito-owner", grantedSub);
    }

    [Fact]
    public async Task Register_skips_the_grant_when_no_subject_is_supplied()
    {
        // An api-key principal (no Cognito subject to grant) or a DB-less path passes no subject — the grant
        // is skipped and registration still succeeds.
        var fx = new Fixture();
        var owner = Guid.NewGuid();

        var result = await fx.Service.RegisterAsync(owner, new RegisterHostRequest("h1", null));

        Assert.StartsWith(HostAgentToken.Prefix, result.AgentToken);
        Assert.Empty(fx.RoleGranter.HostGrants);
    }

    [Fact]
    public async Task Register_still_succeeds_when_the_grant_fails()
    {
        // The host row is already committed, so a transient group-write failure is logged and swallowed — it
        // must never fail registration (the role reconciles on the next login / from owned hosts).
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        fx.RoleGranter.Throws = new InvalidOperationException("cognito unavailable");

        var result = await fx.Service.RegisterAsync(owner, new RegisterHostRequest("h1", null), "cognito-owner");

        Assert.StartsWith(HostAgentToken.Prefix, result.AgentToken);
        Assert.NotNull(await fx.Hosts.GetByIdAsync(result.Id));
        Assert.True(fx.RoleGranter.HostGrants.TryDequeue(out _)); // the grant was attempted
    }

    [Fact]
    public async Task ListMine_returns_owned_hosts_with_presence_and_earnings()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var online = await fx.SeedHostAsync(owner);
        var offline = await fx.SeedHostAsync(owner);
        await fx.SeedHostAsync(Guid.NewGuid()); // another owner's host — must not appear
        fx.Registry.SetOnline(online.Id);

        var result = await fx.Service.ListMineAsync(owner);

        Assert.Equal(2, result.Data.Count);
        Assert.True(result.Data.Single(h => h.Id == online.Id).Online);
        Assert.False(result.Data.Single(h => h.Id == offline.Id).Online);
        Assert.Equal("usd", result.Earnings.Currency);
        Assert.Equal(0, result.Earnings.AccruedCents);
    }

    [Fact]
    public async Task Rotate_replaces_the_hash_and_closes_the_live_tunnel_4402()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        var oldHash = host.AgentTokenHash;
        fx.TunnelCloser.SetLive(host.Id);

        var result = await fx.Service.RotateAgentTokenAsync(owner, host.Id);

        Assert.StartsWith(HostAgentToken.Prefix, result.AgentToken);
        Assert.True(result.TunnelClosed);

        var stored = await fx.Hosts.GetByIdAsync(host.Id);
        Assert.Equal(HostAgentToken.Hash(result.AgentToken), stored!.AgentTokenHash);
        Assert.NotEqual(oldHash, stored.AgentTokenHash);

        Assert.True(fx.TunnelCloser.Closes.TryDequeue(out var close));
        Assert.Equal(host.Id, close.HostId);
        Assert.Equal(CloseCodes.Revoked, close.CloseCode);
    }

    [Fact]
    public async Task Rotate_reports_no_tunnel_closed_when_offline_but_still_rotates()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);

        var result = await fx.Service.RotateAgentTokenAsync(owner, host.Id);

        Assert.False(result.TunnelClosed);
        var stored = await fx.Hosts.GetByIdAsync(host.Id);
        Assert.Equal(HostAgentToken.Hash(result.AgentToken), stored!.AgentTokenHash);
    }

    [Fact]
    public async Task Rotate_of_unowned_host_is_not_found()
    {
        var fx = new Fixture();
        var host = await fx.SeedHostAsync(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.RotateAgentTokenAsync(Guid.NewGuid(), host.Id));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_validates_and_persists_the_allow_list()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest", "ubuntu:24.04"));

        var request = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none", "open" }, 3600, 2m, 2048, 512, true),
            new ImageUpsert("ubuntu:24.04", 9, new[] { "none" }, 7200, null, null, null, false),
        });

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, request);

        Assert.Equal(2, result.Data.Count);
        var alpine = result.Data.Single(i => i.ImageRef == "alpine:latest");
        Assert.Equal(5, alpine.PriceCentsPerMin);
        Assert.Equal(new[] { "none", "open" }, alpine.Networks);
        Assert.True(alpine.Enabled);
        Assert.False(result.Data.Single(i => i.ImageRef == "ubuntu:24.04").Enabled);
    }

    [Fact]
    public async Task ReplaceImages_rejects_an_image_outside_capability()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var request = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("nope:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        });

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, request));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id));
    }

    [Fact]
    public async Task ReplaceImages_rejects_a_network_or_limit_the_host_does_not_permit()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var badNetwork = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "egress" }, 3600, null, null, null, true),
        });
        var netEx = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, badNetwork));
        Assert.Equal(ApiErrorCode.ValidationError, netEx.Code);

        var badTtl = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 999999, null, null, null, true),
        });
        var ttlEx = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, badTtl));
        Assert.Equal(ApiErrorCode.ValidationError, ttlEx.Code);
    }

    [Fact]
    public async Task ReplaceImages_requires_a_live_tunnel()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // No capability declared → host offline for validation.

        var request = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        });

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, request));
        Assert.Equal(ApiErrorCode.HostOffline, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_replaces_the_prior_set()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest", "ubuntu:24.04"));

        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));

        // A second replace drops alpine and adds ubuntu.
        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("ubuntu:24.04", 7, new[] { "open" }, 3600, null, null, null, true),
        }));

        Assert.Single(result.Data);
        Assert.Equal("ubuntu:24.04", result.Data[0].ImageRef);
    }

    [Fact]
    public async Task PatchImage_updates_one_entry_and_revalidates()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(PriceCentsPerMin: 12, Networks: null, MaxTtlSeconds: null,
                MaxCpus: null, MaxMemoryMb: null, MaxPids: null, Enabled: false));

        Assert.Equal(12, patched.PriceCentsPerMin);
        Assert.False(patched.Enabled);
        Assert.Equal(new[] { "none" }, patched.Networks); // unchanged
    }

    [Fact]
    public async Task PatchImage_rejects_a_limit_over_capability()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, imageId,
                new PatchImageRequest(null, null, MaxTtlSeconds: 999999, null, null, null, null)));
        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_accepts_gpus_within_the_live_capability_and_persists_it()
    {
        // GPU access is priced into the sized offer (task #569): a host advertising GPU devices may sell an
        // offer that provisions an exact gpus count up to its advertised count, validated live.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 4, "alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Gpus: 2),
        }));

        Assert.Equal(2, Assert.Single(result.Data).Gpus);
        Assert.Equal(2, (await fx.Images.ListByHostAsync(host.Id))[0].Gpus);
    }

    [Fact]
    public async Task ReplaceImages_rejects_gpus_over_the_live_capability()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 1, "alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Gpus: 2),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id)); // nothing persisted on rejection
    }

    [Fact]
    public async Task ReplaceImages_rejects_any_gpu_offer_on_a_gpu_less_host()
    {
        // A host advertising 0 GPU devices genuinely has none — there is no "0 means unlimited" escape here,
        // so any offer above 0 is rejected (unlike the cpu/mem/pid ceilings, where 0 means unadvertised).
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 0, "alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Gpus: 1),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_defaults_gpus_to_zero_when_omitted()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 4, "alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));

        Assert.Equal(0, Assert.Single(result.Data).Gpus); // no GPU provisioned by default
    }

    [Fact]
    public async Task ReplaceImages_round_trips_the_sized_cpu_and_memory_profile()
    {
        // The sized offer sells a FIXED resource profile (task #569): cpus/memory_mb are the exact per-lease
        // provisioning and round-trip through the offer surface.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true,
                Cpus: 2, MemoryMb: 4096),
        }));

        var view = Assert.Single(result.Data);
        Assert.Equal(2, view.Cpus);
        Assert.Equal(4096, view.MemoryMb);
        var stored = (await fx.Images.ListByHostAsync(host.Id))[0];
        Assert.Equal(2, stored.Cpus);
        Assert.Equal(4096, stored.MemoryMb);
    }

    [Fact]
    public async Task ReplaceImages_leaves_the_sized_profile_null_when_omitted()
    {
        // NULL cpus/memory_mb means the host's own per-lease policy default applies downstream (task #570).
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));

        var view = Assert.Single(result.Data);
        Assert.Null(view.Cpus);
        Assert.Null(view.MemoryMb);
    }

    [Fact]
    public async Task ReplaceImages_rejects_a_non_positive_sized_cpu_count()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Cpus: 0),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id));
    }

    [Fact]
    public async Task ReplaceImages_rejects_a_non_positive_sized_memory()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, MemoryMb: -1),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_rejects_cpus_over_the_host_per_lease_cap()
    {
        // Offer honesty (task #583): a host must not over-promise. The live gap — an offer of 4 cpus on a host
        // whose wisp per-lease cap is 2 — is rejected with the field AND the cap in details, never clamped.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Cpus: 4),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Equal("cpus", Detail(ex, "field"));
        Assert.Equal(2d, Convert.ToDouble(Detail(ex, "max"))); // the cap is named so the editor can correct it
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id)); // nothing persisted on rejection
    }

    [Fact]
    public async Task ReplaceImages_rejects_memory_over_the_host_per_lease_cap()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true,
                    MemoryMb: 4194304),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Equal("memory_mb", Detail(ex, "field"));
        Assert.Equal(2048L, Convert.ToInt64(Detail(ex, "max")));
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id));
    }

    [Fact]
    public async Task ReplaceImages_accepts_a_profile_at_the_host_cap()
    {
        // The cap is inclusive: an offer exactly at the advertised per-lease cap is honest and accepted.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true,
                Cpus: 2, MemoryMb: 2048),
        }));

        var view = Assert.Single(result.Data);
        Assert.Equal(2, view.Cpus);
        Assert.Equal(2048, view.MemoryMb);
    }

    [Fact]
    public async Task ReplaceImages_leaves_a_dimension_unbounded_when_the_host_advertises_no_cap()
    {
        // A host that advertises 0 for a dimension imposes no bound on it (task #583): a large cpu profile is
        // accepted when max_cpus is unadvertised, even though the memory cap still applies.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 0, maxMemoryMb: 2048, "alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true,
                Cpus: 64, MemoryMb: 2048),
        }));

        Assert.Equal(64, Assert.Single(result.Data).Cpus);
    }

    [Fact]
    public async Task ReplaceImages_rejects_the_whole_list_when_one_offer_is_over_cap()
    {
        // PUT is a whole-list replace: one over-cap offer fails the entire save, so a good entry alongside it is
        // not persisted either. This also pins that existing over-cap rows simply fail the NEXT save (task #583).
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(
            host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest", "ubuntu:24.04"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Cpus: 2),
                new ImageUpsert("ubuntu:24.04", 9, new[] { "none" }, 3600, null, null, null, true, Cpus: 4),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id)); // whole list rejected — nothing persisted
    }

    [Fact]
    public async Task PatchImage_rejects_a_profile_over_the_host_per_lease_cap()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Cpus: 2),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, imageId,
                new PatchImageRequest(null, null, null, null, null, null, null, Cpus: 8)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Equal("cpus", Detail(ex, "field"));
        Assert.Equal(2, (await fx.Images.ListByHostAsync(host.Id))[0].Cpus); // unchanged on rejection
    }

    [Fact]
    public async Task ListMine_surfaces_the_host_caps_when_online_and_null_when_offline()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var online = await fx.SeedHostAsync(owner);
        var offline = await fx.SeedHostAsync(owner);
        fx.Registry.SetOnline(online.Id);
        fx.Capabilities.Set(online.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));

        var result = await fx.Service.ListMineAsync(owner);

        var onlineRow = result.Data.Single(h => h.Id == online.Id);
        Assert.Equal(2m, onlineRow.HostMaxCpus);
        Assert.Equal(2048, onlineRow.HostMaxMemoryMb);
        var offlineRow = result.Data.Single(h => h.Id == offline.Id);
        Assert.Null(offlineRow.HostMaxCpus);
        Assert.Null(offlineRow.HostMaxMemoryMb);
    }

    [Fact]
    public async Task ListImages_surfaces_the_host_caps_when_online_and_null_when_offline()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);

        // Offline: the caps resolve null (GET does not require a live tunnel).
        var offline = await fx.Service.ListImagesAsync(owner, host.Id);
        Assert.Null(offline.HostMaxCpus);
        Assert.Null(offline.HostMaxMemoryMb);

        // Online: the advertised per-lease caps ride the same response the editor already fetches.
        fx.Capabilities.Set(host.Id, fx.CappedCapability(maxCpus: 2, maxMemoryMb: 2048, "alpine:latest"));
        var online = await fx.Service.ListImagesAsync(owner, host.Id);
        Assert.Equal(2m, online.HostMaxCpus);
        Assert.Equal(2048, online.HostMaxMemoryMb);
    }

    [Fact]
    public async Task PatchImage_rejects_gpus_over_the_live_capability()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 2, "alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Gpus: 1),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, imageId,
                new PatchImageRequest(null, null, null, null, null, null, null, Gpus: 3)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Equal(1, (await fx.Images.ListByHostAsync(host.Id))[0].Gpus); // unchanged on rejection
    }

    [Fact]
    public async Task PatchImage_updates_gpus_within_the_live_capability()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability(maxGpus: 4, "alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true, Gpus: 1),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(null, null, null, null, null, null, null, Gpus: 3));

        Assert.Equal(3, patched.Gpus);
    }

    [Fact]
    public async Task PatchImage_updates_the_sized_cpu_and_memory_profile()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(null, null, null, null, null, null, null, Cpus: 4, MemoryMb: 8192));

        Assert.Equal(4, patched.Cpus);
        Assert.Equal(8192, patched.MemoryMb);
    }

    [Fact]
    public async Task PatchImage_of_missing_image_is_not_found()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, Guid.NewGuid(),
                new PatchImageRequest(PriceCentsPerMin: 1, null, null, null, null, null, null)));
        Assert.Equal(ApiErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task ReplaceImages_accepts_a_zero_price_for_a_free_self_hosted_image()
    {
        // price 0 is a valid price (>= 0) so a self-hosted operator can list their own box at cost — the
        // wallet gate then places a 0-cent hold and needs no funding (docs/PAYMENTS.md §4, docs/API.md §6).
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 0, new[] { "none", "open" }, 3600, null, null, null, true),
        }));

        Assert.Equal(0, Assert.Single(result.Data).PriceCentsPerMin); // accepted and persisted, not rejected
        Assert.Equal(0, (await fx.Images.ListByHostAsync(host.Id))[0].PriceCentsPerMin);
    }

    [Fact]
    public async Task ReplaceImages_rejects_a_negative_price()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", -1, new[] { "none" }, 3600, null, null, null, true),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id)); // nothing persisted on rejection
    }

    [Fact]
    public async Task PatchImage_can_drop_a_paid_image_to_a_free_zero_price()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(PriceCentsPerMin: 0, null, null, null, null, null, null));

        Assert.Equal(0, patched.PriceCentsPerMin);
    }

    [Fact]
    public async Task ReplaceImages_rejects_enabling_a_priced_image_for_a_non_connect_owner()
    {
        // A non-zero-priced, enabled image cannot be advertised until the owner completes Connect (§6): the
        // gate is enforced at the pricing mutation so the host never moves into the earning arm mid-tunnel.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner, connect: ConnectStatus.Pending);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
            {
                new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
            })));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.Empty(await fx.Images.ListByHostAsync(host.Id)); // nothing persisted on rejection
    }

    [Fact]
    public async Task ReplaceImages_accepts_a_zero_priced_image_for_a_non_connect_owner()
    {
        // The self-hosted / zero-price posture (task #386): a host that earns nothing needs no Connect.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner, connect: ConnectStatus.None);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 0, new[] { "none", "open" }, 3600, null, null, null, true),
        }));

        Assert.Equal(0, Assert.Single(result.Data).PriceCentsPerMin);
    }

    [Fact]
    public async Task ReplaceImages_accepts_a_disabled_priced_image_for_a_non_connect_owner()
    {
        // A priced but disabled entry never advertises/earns, so Connect is not yet required to stage it.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner, connect: ConnectStatus.None);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, false),
        }));

        Assert.False(Assert.Single(result.Data).Enabled);
    }

    [Fact]
    public async Task PatchImage_rejects_enabling_a_priced_image_for_a_non_connect_owner()
    {
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        // Seed a Connect-enabled owner so a priced image can be staged, then drop Connect and try to keep it.
        var host = await fx.SeedHostAsync(owner, connect: ConnectStatus.Enabled);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, false),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;
        await fx.EnsureOwnerAsync(owner, ConnectStatus.Restricted);

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, imageId,
                new PatchImageRequest(null, null, null, null, null, null, Enabled: true)));

        Assert.Equal(ApiErrorCode.ValidationError, ex.Code);
        Assert.False((await fx.Images.ListByHostAsync(host.Id))[0].Enabled); // stayed disabled
    }

    // ---- Disable-without-capability tests (task #19) ------------------------------------------------

    [Fact]
    public async Task PatchImage_disable_succeeds_when_image_absent_from_capability()
    {
        // AC63: PATCH {enabled:false} must succeed even when the image is no longer in the live
        // wisp capability — that is exactly the case where the owner needs to retract the stale offer.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        // Host now advertises ubuntu:24.04 only — alpine:latest is absent from the capability.
        fx.Capabilities.Set(host.Id, fx.Capability("ubuntu:24.04"));

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(null, null, null, null, null, null, Enabled: false));

        Assert.False(patched.Enabled);
        Assert.False((await fx.Images.GetByIdAsync(imageId))!.Enabled);
    }

    [Fact]
    public async Task PatchImage_disable_succeeds_when_host_offline()
    {
        // AC64: PATCH {enabled:false} must not require a live tunnel — disabling is safe even when
        // the host has gone offline entirely.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, true),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id))[0].Id;

        fx.Capabilities.Clear(host.Id); // host is now offline

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(null, null, null, null, null, null, Enabled: false));

        Assert.False(patched.Enabled);
    }

    [Fact]
    public async Task PatchImage_enable_still_requires_live_capability()
    {
        // AC65: enabling must still validate against the live capability — the safe-disable
        // exemption must not weaken the enable gate.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // Seed the image row directly so we can test the gate without going through ReplaceImages.
        await fx.Images.CreateAsync(new HostImage
        {
            HostId = host.Id,
            ImageRef = "alpine:latest",
            PriceCentsPerMin = 5,
            Networks = new[] { NetworkMode.None },
            MaxTtlSeconds = 3600,
            Enabled = false,
            CreatedAt = T0,
            UpdatedAt = T0,
        });
        var imageId = (await fx.Images.ListByHostAsync(host.Id, enabledOnly: false))[0].Id;
        // No capability set → host is offline.

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => fx.Service.PatchImageAsync(
                owner, host.Id, imageId,
                new PatchImageRequest(null, null, null, null, null, null, Enabled: true)));

        Assert.Equal(ApiErrorCode.HostOffline, ex.Code);
        Assert.False((await fx.Images.GetByIdAsync(imageId))!.Enabled); // unchanged
    }

    [Fact]
    public async Task PatchImage_disabled_row_price_change_skips_capability_check()
    {
        // AC67: a price change on an already-disabled row must not require capability membership —
        // the row cannot reach the catalog until re-enabled (which re-validates), so the check is
        // unnecessary friction.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("alpine:latest"));
        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, false),
        }));
        var imageId = (await fx.Images.ListByHostAsync(host.Id, enabledOnly: false))[0].Id;

        fx.Capabilities.Clear(host.Id); // host goes offline — price change must still succeed

        var patched = await fx.Service.PatchImageAsync(
            owner, host.Id, imageId,
            new PatchImageRequest(PriceCentsPerMin: 10, null, null, null, null, null, null));

        Assert.False(patched.Enabled);
        Assert.Equal(10, patched.PriceCentsPerMin);
    }

    [Fact]
    public async Task ReplaceImages_disabled_entry_exempt_from_capability_check()
    {
        // AC66: a PUT entry with enabled:false must not fail capability-membership validation —
        // the same safe-disable principle as PATCH.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // Capability only allows ubuntu:24.04 — alpine:latest is absent.
        fx.Capabilities.Set(host.Id, fx.Capability("ubuntu:24.04"));

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, false),
        }));

        Assert.False(Assert.Single(result.Data).Enabled);
    }

    [Fact]
    public async Task ReplaceImages_disabled_entry_succeeds_when_host_offline()
    {
        // Corollary of AC64/AC66: a PUT list composed entirely of disabled entries must not
        // require a live tunnel — no enabled entry triggers the capability gate.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // No capability set → host is offline.

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("alpine:latest", 5, new[] { "none" }, 3600, null, null, null, false),
        }));

        Assert.False(Assert.Single(result.Data).Enabled);
    }

    // ---- Distributed liveness (cross-instance) tests ------------------------------------------------

    [Fact]
    public async Task ListMine_reports_online_when_tunnel_is_on_another_instance()
    {
        // Acceptance criterion #48: hosts/mine online=true when presence store has an owner but local registry empty.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // Local registry is empty (simulates instance B); presence store records an owner from instance A.
        await fx.PresenceStore.SetOwnerAsync(host.Id.ToString(), "instance-a");

        var result = await fx.Service.ListMineAsync(owner);

        var row = Assert.Single(result.Data);
        Assert.True(row.Online);
    }

    [Fact]
    public async Task ListMine_reports_offline_when_absent_from_both_registry_and_presence_store()
    {
        // A host with no live tunnel on any instance must report online=false.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        // Neither local registry nor presence store have an entry.

        var result = await fx.Service.ListMineAsync(owner);

        var row = Assert.Single(result.Data);
        Assert.False(row.Online);
    }

    // ---- Image replace-list + lease history (soft-delete) tests ------------------------------------

    [Fact]
    public async Task ReplaceImages_dropping_image_with_ended_lease_disables_it_instead_of_deleting()
    {
        // AC58/AC61: omitting an image that has lease history must soft-disable it (enabled=false)
        // not hard-delete it, so the FK from the historical lease row stays valid.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("wisp-base:1", "ubuntu:24.04"));

        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("wisp-base:1", 5, new[] { "none" }, 3600, null, null, null, true),
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));
        var wispImage = (await fx.Images.ListByHostAsync(host.Id, enabledOnly: false))
            .First(i => i.ImageRef == "wisp-base:1");

        // Seed an ended lease against wisp-base:1.
        var consumer = Guid.NewGuid();
        var lease = await fx.Leases.CreateAsync(new Lease
        {
            ConsumerUserId = consumer,
            HostId = host.Id,
            HostImageId = wispImage.Id,
            ImageRef = "wisp-base:1",
            TtlSeconds = 3600,
            PriceCentsPerMin = 5,
            Status = LeaseStatus.Ended,
            EndReason = LeaseEndReason.Expired,
            EndedAt = T0,
            CreatedAt = T0,
        });

        // Replace with ubuntu only — wisp-base:1 is omitted.
        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));

        // Response contains only the submitted image (soft-disabled leftover excluded).
        Assert.Single(result.Data);
        Assert.Equal("ubuntu:24.04", result.Data[0].ImageRef);

        // wisp-base:1 row still exists, but is disabled.
        var wispStored = await fx.Images.GetByIdAsync(wispImage.Id);
        Assert.NotNull(wispStored);
        Assert.False(wispStored!.Enabled);

        // Catalog (enabledOnly:true) no longer surfaces it.
        var catalog = await fx.Images.ListByHostAsync(host.Id, enabledOnly: true);
        Assert.DoesNotContain(catalog, i => i.ImageRef == "wisp-base:1");

        // The historical lease FK target still resolves.
        var storedLease = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.NotNull(storedLease);
        Assert.Equal(wispImage.Id, storedLease!.HostImageId);
    }

    [Fact]
    public async Task ReplaceImages_dropping_image_with_active_lease_disables_it_not_deletes()
    {
        // AC58/AC61: an image with an active lease must also be soft-disabled — the active lease must
        // keep its FK target and continue unaffected.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("wisp-base:1", "ubuntu:24.04"));

        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("wisp-base:1", 5, new[] { "none" }, 3600, null, null, null, true),
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));
        var wispImage = (await fx.Images.ListByHostAsync(host.Id, enabledOnly: false))
            .First(i => i.ImageRef == "wisp-base:1");

        // Seed an active lease against wisp-base:1.
        var consumer = Guid.NewGuid();
        var lease = await fx.Leases.CreateAsync(new Lease
        {
            ConsumerUserId = consumer,
            HostId = host.Id,
            HostImageId = wispImage.Id,
            ImageRef = "wisp-base:1",
            TtlSeconds = 3600,
            PriceCentsPerMin = 5,
            Status = LeaseStatus.Active,
            StartedAt = T0,
            CreatedAt = T0,
        });

        // Replace with ubuntu only — wisp-base:1 omitted.
        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));

        Assert.Single(result.Data);
        Assert.Equal("ubuntu:24.04", result.Data[0].ImageRef);

        // wisp-base:1 is disabled, not deleted.
        var wispStored = await fx.Images.GetByIdAsync(wispImage.Id);
        Assert.NotNull(wispStored);
        Assert.False(wispStored!.Enabled);

        // Active lease is unaffected.
        var storedLease = await fx.Leases.GetByIdAsync(lease.Id);
        Assert.NotNull(storedLease);
        Assert.Equal(LeaseStatus.Active, storedLease!.Status);
        Assert.Equal(wispImage.Id, storedLease.HostImageId);
    }

    [Fact]
    public async Task ReplaceImages_dropping_image_with_no_lease_hard_deletes_it()
    {
        // AC59: images with zero lease references must be hard-deleted to keep the table clean.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("wisp-base:1", "ubuntu:24.04"));

        await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("wisp-base:1", 5, new[] { "none" }, 3600, null, null, null, true),
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));
        var wispImage = (await fx.Images.ListByHostAsync(host.Id, enabledOnly: false))
            .First(i => i.ImageRef == "wisp-base:1");

        // No leases seeded against wisp-base:1.

        var result = await fx.Service.ReplaceImagesAsync(owner, host.Id, new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        }));

        Assert.Single(result.Data);
        Assert.Equal("ubuntu:24.04", result.Data[0].ImageRef);

        // wisp-base:1 row is gone.
        var wispStored = await fx.Images.GetByIdAsync(wispImage.Id);
        Assert.Null(wispStored);
    }

    [Fact]
    public async Task ReplaceImages_is_idempotent_when_repeated_with_the_same_list()
    {
        // AC62: the same PUT applied twice must succeed and return consistent results.
        var fx = new Fixture();
        var owner = Guid.NewGuid();
        var host = await fx.SeedHostAsync(owner);
        fx.Capabilities.Set(host.Id, fx.Capability("ubuntu:24.04"));

        var request = new ReplaceImagesRequest(new[]
        {
            new ImageUpsert("ubuntu:24.04", 7, new[] { "none" }, 3600, null, null, null, true),
        });

        var first = await fx.Service.ReplaceImagesAsync(owner, host.Id, request);
        var second = await fx.Service.ReplaceImagesAsync(owner, host.Id, request);

        Assert.Single(first.Data);
        Assert.Single(second.Data);
        Assert.Equal("ubuntu:24.04", second.Data[0].ImageRef);
    }
}
