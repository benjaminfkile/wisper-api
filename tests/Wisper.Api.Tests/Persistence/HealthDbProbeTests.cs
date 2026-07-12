using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// Over the real app host with no database configured (as on Grunt), the health endpoints must
/// still report <c>ok</c> and include the DB probe entry marked as skipped — proving the probe is
/// wired and degrades gracefully rather than crashing the boot (docs/DATA_MODEL.md §1).
/// </summary>
public class HealthDbProbeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthDbProbeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/healthz")]
    public async Task Health_includes_db_probe_and_degrades_gracefully(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<HealthReport>();
        Assert.NotNull(report);
        Assert.Equal("ok", report!.Status);
        Assert.True(report.Checks.TryGetValue("database", out var db));
        Assert.Equal("healthy", db!.Status);
        Assert.Contains("skipped", db.Description ?? string.Empty);
    }

    private sealed record HealthReport(string Status, Dictionary<string, HealthEntry> Checks);

    private sealed record HealthEntry(string Status, string? Description);
}
