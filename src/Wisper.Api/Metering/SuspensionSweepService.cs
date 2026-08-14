using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Persistence;
using Wisper.Api.Tunnel;

namespace Wisper.Api.Metering;

/// <summary>
/// The durable grace backstop (task #55, docs/TUNNEL.md §8). On a fixed tick, sweeps the DB for leases
/// whose <c>suspended_at</c> is older than the grace window (plus a safety margin) and whose host is not
/// currently under an active in-process grace timer on THIS instance, ending each one as
/// <c>host_disconnect</c> and releasing its wallet hold — the same terminal path the in-memory grace
/// timer takes. Turns the 90s in-memory grace window into a durable, restart-safe policy: a wisper-api
/// restart / crash / scale-in wipes <see cref="TunnelDisconnectCoordinator"/>'s timer state, but the
/// durable <see cref="Wisper.Api.Domain.Lease.SuspendedAt"/> stamp lets the next sweep tick reap the
/// stranded lease within one interval — no more wallet holds / concurrency slots stuck forever waiting
/// on a timer that is never coming.
/// <para>
/// Idempotent under concurrent instances: the underlying <c>suspended → ended</c> transition is
/// CAS-guarded on <c>status = 'suspended'</c>, so two sweep instances (or a sweep and a late-firing
/// in-process timer) produce exactly one end transition per lease. No-op on a DB-less boot; skipped
/// when metering is disabled by config; a tick that throws is logged and the loop continues so one bad
/// sweep never stops the backstop.
/// </para>
/// </summary>
public sealed class SuspensionSweepService : BackgroundService
{
    /// <summary>
    /// Additional slack on top of <see cref="TunnelOptions.GraceSeconds"/> before a suspended lease is
    /// swept, so a real reconnect that lands right at the grace edge is never raced by the sweep. The
    /// in-process timer fires at exactly grace; the sweep waits a little longer to defer to it.
    /// </summary>
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sweep cadence — long enough that the query load is negligible, short enough that a stranded
    /// suspended lease is reaped within roughly one interval past its grace edge.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly LeaseReconciliationService _reconciler;
    private readonly TunnelDisconnectCoordinator _coordinator;
    private readonly IOptionsMonitor<TunnelOptions> _tunnelOptions;
    private readonly IOptions<MeteringOptions> _meteringOptions;
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<SuspensionSweepService> _logger;

    public SuspensionSweepService(
        LeaseReconciliationService reconciler,
        TunnelDisconnectCoordinator coordinator,
        IOptionsMonitor<TunnelOptions> tunnelOptions,
        IOptions<MeteringOptions> meteringOptions,
        Db db,
        TimeProvider time,
        ILogger<SuspensionSweepService> logger)
    {
        _reconciler = reconciler;
        _coordinator = coordinator;
        _tunnelOptions = tunnelOptions;
        _meteringOptions = meteringOptions;
        _db = db;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Share the metering enable/DB gates: sweeping without a database is nonsensical (nothing to
        // sweep), and turning metering off is the operator kill-switch for the whole billing-adjacent
        // background surface (the sweep touches lease_holds).
        if (!_meteringOptions.Value.Enabled)
        {
            _logger.LogInformation("metering disabled by config — suspension sweep loop not started");
            return;
        }

        if (!_db.IsConfigured)
        {
            _logger.LogInformation("no database configured — suspension sweep loop not started");
            return;
        }

        _logger.LogInformation(
            "suspension sweep loop starting (tick every {IntervalSeconds}s, safety margin {SafetySeconds}s)",
            SweepInterval.TotalSeconds, SafetyMargin.TotalSeconds);

        using var timer = new PeriodicTimer(SweepInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // Idempotent + CAS-guarded — the next tick simply retries the same query.
                _logger.LogError(ex, "suspension sweep tick failed; will retry on the next tick");
            }
        }
    }

    /// <summary>
    /// One sweep pass. Public for the test surface (which drives the sweep deterministically without
    /// standing up the hosted-service loop).
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var grace = TimeSpan.FromSeconds(Math.Max(0, _tunnelOptions.CurrentValue.GraceSeconds));
        var cutoff = grace + SafetyMargin;
        var hostsUnderGrace = _coordinator.HostsUnderInProcessGrace();
        var ended = await _reconciler.SweepStaleSuspendedLeasesAsync(cutoff, hostsUnderGrace, ct);
        if (ended > 0)
        {
            _logger.LogWarning(
                "suspension sweep ended {Count} stale suspended lease(s) (grace {Grace}s + safety {Safety}s)",
                ended, grace.TotalSeconds, SafetyMargin.TotalSeconds);
        }

        return ended;
    }
}
