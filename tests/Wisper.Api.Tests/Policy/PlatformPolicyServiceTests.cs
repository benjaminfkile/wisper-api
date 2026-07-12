using System.Threading.Tasks;
using Wisper.Api.Domain;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Wisper.Api.Tests.TestSupport;
using Xunit;

namespace Wisper.Api.Tests.Policy;

/// <summary>
/// Unit tests for <see cref="PlatformPolicyService"/> (docs/DATA_MODEL.md §11): the active version is the
/// newest one in force by the service clock, publishing appends a new version (defaulting
/// <c>effective_from</c> to now), and the fee-basis reader throws when nothing is configured.
/// </summary>
public class PlatformPolicyServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);

    private static (PlatformPolicyService Service, FakeTimeProvider Clock) NewService()
    {
        var clock = new FakeTimeProvider(T0);
        return (new PlatformPolicyService(new InMemoryPlatformPolicyRepository(), clock), clock);
    }

    [Fact]
    public async Task Publish_defaults_effective_from_to_now_and_becomes_active()
    {
        var (svc, _) = NewService();

        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1500 });

        var active = await svc.GetActiveAsync();
        Assert.Equal(1500, active!.FeeBps);
        Assert.Equal(T0, active.EffectiveFrom);
    }

    [Fact]
    public async Task Newest_effective_version_wins()
    {
        var (svc, clock) = NewService();
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1500 });

        clock.Advance(TimeSpan.FromDays(1));
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1200 });

        Assert.Equal(1200, (await svc.GetActiveAsync())!.FeeBps);
    }

    [Fact]
    public async Task A_future_effective_version_is_not_active_yet()
    {
        var (svc, _) = NewService();
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1500 });
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1000, EffectiveFrom = T0.AddDays(3) });

        Assert.Equal(1500, (await svc.GetActiveAsync())!.FeeBps);
    }

    [Fact]
    public async Task GetActiveOrThrow_throws_when_none_configured()
    {
        var (svc, _) = NewService();

        Assert.Null(await svc.GetActiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetActiveOrThrowAsync());
    }

    [Fact]
    public async Task ListVersions_returns_the_full_history()
    {
        var (svc, clock) = NewService();
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1500 });
        clock.Advance(TimeSpan.FromDays(1));
        await svc.PublishAsync(new PlatformPolicy { FeeBps = 1200 });

        var versions = await svc.ListVersionsAsync();

        Assert.Equal(2, versions.Count);
    }
}
