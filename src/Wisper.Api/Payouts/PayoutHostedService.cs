using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wisper.Api.Persistence;

namespace Wisper.Api.Payouts;

/// <summary>
/// The scheduled payout loop (docs/PAYMENTS.md §6, default daily). Each tick runs
/// <see cref="PayoutService.RunScheduledPayoutsAsync"/>, transferring accrued earnings to every eligible host;
/// a tick that throws is logged and the loop continues, so one bad run never stops payouts (failed transfers
/// retain earnings and retry on the next run). The loop is a no-op on a DB-less boot (the tunnel needs no
/// database) or when disabled by config.
/// </summary>
public sealed class PayoutHostedService : BackgroundService
{
    private readonly PayoutService _payouts;
    private readonly PayoutOptions _options;
    private readonly Db _db;
    private readonly TimeProvider _time;
    private readonly ILogger<PayoutHostedService> _logger;

    public PayoutHostedService(
        PayoutService payouts,
        IOptions<PayoutOptions> options,
        Db db,
        TimeProvider time,
        ILogger<PayoutHostedService> logger)
    {
        _payouts = payouts;
        _options = options.Value;
        _db = db;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("payouts disabled by config — scheduled payout loop not started");
            return;
        }

        if (!_db.IsConfigured)
        {
            _logger.LogInformation("no database configured — payout loop not started (tunnel-only boot)");
            return;
        }

        _logger.LogInformation(
            "payout loop starting (every {IntervalHours}h, minimum {Min}¢)",
            _options.IntervalHours, _options.PayoutMinCents);

        using var timer = new PeriodicTimer(_options.Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var paid = await _payouts.RunScheduledPayoutsAsync(stoppingToken);
                if (paid > 0)
                {
                    _logger.LogInformation("payout run paid {Paid} host(s)", paid);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutting down
            }
            catch (Exception ex)
            {
                // A failed run must not stop payouts — earnings are durable in the ledger and the next run
                // retries every eligible host (docs/PAYMENTS.md §6).
                _logger.LogError(ex, "payout run failed; will retry on the next tick");
            }
        }
    }
}
