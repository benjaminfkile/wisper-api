using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wisper.Api.Persistence.Policy;
using Wisper.Api.Policy;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Task #184: on a DB-less boot the app must seed a default <see cref="Wisper.Api.Domain.PlatformPolicy"/>
/// row so billing paths never throw for lack of a policy. Postgres gets the same seed via migration 0017;
/// this test proves the parallel in-memory startup hook (<c>SeedInMemoryDefaultsAsync</c>) runs and leaves
/// exactly one conservative default active on the DB-less boot path.
/// </summary>
public class InMemoryDefaultPolicySeedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public InMemoryDefaultPolicySeedTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Dbless_boot_seeds_a_conservative_default_platform_policy()
    {
        // Reach into the built host's services after Program.cs has run. SeedInMemoryDefaultsAsync
        // fires during app startup, so the store already carries the seed row by the time factory.Services
        // is available.
        var svc = _factory.Services.GetRequiredService<PlatformPolicyService>();

        var active = await svc.GetActiveAsync();

        Assert.NotNull(active);
        Assert.Equal(0, active!.FeeBps);                    // no platform cut on the seed
        Assert.Equal(0, active.MinTopupCents);
        Assert.Null(active.MaxConcurrentLeasesPerUser);     // no restriction
        Assert.Null(active.MaxTtlSecondsCap);
        Assert.Null(active.MinIsolation);
        Assert.Null(active.FirstTopupMaxCents);
        Assert.Null(active.NewAccountWindowHours);
        Assert.Null(active.NewAccountMaxTopupCentsPerDay);
        Assert.Null(active.MaxSpendCentsPerDay);
        Assert.Null(active.CreatedBy);                      // system seed, not an admin-authored version
    }

    [Fact]
    public async Task Only_one_seed_row_is_present_after_boot()
    {
        // The seed hook guards on "table empty" so it inserts at most once per store lifetime: a
        // freshly booted DB-less deployment carries exactly the one system seed until an admin
        // publishes their own version.
        var policies = _factory.Services.GetRequiredService<IPlatformPolicyRepository>();

        var versions = await policies.ListAsync();

        Assert.Single(versions);
        Assert.Null(versions[0].CreatedBy);                 // marks it as the system seed
    }
}
