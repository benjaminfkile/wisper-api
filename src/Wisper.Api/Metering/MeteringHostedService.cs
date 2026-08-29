using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Persistence;

namespace Wisper.Api.Metering;

/// <summary>
/// The background flush loop that drives <see cref="MeteringService"/> on a fixed tick (docs/DATA_MODEL.md
/// §14, default 60s). Each tick reloads the active leases and flushes accrued lease-minutes; a tick that
/// throws is logged and the loop continues, so one bad flush never stops metering. The loop is a no-op on
/// a DB-less boot (the tunnel needs no database) or when disabled by config -- the meter's working set
/// lives in Postgres.
/// </summary>
public sealed class MeteringHostedService : BackgroundService
{
    private readonly MeteringService _meter;
    private readonly MeteringOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<MeteringHostedService> _logger;

    public MeteringHostedService(
        MeteringService meter,
        IOptions<MeteringOptions> options,
        Db db,
        TimeProvider time,
        ILogger<MeteringHostedService> logger)
    {
        _meter = meter;
        _options = options.Value;
        _db = db;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("metering disabled by config -- background flush loop not started");
            return;
        }

        if (!_db.IsConfigured)
        {
            _logger.LogInformation("no database configured -- metering flush loop not started (tunnel-only boot)");
            return;
        }

        _logger.LogInformation(
            "metering flush loop starting (tick every {TickSeconds}s)", _options.TickSeconds);

        using var timer = new PeriodicTimer(_options.TickInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var flushed = await _meter.RunTickAsync(stoppingToken);
                if (flushed > 0)
                {
                    _logger.LogInformation("metering tick flushed {Flushed} lease(s)", flushed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // A single failed tick (e.g. a transient DB error) must not stop metering -- the watermark
                // is durable, so the next tick simply resumes and re-bills the un-flushed interval (§14).
                _logger.LogError(ex, "metering tick failed; will retry on the next tick");
            }
        }
    }
}
