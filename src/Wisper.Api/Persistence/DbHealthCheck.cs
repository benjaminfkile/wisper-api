using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Wisper.Api.Persistence;

/// <summary>
/// Liveness probe for PostgreSQL (docs/API.md §4). Behaviour by configuration:
/// <list type="bullet">
///   <item>no database configured → <see cref="HealthStatus.Healthy"/> with a "skipped" note, so
///   the app still reports healthy and boots for the tunnel (graceful degrade, never a crash);</item>
///   <item>configured and reachable (a <c>SELECT 1</c> round-trips) → <see cref="HealthStatus.Healthy"/>;</item>
///   <item>configured but unreachable → <see cref="HealthStatus.Unhealthy"/> (the DB is expected but down).</item>
/// </list>
/// </summary>
public sealed class DbHealthCheck : IHealthCheck
{
    /// <summary>The health-check registration name.</summary>
    public const string Name = "database";

    private readonly Db _db;

    public DbHealthCheck(Db db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_db.TryGetDataSource(out var dataSource))
        {
            return HealthCheckResult.Healthy("skipped: no database configured");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var command = new CommandDefinition("SELECT 1", cancellationToken: cancellationToken);
            _ = await connection.ExecuteScalarAsync<int>(command);

            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var data = new Dictionary<string, object> { ["latency_ms"] = Math.Round(elapsedMs, 1) };
            return HealthCheckResult.Healthy("database reachable", data);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}
