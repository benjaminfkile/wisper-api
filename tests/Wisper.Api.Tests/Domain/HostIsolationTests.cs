using Wisper.Api.Domain;
using Xunit;

namespace Wisper.Api.Tests.Domain;

/// <summary>
/// <see cref="HostIsolation.Normalize"/> -- the single place a host's advertised isolation capability is
/// canonicalized before it is persisted/surfaced (task #417): fallback to <c>["shared"]</c>/<c>"shared"</c>
/// for a host that reports nothing, blank/dupe trimming, and forcing the default to be one of the levels.
/// </summary>
public class HostIsolationTests
{
    [Fact]
    public void Nothing_reported_falls_back_to_shared()
    {
        var (levels, def) = HostIsolation.Normalize(null, null);

        Assert.Equal(new[] { "shared" }, levels);
        Assert.Equal("shared", def);
    }

    [Fact]
    public void Empty_list_falls_back_to_shared()
    {
        var (levels, def) = HostIsolation.Normalize(Array.Empty<string>(), "");

        Assert.Equal(new[] { "shared" }, levels);
        Assert.Equal("shared", def);
    }

    [Fact]
    public void Advertised_levels_are_trimmed_deduped_and_order_preserved()
    {
        var (levels, def) = HostIsolation.Normalize(
            new[] { "shared", " vm ", "shared", "", "  ", "gvisor" }, "vm");

        Assert.Equal(new[] { "shared", "vm", "gvisor" }, levels);
        Assert.Equal("vm", def);
    }

    [Fact]
    public void Default_absent_prefers_shared_when_offered()
    {
        var (levels, def) = HostIsolation.Normalize(new[] { "vm", "shared" }, null);

        Assert.Equal(new[] { "vm", "shared" }, levels);
        Assert.Equal("shared", def);
    }

    [Fact]
    public void Default_not_in_levels_or_shared_absent_falls_to_first_level()
    {
        // Default 'gvisor' is not advertised and 'shared' is not offered → the first advertised level wins.
        var (levels, def) = HostIsolation.Normalize(new[] { "vm", "kata" }, "gvisor");

        Assert.Equal(new[] { "vm", "kata" }, levels);
        Assert.Equal("vm", def);
    }
}
