using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Wisper.Api.Persistence;
using Xunit;

namespace Wisper.Api.Tests.Persistence;

/// <summary>
/// The DB probe must report healthy and name the <c>in-memory</c> dev mode when no database is configured
/// (rather than pretending a database is healthy), and must actually round-trip a query -- surfacing an
/// <b>unhealthy</b> result -- when a database is configured but unreachable.
/// </summary>
public class DbHealthCheckTests
{
    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration(DbHealthCheck.Name, new DbHealthCheck(Db.Unconfigured), null, null),
    };

    [Fact]
    public async Task Unconfigured_reports_healthy_in_memory()
    {
        var check = new DbHealthCheck(Db.Unconfigured);

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("in-memory", result.Description);
    }

    [Fact]
    public async Task Configured_but_unreachable_reports_unhealthy()
    {
        // Port 1 is never a Postgres listener, so the probe fails fast with a connection error.
        var dataSource = new NpgsqlDataSourceBuilder(
            "Host=127.0.0.1;Port=1;Database=wisper;Username=wisper;Timeout=2;Command Timeout=2").Build();
        await using var db = new Db(dataSource);
        var check = new DbHealthCheck(db);

        var result = await check.CheckHealthAsync(Context());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
