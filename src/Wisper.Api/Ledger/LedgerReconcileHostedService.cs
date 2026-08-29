using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wisper.Api.Persistence;

namespace Wisper.Api.Ledger;

/// <summary>
/// The scheduled ledger reconciliation loop (docs/DATA_MODEL.md §7e, §14): on a fixed tick
/// (default 15 minutes), re-derives every account's balance from the immutable journal and compares it
/// against the maintained <c>ledger_accounts.balance_cents</c>. Any non-zero drift is logged at warning
/// and reflected on the admin overview via <see cref="LedgerReconcileMonitor"/>. Balances are a cache;
/// the journal is truth.
/// <para>
/// <b>Off in the in-memory persistence mode.</b> The loop gates on a configured database (nothing to
/// reconcile without one). It is also off when disabled by config. A tick that throws is logged and the
/// loop continues, so one bad pass never stops reconciliation.
/// </para>
/// <para>
/// <b>Multi-instance safe.</b> Every tick attempts a PostgreSQL session-scoped advisory lock
/// (<see cref="PostgresAdvisoryLock.Keys.LedgerReconcile"/>). Exactly one instance runs the pass; every
/// other instance's tick observes the lock held and skips. The lock is released automatically if the
/// winner's connection drops, so a crash cannot wedge the loop.
/// </para>
/// </summary>
public sealed class LedgerReconcileHostedService : BackgroundService
{
    private readonly LedgerService _ledger;
    private readonly LedgerReconcileMonitor _monitor;
    private readonly LedgerReconcileOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<LedgerReconcileHostedService> _logger;

    public LedgerReconcileHostedService(
        LedgerService ledger,
        LedgerReconcileMonitor monitor,
        IOptions<LedgerReconcileOptions> options,
        Db db,
        TimeProvider time,
        ILogger<LedgerReconcileHostedService> logger)
    {
        _ledger = ledger;
        _monitor = monitor;
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
                "ledger reconcile disabled by config; background loop not started");
            return;
        }

        if (!_db.IsConfigured)
        {
            _logger.LogInformation(
                "no database configured; ledger reconcile loop not started (in-memory persistence mode)");
            return;
        }

        _logger.LogInformation(
            "ledger reconcile loop starting (tick every {IntervalMinutes}m)", _options.IntervalMinutes);

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
                _logger.LogError(ex, "ledger reconcile tick failed; will retry on the next tick");
            }
        }
    }

    /// <summary>
    /// One reconciliation tick as the loop drives it: takes the multi-instance advisory lock first, and
    /// if another instance already holds it, skips this pass (returns <c>null</c>). On success delegates
    /// to <see cref="ReconcileOnceAsync"/>. Public for the test surface.
    /// </summary>
    public async Task<LedgerReconcileSummary?> RunOnceAsync(CancellationToken ct = default)
    {
        await using var handle = await PostgresAdvisoryLock.TryAcquireAsync(
            _db, PostgresAdvisoryLock.Keys.LedgerReconcile, ct);
        if (handle is null)
        {
            _logger.LogDebug(
                "ledger reconcile tick skipped; another instance holds the advisory lock");
            return null;
        }

        return await ReconcileOnceAsync(ct);
    }

    /// <summary>
    /// The actual reconciliation pass: re-derives every account's balance from the journal, records the
    /// summary on <see cref="LedgerReconcileMonitor"/>, and logs the outcome (warning per drifted account
    /// so an operator sees exactly which cache is wrong). Bypasses the advisory lock, so a test can drive
    /// it deterministically without a live Postgres. Callers running in production go through
    /// <see cref="RunOnceAsync"/>.
    /// </summary>
    public async Task<LedgerReconcileSummary> ReconcileOnceAsync(CancellationToken ct = default)
    {
        var results = await _ledger.ReconcileAsync(ct);
        var drifted = results.Where(r => !r.IsBalanced).ToList();
        var totalAbsDrift = drifted.Sum(r => Math.Abs(r.DriftCents));
        var summary = new LedgerReconcileSummary(
            _time.GetUtcNow(), results.Count, drifted.Count, totalAbsDrift);
        _monitor.Record(summary);

        if (drifted.Count == 0)
        {
            _logger.LogInformation(
                "ledger reconcile: {AccountCount} account(s) checked, no drift",
                results.Count);
        }
        else
        {
            _logger.LogWarning(
                "ledger reconcile: {DriftCount}/{AccountCount} account(s) drift " +
                "(total |drift| {TotalAbsDriftCents}c); balance cache disagrees with the journal",
                drifted.Count, results.Count, totalAbsDrift);
            foreach (var d in drifted)
            {
                _logger.LogWarning(
                    "ledger drift: account {AccountId} ({Kind}, owner {OwnerUserId}) " +
                    "maintained {MaintainedCents}c vs derived {DerivedCents}c (drift {DriftCents}c)",
                    d.AccountId, d.Kind, d.OwnerUserId, d.MaintainedBalanceCents,
                    d.DerivedBalanceCents, d.DriftCents);
            }
        }

        return summary;
    }
}
