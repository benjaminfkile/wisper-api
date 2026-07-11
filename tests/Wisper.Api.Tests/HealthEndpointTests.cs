using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wisper.Api.Tests;

/// <summary>
/// Integration tests over the real app host (WebApplicationFactory&lt;Program&gt;):
/// prove liveness, the request-id header, and the uniform error envelope.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/healthz")]
    public async Task Health_returns_ok_and_request_id(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Request-Id"));

        var body = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
    }

    [Fact]
    public async Task Unknown_route_returns_uniform_error_envelope()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ErrorEnvelopeDto>();
        Assert.NotNull(envelope);
        Assert.Equal("not_found", envelope!.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.Message));
        Assert.False(string.IsNullOrWhiteSpace(envelope.Error.RequestId));
    }

    private sealed record HealthDto(string Status);

    private sealed record ErrorEnvelopeDto(ErrorBodyDto Error);

    private sealed record ErrorBodyDto(string Code, string Message, string RequestId);
}
