using Wisper.Api.Domain;
using Wisper.Api.Infrastructure;
using Wisper.Api.Leases;
using Wisper.Api.Ledger;
using Wisper.Api.Persistence.Leases;

namespace Wisper.Api.Policy;

/// <summary>
/// The day-one fraud guards (docs/PAYMENTS.md §7, §13) — cheap, deterministic <c>platform_policy</c> checks
/// that make refunds/disputes/chargebacks rare, enforced <b>up front</b> at top-up and lease start (never an
/// ML system — that would be theater on ~zero history). Three guards, all read from the active policy so an
/// unset limit (<c>null</c>) is simply "no limit":
/// <list type="bullet">
/// <item><b>first-top-up hold</b> — a fresh account's <i>first-ever</i> top-up is capped
/// (<see cref="PlatformPolicy.FirstTopupMaxCents"/>) until a payment materially clears the dispute window.</item>
/// <item><b>new-account velocity</b> — while an account is younger than
/// <see cref="PlatformPolicy.NewAccountWindowHours"/>, its cumulative top-up per rolling 24h is capped
/// (<see cref="PlatformPolicy.NewAccountMaxTopupCentsPerDay"/>).</item>
/// <item><b>per-user spend cap</b> — a user's cumulative lease commitment per rolling 24h is capped
/// (<see cref="PlatformPolicy.MaxSpendCentsPerDay"/>), measured by the up-front lease holds that bound spend.</item>
/// </list>
/// The per-user <b>concurrency</b> cap (<see cref="PlatformPolicy.MaxConcurrentLeasesPerUser"/>) is the fourth
/// guard, enforced next door in <see cref="WalletLeaseGate"/>. A breach throws
/// <see cref="ApiErrorCode.LimitExceeded"/> (429). Built against the ledger + lease repository so it is
/// unit-testable without Postgres (Grunt has no database).
/// </summary>
public sealed class FraudGuardService
{
    /// <summary>The single currency v0 supports (docs/PAYMENTS.md §13, docs/DATA_MODEL.md §16).</summary>
    public const string Currency = "usd";

    /// <summary>The rolling window the velocity/spend limits are measured over (docs/PAYMENTS.md §7).</summary>
    private static readonly TimeSpan VelocityWindow = TimeSpan.FromHours(24);

    private readonly LedgerService _ledger;
    private readonly ILeaseRepository _leases;
    private readonly PlatformPolicyService _policy;
    private readonly TimeProvider _time;
    private readonly ILogger<FraudGuardService> _logger;

    public FraudGuardService(
        LedgerService ledger,
        ILeaseRepository leases,
        PlatformPolicyService policy,
        TimeProvider time,
        ILogger<FraudGuardService> logger)
    {
        _ledger = ledger;
        _leases = leases;
        _policy = policy;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The top-up guards (docs/PAYMENTS.md §7): the first-top-up hold and the new-account top-up velocity
    /// limit, both read from the active policy. Called from <see cref="Billing.BillingService"/> before any
    /// PaymentIntent is created, so a limited request never even reaches Stripe. Throws
    /// <see cref="ApiErrorCode.LimitExceeded"/> on a breach; no policy or an unset limit ⇒ allowed.
    /// </summary>
    public async Task EnforceTopupAllowedAsync(User user, long amountCents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var policy = await _policy.GetActiveAsync(ct);
        if (policy is null)
        {
            return;
        }

        // The wallet's own topup transactions are the history both guards read (newest-first).
        var walletTxns = await _ledger.ListWalletTransactionsAsync(user.Id, Currency, ct);
        var topups = walletTxns.Where(t => t.Transaction.Kind == LedgerTxnKind.Topup).ToList();

        // First-top-up hold: cap the very first funding of a fresh wallet (docs/PAYMENTS.md §7).
        if (topups.Count == 0 && policy.FirstTopupMaxCents is { } firstCap && amountCents > firstCap)
        {
            _logger.LogInformation(
                "top-up blocked for user {User}: first top-up {Amount}¢ exceeds first-top-up cap {Cap}¢",
                user.Id, amountCents, firstCap);
            throw new ApiException(
                ApiErrorCode.LimitExceeded,
                $"A first top-up is limited to {firstCap} cents.",
                new { field = "amount_cents", first_topup_max_cents = firstCap });
        }

        // New-account top-up velocity: while the account is new, cap cumulative top-up per rolling 24h.
        if (IsNewAccount(user, policy) && policy.NewAccountMaxTopupCentsPerDay is { } dayCap)
        {
            var windowStart = _time.GetUtcNow() - VelocityWindow;
            var recent = topups
                .Where(t => t.Transaction.CreatedAt >= windowStart)
                .Sum(t => Math.Max(0, t.SignedAmountCents));  // a top-up credits the wallet (positive)
            if (recent + amountCents > dayCap)
            {
                _logger.LogInformation(
                    "top-up blocked for new user {User}: {Recent}¢ + {Amount}¢ exceeds new-account daily cap {Cap}¢",
                    user.Id, recent, amountCents, dayCap);
                throw new ApiException(
                    ApiErrorCode.LimitExceeded,
                    $"New-account top-ups are limited to {dayCap} cents per day.",
                    new { field = "amount_cents", new_account_max_topup_cents_per_day = dayCap, recent_cents = recent });
            }
        }
    }

    /// <summary>
    /// The per-user daily spend cap (docs/PAYMENTS.md §7), read from the active policy and enforced at lease
    /// start (from <see cref="WalletLeaseGate"/>). "Spend" is measured by the up-front lease holds
    /// (<c>⌈ttl/60⌉·price</c>) — the amount a lease commits — so the cap bounds authorized spend before any
    /// compute runs. <paramref name="projectedHoldCents"/> is the hold the pending lease would place. Throws
    /// <see cref="ApiErrorCode.LimitExceeded"/> on a breach; no policy or an unset cap ⇒ allowed.
    /// </summary>
    public async Task EnforceLeaseSpendAllowedAsync(
        Guid consumerUserId, long projectedHoldCents, CancellationToken ct = default)
    {
        var policy = await _policy.GetActiveAsync(ct);
        if (policy?.MaxSpendCentsPerDay is not { } dayCap)
        {
            return;
        }

        var windowStart = _time.GetUtcNow() - VelocityWindow;
        var leases = await _leases.ListByConsumerAsync(consumerUserId, ct);
        var recent = leases
            .Where(l => l.CreatedAt >= windowStart)
            .Sum(l => LeaseHoldPricing.EstimateHoldCents(l.TtlSeconds, l.PriceCentsPerMin));

        if (recent + projectedHoldCents > dayCap)
        {
            _logger.LogInformation(
                "lease blocked for user {User}: committed {Recent}¢ + {Hold}¢ exceeds daily spend cap {Cap}¢",
                consumerUserId, recent, projectedHoldCents, dayCap);
            throw new ApiException(
                ApiErrorCode.LimitExceeded,
                $"Your daily spend is limited to {dayCap} cents.",
                new { max_spend_cents_per_day = dayCap, committed_cents = recent, required_cents = projectedHoldCents });
        }
    }

    /// <summary>Whether <paramref name="user"/> is still within the policy's new-account window.</summary>
    private bool IsNewAccount(User user, PlatformPolicy policy)
    {
        if (policy.NewAccountWindowHours is not { } hours || hours <= 0)
        {
            return false;
        }

        return user.CreatedAt > _time.GetUtcNow() - TimeSpan.FromHours(hours);
    }
}
