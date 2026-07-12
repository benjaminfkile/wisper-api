using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Idempotency;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IIdempotencyKeyRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers the atomic in-progress lock (<see cref="IIdempotencyKeyRepository.TryBeginAsync"/> is
/// first-writer-wins), completing to <c>done</c> with a stored response, and the TTL sweep
/// (docs/DATA_MODEL.md §10).
/// </summary>
public class InMemoryIdempotencyKeyRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static IdempotencyKey NewKey(string key, Guid user, string hash = "h") => new()
    {
        Key = key,
        UserId = user,
        RequestHash = hash,
        ExpiresAt = T0.AddHours(24),
    };

    [Fact]
    public async Task TryBegin_inserts_and_wins_then_a_second_sees_the_existing_row()
    {
        var repo = new InMemoryIdempotencyKeyRepository();
        var user = Guid.NewGuid();

        Assert.Null(await repo.TryBeginAsync(NewKey("k1", user)));   // won the lock

        var second = await repo.TryBeginAsync(NewKey("k1", user));   // sees the in-progress row
        Assert.NotNull(second);
        Assert.Equal(IdempotencyStatus.InProgress, second!.Status);
    }

    [Fact]
    public async Task Complete_stores_the_response_and_flips_to_done()
    {
        var repo = new InMemoryIdempotencyKeyRepository();
        await repo.TryBeginAsync(NewKey("k1", Guid.NewGuid()));

        var done = await repo.CompleteAsync("k1", 201, """{"ok":true}""");

        Assert.Equal(IdempotencyStatus.Done, done!.Status);
        Assert.Equal(201, done.ResponseStatus);
        Assert.Equal("""{"ok":true}""", done.ResponseBody);
        Assert.Equal(done, await repo.GetAsync("k1"));
    }

    [Fact]
    public async Task DeleteExpired_removes_only_stale_rows()
    {
        var repo = new InMemoryIdempotencyKeyRepository();
        var user = Guid.NewGuid();
        await repo.TryBeginAsync(NewKey("fresh", user) with { ExpiresAt = T0.AddHours(1) });
        await repo.TryBeginAsync(NewKey("stale", user) with { ExpiresAt = T0.AddHours(-1) });

        var removed = await repo.DeleteExpiredAsync(T0);

        Assert.Equal(1, removed);
        Assert.Null(await repo.GetAsync("stale"));
        Assert.NotNull(await repo.GetAsync("fresh"));
    }

    [Fact]
    public async Task Complete_and_delete_on_unknown_key_are_safe()
    {
        var repo = new InMemoryIdempotencyKeyRepository();
        Assert.Null(await repo.CompleteAsync("nope", 200, "{}"));
        Assert.False(await repo.DeleteAsync("nope"));
    }
}
