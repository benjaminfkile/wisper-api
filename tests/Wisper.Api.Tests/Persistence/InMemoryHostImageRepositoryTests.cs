using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.HostImages;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IHostImageRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers per-host priced-allow-list CRUD, the enabled catalog filter, the network-mode
/// list round-trip, and the <c>(host_id, image_ref)</c> uniqueness the SQL schema enforces
/// (docs/DATA_MODEL.md §4).
/// </summary>
public class InMemoryHostImageRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static HostImage NewImage(Guid hostId, string imageRef = "ubuntu:24.04") => new()
    {
        HostId = hostId,
        ImageRef = imageRef,
        PriceCentsPerMin = 5,
        Networks = new[] { NetworkMode.None, NetworkMode.Egress },
        MaxTtlSeconds = 3600,
        MaxCpus = 2.5m,
        MaxMemoryMb = 4096,
        MaxPids = 512,
        Cpus = 2,
        MemoryMb = 4096,
        Gpus = 1,
        CreatedAt = T0,
        UpdatedAt = T0,
    };

    [Fact]
    public async Task Create_assigns_id_and_round_trips_including_networks()
    {
        var repo = new InMemoryHostImageRepository();
        var host = Guid.NewGuid();

        var created = await repo.CreateAsync(NewImage(host));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.True(created.Enabled);
        Assert.Equal(new[] { NetworkMode.None, NetworkMode.Egress }, created.Networks.ToArray());
        Assert.Equal(2.5m, created.MaxCpus);
        // The sized offer's fixed resource profile round-trips too (task #569).
        Assert.Equal(2, created.Cpus);
        Assert.Equal(4096, created.MemoryMb);
        Assert.Equal(1, created.Gpus);
        Assert.Equal(created, await repo.GetByIdAsync(created.Id));
        Assert.Equal(created, await repo.GetByHostAndRefAsync(host, "ubuntu:24.04"));
    }

    [Fact]
    public async Task ListByHost_scopes_orders_and_can_filter_to_enabled()
    {
        var repo = new InMemoryHostImageRepository();
        var host = Guid.NewGuid();
        await repo.CreateAsync(NewImage(host, "debian:12"));
        await repo.CreateAsync(NewImage(host, "alpine:3.20") with { Enabled = false });
        await repo.CreateAsync(NewImage(Guid.NewGuid(), "other:1"));

        var all = await repo.ListByHostAsync(host);
        Assert.Equal(new[] { "alpine:3.20", "debian:12" }, all.Select(i => i.ImageRef).ToArray());

        var enabled = await repo.ListByHostAsync(host, enabledOnly: true);
        Assert.Equal(new[] { "debian:12" }, enabled.Select(i => i.ImageRef).ToArray());
    }

    [Fact]
    public async Task Duplicate_host_and_image_ref_is_rejected()
    {
        var repo = new InMemoryHostImageRepository();
        var host = Guid.NewGuid();
        await repo.CreateAsync(NewImage(host, "ubuntu:24.04"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.CreateAsync(NewImage(host, "ubuntu:24.04")));

        // The same image_ref under a different host is fine.
        var image = await repo.CreateAsync(NewImage(Guid.NewGuid(), "ubuntu:24.04"));
        Assert.NotEqual(Guid.Empty, image.Id);
    }

    [Fact]
    public async Task Update_changes_pricing_and_flags()
    {
        var repo = new InMemoryHostImageRepository();
        var created = await repo.CreateAsync(NewImage(Guid.NewGuid()));

        var updated = await repo.UpdateAsync(created with
        {
            PriceCentsPerMin = 42,
            Enabled = false,
            Networks = new[] { NetworkMode.Open },
            UpdatedAt = T0.AddMinutes(1),
        });

        Assert.Equal(42, updated.PriceCentsPerMin);
        Assert.False(updated.Enabled);
        Assert.Equal(new[] { NetworkMode.Open }, updated.Networks.ToArray());
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Update_into_an_existing_host_and_ref_is_rejected()
    {
        var repo = new InMemoryHostImageRepository();
        var host = Guid.NewGuid();
        await repo.CreateAsync(NewImage(host, "a:1"));
        var second = await repo.CreateAsync(NewImage(host, "b:1"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(second with { ImageRef = "a:1" }));
    }

    [Fact]
    public async Task Update_of_a_missing_image_throws()
    {
        var repo = new InMemoryHostImageRepository();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(NewImage(Guid.NewGuid()) with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Delete_removes_and_reports()
    {
        var repo = new InMemoryHostImageRepository();
        var created = await repo.CreateAsync(NewImage(Guid.NewGuid()));

        Assert.True(await repo.DeleteAsync(created.Id));
        Assert.False(await repo.DeleteAsync(created.Id));
        Assert.Null(await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Missing_lookups_return_null()
    {
        var repo = new InMemoryHostImageRepository();

        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid()));
        Assert.Null(await repo.GetByHostAndRefAsync(Guid.NewGuid(), "nope"));
        Assert.Empty(await repo.ListByHostAsync(Guid.NewGuid()));
    }
}
