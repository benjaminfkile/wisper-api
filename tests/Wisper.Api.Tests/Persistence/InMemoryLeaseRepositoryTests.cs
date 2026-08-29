using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Leases;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="ILeaseRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers create/get with immutable snapshots, consumer + host-active listings, the full
/// mutable update, and the state-machine <see cref="ILeaseRepository.TransitionStateAsync"/> across the
/// lifecycle (docs/DATA_MODEL.md §5, docs/TUNNEL.md §8).
/// </summary>
public class InMemoryLeaseRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static Lease NewLease(Guid consumer, Guid host) => new()
    {
        ConsumerUserId = consumer,
        HostId = host,
        HostImageId = Guid.NewGuid(),
        ImageRef = "ubuntu:24.04",
        Network = NetworkMode.Egress,
        Cpus = 2,
        MemoryMb = 4096,
        TtlSeconds = 3600,
        PriceCentsPerMin = 5,
        CreatedAt = T0,
    };

    [Fact]
    public async Task Create_assigns_id_and_round_trips_the_snapshots()
    {
        var repo = new InMemoryLeaseRepository();
        var created = await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(LeaseStatus.Pending, created.Status);
        Assert.Equal("usd", created.Currency);
        Assert.Equal(NetworkMode.Egress, created.Network);
        Assert.Equal(2, created.Cpus);
        Assert.Equal(0, created.BillableSeconds);
        Assert.Null(created.EndReason);
        Assert.Null(created.StartedAt);
        Assert.Equal(created, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task ListByConsumer_scopes_and_orders_newest_first()
    {
        var repo = new InMemoryLeaseRepository();
        var consumer = Guid.NewGuid();
        var older = await repo.CreateAsync(NewLease(consumer, Guid.NewGuid()) with { CreatedAt = T0 });
        var newer = await repo.CreateAsync(NewLease(consumer, Guid.NewGuid()) with { CreatedAt = T0.AddMinutes(5) });
        await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()));

        var mine = await repo.ListByConsumerAsync(consumer);

        Assert.Equal(new[] { newer.Id, older.Id }, mine.Select(l => l.Id).ToArray());
    }

    [Fact]
    public async Task ListActiveByHost_returns_only_active_and_suspended()
    {
        var repo = new InMemoryLeaseRepository();
        var host = Guid.NewGuid();
        var active = await repo.CreateAsync(NewLease(Guid.NewGuid(), host) with { Status = LeaseStatus.Active });
        var suspended = await repo.CreateAsync(NewLease(Guid.NewGuid(), host) with { Status = LeaseStatus.Suspended });
        await repo.CreateAsync(NewLease(Guid.NewGuid(), host) with { Status = LeaseStatus.Pending });
        await repo.CreateAsync(NewLease(Guid.NewGuid(), host) with { Status = LeaseStatus.Ended, EndedAt = T0 });
        await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()) with { Status = LeaseStatus.Active });

        var live = await repo.ListActiveByHostAsync(host);

        Assert.Equal(new[] { active.Id, suspended.Id }.OrderBy(g => g),
            live.Select(l => l.Id).OrderBy(g => g));
    }

    [Fact]
    public async Task Full_lifecycle_transitions_advance_status_and_timeline()
    {
        var repo = new InMemoryLeaseRepository();
        var created = await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()));

        var provisioning = await repo.TransitionStateAsync(created.Id, LeaseStatus.Provisioning);
        Assert.NotNull(provisioning);
        Assert.Equal(LeaseStatus.Provisioning, provisioning!.Status);

        // active: meter starts
        var active = await repo.TransitionStateAsync(
            created.Id, LeaseStatus.Active, startedAt: T0.AddSeconds(10), lastMeteredAt: T0.AddSeconds(10));
        Assert.Equal(LeaseStatus.Active, active!.Status);
        Assert.Equal(T0.AddSeconds(10), active.StartedAt);

        // a tick advances the watermark + billable seconds
        var ticked = await repo.TransitionStateAsync(
            created.Id, LeaseStatus.Active, lastMeteredAt: T0.AddSeconds(70), billableSeconds: 60);
        Assert.Equal(60, ticked!.BillableSeconds);
        Assert.Equal(T0.AddSeconds(70), ticked.LastMeteredAt);
        Assert.Equal(T0.AddSeconds(10), ticked.StartedAt); // unchanged (null arg keeps column)

        // suspend then resume -- no timeline change while suspended
        var suspended = await repo.TransitionStateAsync(created.Id, LeaseStatus.Suspended);
        Assert.Equal(LeaseStatus.Suspended, suspended!.Status);
        Assert.Equal(60, suspended.BillableSeconds);

        var resumed = await repo.TransitionStateAsync(created.Id, LeaseStatus.Active);
        Assert.Equal(LeaseStatus.Active, resumed!.Status);

        // end
        var ended = await repo.TransitionStateAsync(
            created.Id, LeaseStatus.Ended,
            endReason: LeaseEndReason.Released, endedAt: T0.AddSeconds(130), billableSeconds: 120);
        Assert.Equal(LeaseStatus.Ended, ended!.Status);
        Assert.Equal(LeaseEndReason.Released, ended.EndReason);
        Assert.Equal(T0.AddSeconds(130), ended.EndedAt);
        Assert.Equal(120, ended.BillableSeconds);
    }

    [Fact]
    public async Task Provisioning_can_fail()
    {
        var repo = new InMemoryLeaseRepository();
        var created = await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()));
        await repo.TransitionStateAsync(created.Id, LeaseStatus.Provisioning);

        var failed = await repo.TransitionStateAsync(created.Id, LeaseStatus.Failed);

        Assert.Equal(LeaseStatus.Failed, failed!.Status);
    }

    [Fact]
    public async Task Transition_of_a_missing_lease_returns_null()
    {
        var repo = new InMemoryLeaseRepository();
        Assert.Null(await repo.TransitionStateAsync(Guid.NewGuid(), LeaseStatus.Active));
    }

    [Fact]
    public async Task Update_writes_mutable_columns_and_leaves_snapshots()
    {
        var repo = new InMemoryLeaseRepository();
        var created = await repo.CreateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()));
        var hold = Guid.NewGuid();

        var updated = await repo.UpdateAsync(created with
        {
            Status = LeaseStatus.Active,
            WispContractId = "wisp-abc",
            HoldTxnId = hold,
            StartedAt = T0.AddSeconds(5),
            LastMeteredAt = T0.AddSeconds(5),
            BillableSeconds = 0,
            // attempts to mutate a snapshot must be ignored
            ImageRef = "SHOULD-NOT-CHANGE",
            PriceCentsPerMin = 999,
        });

        Assert.Equal(LeaseStatus.Active, updated.Status);
        Assert.Equal("wisp-abc", updated.WispContractId);
        Assert.Equal(hold, updated.HoldTxnId);
        Assert.Equal(T0.AddSeconds(5), updated.StartedAt);
        Assert.Equal("ubuntu:24.04", updated.ImageRef);
        Assert.Equal(5, updated.PriceCentsPerMin);
        Assert.Equal(updated, await repo.GetByIdAsync(created.Id));
    }

    [Fact]
    public async Task Update_of_a_missing_lease_throws()
    {
        var repo = new InMemoryLeaseRepository();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpdateAsync(NewLease(Guid.NewGuid(), Guid.NewGuid()) with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Missing_lookups_return_null_or_empty()
    {
        var repo = new InMemoryLeaseRepository();
        Assert.Null(await repo.GetByIdAsync(Guid.NewGuid()));
        Assert.Empty(await repo.ListByConsumerAsync(Guid.NewGuid()));
        Assert.Empty(await repo.ListActiveByHostAsync(Guid.NewGuid()));
    }
}
