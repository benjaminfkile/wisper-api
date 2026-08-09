using Wisper.Api.Domain;
using Xunit;

namespace Wisper.Api.Tests.Domain;

/// <summary>
/// Unit tests for <see cref="ResolvedResources"/> (task #578): the effective-resource resolution precedence
/// (offer &gt; host per-lease cap &gt; unknown) shared by the catalog and lease surfaces, plus the whole-vCPU
/// rounding applied when stamping the integer <c>leases.cpus</c> column.
/// </summary>
public class ResolvedResourcesTests
{
    [Fact]
    public void Offer_values_beat_the_host_cap()
    {
        var resolved = ResolvedResources.Resolve(offerCpus: 2, offerMemoryMb: 4096, hostCapCpus: 4, hostCapMemoryMb: 8192);

        Assert.Equal(2m, resolved.Cpus);
        Assert.Equal(4096, resolved.MemoryMb);
        Assert.Equal("offer", resolved.Source);
        Assert.Equal(2, resolved.CpusForStamp);
    }

    [Fact]
    public void Host_cap_fills_a_null_profile()
    {
        var resolved = ResolvedResources.Resolve(offerCpus: null, offerMemoryMb: null, hostCapCpus: 4, hostCapMemoryMb: 8192);

        Assert.Equal(4m, resolved.Cpus);
        Assert.Equal(8192, resolved.MemoryMb);
        Assert.Equal("host_cap", resolved.Source);
        Assert.Equal(4, resolved.CpusForStamp);
    }

    [Fact]
    public void No_offer_and_no_cap_is_unknown()
    {
        var resolved = ResolvedResources.Resolve(offerCpus: null, offerMemoryMb: null, hostCapCpus: 0, hostCapMemoryMb: 0);

        Assert.Null(resolved.Cpus);
        Assert.Null(resolved.MemoryMb);
        Assert.Equal("unknown", resolved.Source);
        Assert.Null(resolved.CpusForStamp);
    }

    [Fact]
    public void A_present_offer_value_marks_the_source_offer_even_when_the_other_dimension_falls_back()
    {
        // Precedence is by tier: any offer-provided value marks "offer"; the still-null memory falls back
        // to the host cap for its effective value.
        var resolved = ResolvedResources.Resolve(offerCpus: 2, offerMemoryMb: null, hostCapCpus: 4, hostCapMemoryMb: 8192);

        Assert.Equal(2m, resolved.Cpus);
        Assert.Equal(8192, resolved.MemoryMb);
        Assert.Equal("offer", resolved.Source);
    }

    [Fact]
    public void A_fractional_host_cap_rounds_to_the_nearest_whole_core_for_stamping()
    {
        // The effective value preserves the fractional cap for display; only the integer stamp rounds it.
        var resolved = ResolvedResources.Resolve(offerCpus: null, offerMemoryMb: null, hostCapCpus: 1.5, hostCapMemoryMb: 2048);

        Assert.Equal(1.5m, resolved.Cpus);
        Assert.Equal(2, resolved.CpusForStamp);
    }
}
