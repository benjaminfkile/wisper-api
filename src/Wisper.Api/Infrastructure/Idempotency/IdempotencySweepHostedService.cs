using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Persistence;

namespace Wisper.Api.Infrastructure.Idempotency;

/// <summary>
/// The scheduled TTL sweep for the <c>idempotency_keys</c> table (docs/DATA_MODEL.md §10, §14): on a fixed
/// tick (default 60 minutes), deletes every row whose <c>expires_at</c> is at or before now. Without the
/// sweep, expired records are only reaped lazily when a client presents an expired key, meaning long
/// low-traffic gaps let the table accumulate stale rows. Delete count is logged.
/// <para>
/// <b>Off in the in-memory persistence mode.</b> The loop gates on a configured database. It is also off
/// when disabled by config. A tick that throws is logged and the loop continues.
/// </para>
/// <para>
/// <b>Multi-instance safe.</b> Every tick attempts a PostgreSQL session-scoped advisory lock
/// (<see cref="PostgresAdvisoryLock.Keys.IdempotencySweep"/>). Exactly one instance runs the sweep; every
/// other instance's tick observes the lock held and skips. The sweep query itself is also idempotent
/// (a concurrent delete simply reports zero rows removed on the follower), so the advisory lock exists
/// only to avoid wasted work, not for correctness.
/// </para>
/// </summary>
public sealed class IdempotencySweepHostedService : BackgroundService
{
    private readonly IdempotencyService _idempotency;
    private readonly IdempotencySweepOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<IdempotencySweepHostedService> _logger;

    public IdempotencySweepHostedService(
        IdempotencyService idempotency,
        IOptions<IdempotencySweepOptions> options,
        Db db,
        TimeProvider time,
        ILogger<IdempotencySweepHostedService> logger)
    {
        _idempotency = idempotency;
        _options = options.Value;
        _db = db;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "idempotency sweep disabled by config; background loop not started");
            return;
        }

        if (!_db.IsConfigured)
        {
            _logger.LogInformation(
                "no database configured; idempotency sweep loop not started (in-memory persistence mode)");
            return;
        }

        _logger.LogInformation(
            "idempotency sweep loop starting (tick every {IntervalMinutes}m)", _options.IntervalMinutes);

        using var timer = new PeriodicTimer(_options.Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "idempotency sweep tick failed; will retry on the next tick");
            }
        }
    }

    /// <summary>
    /// One sweep tick as the loop drives it: takes the multi-instance advisory lock first, and if
    /// another instance already holds it, skips this pass (returns <c>null</c>). On success delegates to
    /// <see cref="SweepOnceAsync"/>. Public for the test surface.
    /// </summary>
    public async Task<int?> RunOnceAsync(CancellationToken ct = default)
    {
        await using var handle = await PostgresAdvisoryLock.TryAcquireAsync(
            _db, PostgresAdvisoryLock.Keys.IdempotencySweep, ct);
        if (handle is null)
        {
            _logger.LogDebug(
                "idempotency sweep tick skipped; another instance holds the advisory lock");
            return null;
        }

        return await SweepOnceAsync(ct);
    }

    /// <summary>
    /// The actual sweep pass: deletes every expired <c>idempotency_keys</c> row and logs the count.
    /// Bypasses the advisory lock, so a test can drive it deterministically without a live Postgres.
    /// Callers running in production go through <see cref="RunOnceAsync"/>.
    /// </summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct = default)
    {
        var removed = await _idempotency.SweepExpiredAsync(ct);
        if (removed > 0)
        {
            _logger.LogInformation(
                "idempotency sweep removed {Removed} expired record(s)", removed);
        }
        else
        {
            _logger.LogDebug("idempotency sweep found no expired records");
        }

        return removed;
    }
}
