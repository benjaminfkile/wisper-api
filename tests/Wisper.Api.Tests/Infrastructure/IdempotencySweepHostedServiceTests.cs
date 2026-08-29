using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Persistence;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Infrastructure;

/// <summary>
/// Unit tests for the scheduled idempotency-key TTL sweep (task #183, docs/DATA_MODEL.md §10, §14): the
/// pass deletes every expired <c>idempotency_keys</c> row and logs the count. The loop is off in the
/// in-memory persistence mode (nothing to sweep without a database) and off when disabled by config. The
/// Postgres advisory lock that makes it multi-instance safe is verified against a real Postgres
/// separately; these tests exercise the sweep pass through <see cref="IdempotencySweepHostedService.SweepOnceAsync"/>.
/// </summary>
public class IdempotencySweepHostedServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    private static (IdempotencySweepHostedService Svc, InMemoryIdempotencyKeyRepository Repo,
        IdempotencyService Idem, FakeTimeProvider Clock) NewService(
            IdempotencySweepOptions? options = null, Db? db = null, TimeSpan? ttl = null)
    {
        var clock = new FakeTimeProvider(T0);
        var repo = new InMemoryIdempotencyKeyRepository();
        var idem = new IdempotencyService(repo, clock, ttl ?? TimeSpan.FromHours(1));
        var svc = new IdempotencySweepHostedService(
            idem,
            Options.Create(options ?? new IdempotencySweepOptions()),
            db ?? Db.Unconfigured,
            clock,
            NullLogger<IdempotencySweepHostedService>.Instance);
        return (svc, repo, idem, clock);
    }

    [Fact]
    public async Task SweepOnceAsync_removes_expired_records_and_returns_the_count()
    {
        var (svc, repo, idem, clock) = NewService(ttl: TimeSpan.FromHours(1));

        await idem.BeginAsync(Guid.NewGuid(), "k-expired-1", "hashA");
        await idem.BeginAsync(Guid.NewGuid(), "k-expired-2", "hashB");
        clock.Advance(TimeSpan.FromHours(2)); // both keys expire
        await idem.BeginAsync(Guid.NewGuid(), "k-fresh", "hashC"); // written after clock advance

        var removed = await svc.SweepOnceAsync();

        Assert.Equal(2, removed);
        Assert.Null(await repo.GetAsync("k-expired-1"));
        Assert.Null(await repo.GetAsync("k-expired-2"));
        Assert.NotNull(await repo.GetAsync("k-fresh"));
    }

    [Fact]
    public async Task SweepOnceAsync_is_a_zero_pass_when_nothing_has_expired()
    {
        var (svc, _, idem, _) = NewService(ttl: TimeSpan.FromHours(1));
        await idem.BeginAsync(Guid.NewGuid(), "k", "hash");

        var removed = await svc.SweepOnceAsync();

        Assert.Equal(0, removed);
    }

    [Fact]
    public async Task RunOnceAsync_is_a_skip_when_no_database_is_configured()
    {
        // The advisory lock is a Postgres construct; with no DB there is nothing to coordinate and the
        // hosted service ExecuteAsync short-circuits at startup. RunOnceAsync mirrors that: no DB means
        // "another instance owns this pass" and the call returns null without touching the repository.
        var (svc, repo, idem, clock) = NewService(ttl: TimeSpan.FromHours(1));
        await idem.BeginAsync(Guid.NewGuid(), "k", "hash");
        clock.Advance(TimeSpan.FromHours(2));

        var removed = await svc.RunOnceAsync();

        Assert.Null(removed);
        Assert.NotNull(await repo.GetAsync("k")); // still there; the sweep did not run
    }

    [Fact]
    public async Task ExecuteAsync_returns_immediately_when_disabled_by_config()
    {
        var (svc, _, _, _) = NewService(options: new IdempotencySweepOptions { Enabled = false });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        await svc.ExecuteTask!;
        await svc.StopAsync(CancellationToken.None);

        Assert.True(svc.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExecuteAsync_returns_immediately_in_the_in_memory_persistence_mode()
    {
        var (svc, _, _, _) = NewService(db: Db.Unconfigured);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await svc.StartAsync(cts.Token);
        await svc.ExecuteTask!;
        await svc.StopAsync(CancellationToken.None);

        Assert.True(svc.ExecuteTask.IsCompletedSuccessfully);
    }
}
