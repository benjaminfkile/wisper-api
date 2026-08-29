using System.Threading.Tasks;
using Wisper.Api.Infrastructure.Idempotency;
using Wisper.Api.Persistence.Idempotency;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Infrastructure;

/// <summary>
/// Unit tests for the <c>Idempotency-Key</c> helper (docs/API.md §9, docs/DATA_MODEL.md §10): the four
/// outcomes -- begin, replay the stored response on same key+body, 409 conflict on same key+different body,
/// 409 in-progress lock while the first request is still running -- plus the TTL sweep. Runs entirely on
/// the in-memory repository and a fake clock (Grunt has no Postgres).
/// </summary>
public class IdempotencyServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static (IdempotencyService Service, FakeTimeProvider Clock) NewService()
    {
        var clock = new FakeTimeProvider(T0);
        return (new IdempotencyService(new InMemoryIdempotencyKeyRepository(), clock), clock);
    }

    [Fact]
    public async Task First_request_begins()
    {
        var (svc, _) = NewService();

        var result = await svc.BeginAsync(Guid.NewGuid(), "k1", "hashA");

        Assert.Equal(IdempotencyOutcome.Began, result.Outcome);
    }

    [Fact]
    public async Task Same_key_same_body_replays_the_stored_response()
    {
        var (svc, _) = NewService();
        var user = Guid.NewGuid();

        await svc.BeginAsync(user, "k1", "hashA");
        await svc.CompleteAsync("k1", 201, """{"lease":"abc"}""");

        var replay = await svc.BeginAsync(user, "k1", "hashA");

        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal(201, replay.ResponseStatus);
        Assert.Equal("""{"lease":"abc"}""", replay.ResponseBody);
    }

    [Fact]
    public async Task Same_key_different_body_is_a_conflict()
    {
        var (svc, _) = NewService();
        var user = Guid.NewGuid();

        await svc.BeginAsync(user, "k1", "hashA");
        await svc.CompleteAsync("k1", 200, "{}");

        var conflict = await svc.BeginAsync(user, "k1", "hashB");

        Assert.Equal(IdempotencyOutcome.Conflict, conflict.Outcome);
        Assert.NotNull(conflict.Message);
    }

    [Fact]
    public async Task Replay_while_still_in_progress_is_the_in_progress_lock()
    {
        var (svc, _) = NewService();
        var user = Guid.NewGuid();

        await svc.BeginAsync(user, "k1", "hashA");   // first request still running (not completed)

        var again = await svc.BeginAsync(user, "k1", "hashA");

        Assert.Equal(IdempotencyOutcome.InProgress, again.Outcome);
    }

    [Fact]
    public async Task Different_user_reusing_a_key_is_a_conflict()
    {
        var (svc, _) = NewService();

        await svc.BeginAsync(Guid.NewGuid(), "k1", "hashA");

        var other = await svc.BeginAsync(Guid.NewGuid(), "k1", "hashA");

        Assert.Equal(IdempotencyOutcome.Conflict, other.Outcome);
    }

    [Fact]
    public async Task An_expired_record_is_swept_so_the_key_can_be_reused()
    {
        var (svc, clock) = NewService();
        var user = Guid.NewGuid();

        await svc.BeginAsync(user, "k1", "hashA");
        await svc.CompleteAsync("k1", 200, "{}");

        clock.Advance(IdempotencyService.DefaultTtl + TimeSpan.FromMinutes(1));

        // Past the TTL, the same key with a *different* body no longer conflicts -- the stale row is gone.
        var reused = await svc.BeginAsync(user, "k1", "hashB");

        Assert.Equal(IdempotencyOutcome.Began, reused.Outcome);
    }

    [Fact]
    public async Task SweepExpired_removes_stale_records()
    {
        var repo = new InMemoryIdempotencyKeyRepository();
        var clock = new FakeTimeProvider(T0);
        var svc = new IdempotencyService(repo, clock, TimeSpan.FromHours(1));

        await svc.BeginAsync(Guid.NewGuid(), "k1", "hashA");
        clock.Advance(TimeSpan.FromHours(2));

        var removed = await svc.SweepExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Null(await repo.GetAsync("k1"));
    }
}
