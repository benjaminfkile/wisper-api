using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Policy;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="IPlatformPolicyRepository"/> against the in-memory double (Grunt has no
/// Postgres). Covers the versioned, append-only semantics: the active row is the newest
/// <c>effective_from</c> at or before a given instant, and a not-yet-effective version is excluded
/// (docs/DATA_MODEL.md §11).
/// </summary>
public class InMemoryPlatformPolicyRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static PlatformPolicy NewPolicy(int feeBps, DateTimeOffset effectiveFrom) => new()
    {
        FeeBps = feeBps,
        MinTopupCents = 500,
        EffectiveFrom = effectiveFrom,
    };

    [Fact]
    public async Task Active_is_the_newest_effective_version_at_or_before_now()
    {
        var repo = new InMemoryPlatformPolicyRepository();
        await repo.AppendAsync(NewPolicy(1500, T0.AddDays(-10)));
        await repo.AppendAsync(NewPolicy(1200, T0.AddDays(-1)));

        var active = await repo.GetActiveAsync(T0);

        Assert.Equal(1200, active!.FeeBps);
    }

    [Fact]
    public async Task Future_versions_are_not_active_yet()
    {
        var repo = new InMemoryPlatformPolicyRepository();
        await repo.AppendAsync(NewPolicy(1500, T0.AddDays(-1)));
        await repo.AppendAsync(NewPolicy(1000, T0.AddDays(5)));   // scheduled, not yet in force

        var active = await repo.GetActiveAsync(T0);

        Assert.Equal(1500, active!.FeeBps);
    }

    [Fact]
    public async Task No_effective_version_yields_null()
    {
        var repo = new InMemoryPlatformPolicyRepository();
        await repo.AppendAsync(NewPolicy(1500, T0.AddDays(5)));

        Assert.Null(await repo.GetActiveAsync(T0));
    }

    [Fact]
    public async Task List_returns_every_version_newest_first()
    {
        var repo = new InMemoryPlatformPolicyRepository();
        await repo.AppendAsync(NewPolicy(1500, T0.AddDays(-10)));
        await repo.AppendAsync(NewPolicy(1200, T0.AddDays(-1)));
        await repo.AppendAsync(NewPolicy(1000, T0.AddDays(5)));

        var all = await repo.ListAsync();

        Assert.Equal(new[] { 1000, 1200, 1500 }, all.Select(p => p.FeeBps).ToArray());
    }
}
