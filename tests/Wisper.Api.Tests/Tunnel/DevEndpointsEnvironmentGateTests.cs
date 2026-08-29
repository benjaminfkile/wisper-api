using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wisper.Api.Tests.TestSupport;
using Wisper.Api.Tunnel;
using Xunit;

namespace Wisper.Api.Tests.Tunnel;

/// <summary>
/// The DEV-ONLY <c>/dev/leases*</c> and <c>/dev/leases/{id}/shell</c> harness is money-free and
/// unauthenticated -- the caller names the target hostId directly. It must be structurally
/// unreachable in any deployed environment, so mapping is gated on
/// <see cref="IHostEnvironment.IsDevelopment"/> in addition to the <c>Tunnel:EnableDevEndpoints</c>
/// flag. A deployed container runs as Production (Dockerfile sets no ASPNETCORE_ENVIRONMENT), so
/// even a misconfigured secret that sets the flag on cannot expose the harness.
/// </summary>
public class DevEndpointsEnvironmentGateTests
{
    private static WebApplicationFactory<Program> CreateFactory(string environment, bool enableDevEndpoints) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Tunnel:EnableDevEndpoints", enableDevEndpoints ? "true" : "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITunnelRelay>();
                services.AddSingleton<ITunnelRelay>(new FakeTunnelRelay());
            });
        });

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Dev_lease_endpoints_are_unmapped_in_non_development_even_when_flag_is_on(string environment)
    {
        using var factory = CreateFactory(environment, enableDevEndpoints: true);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync(
            "/dev/leases",
            new { hostId = "any-host", image = "alpine", network = "none", ttl_seconds = 3600 });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        var exec = await client.PostAsJsonAsync(
            "/dev/leases/lease-1/exec", new { hostId = "any-host", command = "true" });
        Assert.Equal(HttpStatusCode.NotFound, exec.StatusCode);

        var release = await client.DeleteAsync("/dev/leases/lease-1?hostId=any-host");
        Assert.Equal(HttpStatusCode.NotFound, release.StatusCode);

        var shell = await client.GetAsync("/dev/leases/lease-1/shell?hostId=any-host");
        Assert.Equal(HttpStatusCode.NotFound, shell.StatusCode);
    }

    [Fact]
    public async Task Dev_lease_endpoints_are_mapped_in_development_when_flag_is_on()
    {
        using var factory = CreateFactory("Development", enableDevEndpoints: true);
        var client = factory.CreateClient();

        // The route is mapped: with a missing hostId body the handler runs and rejects with a
        // ValidationError (400), which is what proves the route matched (vs. the 404 fallback).
        var create = await client.PostAsJsonAsync("/dev/leases", new { });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    [Fact]
    public async Task Dev_lease_endpoints_are_unmapped_in_development_when_flag_is_off()
    {
        using var factory = CreateFactory("Development", enableDevEndpoints: false);
        var client = factory.CreateClient();

        var create = await client.PostAsJsonAsync(
            "/dev/leases",
            new { hostId = "any-host", image = "alpine", network = "none", ttl_seconds = 3600 });
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }
}
