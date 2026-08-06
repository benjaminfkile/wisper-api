using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Hosts;
using Xunit;
using Host = Wisper.Api.Domain.Host;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IHostRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers owner-scoped CRUD, the online catalog subset, agent-token-hash lookup, and the
/// narrow online-state transition the tunnel lifecycle uses (docs/DATA_MODEL.md §4, docs/TUNNEL.md §5).
/// </summary>
public class InMemoryHostRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static Host NewHost(Guid owner, string tokenHash = "hash-1") => new()
    {
        OwnerUserId = owner,
        Name = "box",
        AgentTokenHash = tokenHash,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    [Fact]
    public async Task Create_assigns_id_and_defaults_to_offline()
    {
        var repo = new InMemoryHostRepository();

        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(HostStatus.Offline, created.Status);
        Assert.Equal(created, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task ListByOwner_scopes_and_orders_newest_first()
    {
        var repo = new InMemoryHostRepository();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var older = await repo.CreateAsync(NewHost(owner, "h-older") with { CreatedAt = T0 });
        var newer = await repo.CreateAsync(NewHost(owner, "h-newer") with { CreatedAt = T0.AddMinutes(10) });
        await repo.CreateAsync(NewHost(other, "h-other"));

        var mine = await repo.ListByOwnerAsync(owner);

        Assert.Equal(new[] { newer.Id, older.Id }, mine.Select(h => h.Id).ToArray());
    }

    [Fact]
    public async Task ListOnline_returns_only_online_hosts()
    {
        var repo = new InMemoryHostRepository();
        var owner = Guid.NewGuid();
        var online = await repo.CreateAsync(NewHost(owner, "on") with { Status = HostStatus.Online });
        await repo.CreateAsync(NewHost(owner, "off") with { Status = HostStatus.Offline });
        await repo.CreateAsync(NewHost(owner, "susp") with { Status = HostStatus.Suspended });

        var catalog = await repo.ListOnlineAsync();

        Assert.Equal(new[] { online.Id }, catalog.Select(h => h.Id).ToArray());
    }

    [Fact]
    public async Task GetByAgentTokenHash_finds_the_matching_host()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid(), "secret-hash"));

        Assert.Equal(created.Id, (await repo.GetByAgentTokenHashAsync("secret-hash"))!.Id);
        Assert.Null(await repo.GetByAgentTokenHashAsync("no-such-hash"));
    }

    [Fact]
    public async Task Update_replaces_mutable_columns()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));

        var updated = await repo.UpdateAsync(created with
        {
            Label = "us-east",
            WispVersion = "1.2.3",
            MaxLeases = 8,
            MaxStreams = 32,
            UpdatedAt = T0.AddMinutes(1),
        });

        Assert.Equal("us-east", updated.Label);
        Assert.Equal(8, updated.MaxLeases);
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Update_of_a_missing_host_throws()
    {
        var repo = new InMemoryHostRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(NewHost(Guid.NewGuid()) with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task SetOnlineState_stamps_last_seen_when_provided_and_keeps_it_otherwise()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));
        var seen = T0.AddSeconds(30);

        var online = await repo.SetOnlineStateAsync(created.Id, HostStatus.Online, seen, T0.AddSeconds(30));
        Assert.Equal(HostStatus.Online, online!.Status);
        Assert.Equal(seen, online.LastSeenAt);

        // A null last-seen (e.g. a plain status transition) must preserve the prior heartbeat.
        var offline = await repo.SetOnlineStateAsync(created.Id, HostStatus.Offline, null, T0.AddSeconds(60));
        Assert.Equal(HostStatus.Offline, offline!.Status);
        Assert.Equal(seen, offline.LastSeenAt);
    }

    [Fact]
    public async Task SetOnlineState_of_a_missing_host_returns_null()
    {
        var repo = new InMemoryHostRepository();

        Assert.Null(await repo.SetOnlineStateAsync(Guid.NewGuid(), HostStatus.Online, T0, T0));
    }

    [Fact]
    public async Task Create_defaults_isolation_to_shared()
    {
        var repo = new InMemoryHostRepository();

        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));

        Assert.Equal(new[] { "shared" }, created.IsolationLevels);
        Assert.Equal("shared", created.DefaultIsolation);
    }

    [Fact]
    public async Task SetAdvertisedIsolation_stores_levels_and_default_without_touching_presence()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()) with { Status = HostStatus.Online });

        var updated = await repo.SetAdvertisedIsolationAsync(
            created.Id, new[] { "shared", "vm" }, "vm", T0.AddSeconds(30));

        Assert.NotNull(updated);
        Assert.Equal(new[] { "shared", "vm" }, updated!.IsolationLevels);
        Assert.Equal("vm", updated.DefaultIsolation);
        Assert.Equal(HostStatus.Online, updated.Status); // presence untouched
        Assert.Equal(T0.AddSeconds(30), updated.UpdatedAt);
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task SetAdvertisedIsolation_of_a_missing_host_returns_null()
    {
        var repo = new InMemoryHostRepository();

        Assert.Null(await repo.SetAdvertisedIsolationAsync(
            Guid.NewGuid(), new[] { "shared" }, "shared", T0));
    }

    [Fact]
    public async Task Create_defaults_gpu_to_empty_and_zero()
    {
        var repo = new InMemoryHostRepository();

        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));

        Assert.Empty(created.GpuClasses);
        Assert.Equal(0, created.GpuCount);
    }

    [Fact]
    public async Task SetAdvertisedGpu_stores_classes_and_count_without_touching_presence()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()) with { Status = HostStatus.Online });

        var updated = await repo.SetAdvertisedGpuAsync(
            created.Id, new[] { "nvidia-a100", "nvidia-h100" }, 4, T0.AddSeconds(30));

        Assert.NotNull(updated);
        Assert.Equal(new[] { "nvidia-a100", "nvidia-h100" }, updated!.GpuClasses);
        Assert.Equal(4, updated.GpuCount);
        Assert.Equal(HostStatus.Online, updated.Status); // presence untouched
        Assert.Equal(new[] { "shared" }, updated.IsolationLevels); // isolation untouched
        Assert.Equal(T0.AddSeconds(30), updated.UpdatedAt);
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task SetAdvertisedGpu_of_a_missing_host_returns_null()
    {
        var repo = new InMemoryHostRepository();

        Assert.Null(await repo.SetAdvertisedGpuAsync(
            Guid.NewGuid(), new[] { "nvidia-a100" }, 1, T0));
    }

    [Fact]
    public async Task Delete_removes_and_reports()
    {
        var repo = new InMemoryHostRepository();
        var created = await repo.CreateAsync(NewHost(Guid.NewGuid()));

        Assert.True(await repo.DeleteAsync(created.Id));
        Assert.False(await repo.DeleteAsync(created.Id));
        Assert.Null(await repo.GetByIdAsync(created.Id));
    }
}
