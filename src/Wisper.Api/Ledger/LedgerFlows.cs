using Wisper.Api.Domain;

namespace Wisper.Api.Ledger;

/// <summary>
/// The §8 money flows as balanced-transaction builders (docs/DATA_MODEL.md §8). Each returns a
/// <see cref="TransactionDraft"/> whose entries satisfy <c>Σ debit = Σ credit</c> by construction, with
/// the debit/credit side of every leg chosen by the account's normal side (§7). Post one with
/// <see cref="LedgerService.PostAsync"/>. Amounts are integer cents; a builder throws
/// <see cref="ArgumentOutOfRangeException"/> on a nonsensical amount so a bad split never reaches the
/// ledger.
/// </summary>
public static class LedgerFlows
{
    /// <summary>
    /// Splits a metered charge into (platform fee, host payout) by <paramref name="feeBps"/> basis points
    /// (docs/DATA_MODEL.md §8, §11). The fee is floored so <c>fee + host = amount</c> exactly (integer
    /// cents, no rounding drift), matching the <c>lease_usage</c> check <c>amount = fee + host</c> (§6).
    /// </summary>
    public static (long PlatformFeeCents, long HostPayoutCents) SplitFee(long amountCents, int feeBps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountCents);
        ArgumentOutOfRangeException.ThrowIfNegative(feeBps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(feeBps, 10_000);

        var fee = amountCents * feeBps / 10_000;   // integer floor
        return (fee, amountCents - fee);
    }

    /// <summary>
    /// <b>top-up</b> — a consumer funds their wallet (docs/DATA_MODEL.md §8; on Stripe
    /// <c>payment_intent.succeeded</c>). Debit <c>platform_cash</c> the net cash and <c>stripe_fees</c> the
    /// processor fee the platform absorbs; credit <c>user_wallet</c> the gross. Keyed by the Stripe event id.
    /// </summary>
    public static TransactionDraft Topup(
        Guid walletAccountId,
        Guid platformCashAccountId,
        Guid stripeFeesAccountId,
        long grossAmountCents,
        long stripeFeeCents,
        string idempotencyKey,
        string? externalRef = null,
        string? memo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grossAmountCents);
        ArgumentOutOfRangeException.ThrowIfNegative(stripeFeeCents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stripeFeeCents, grossAmountCents);

        var entries = new List<EntryDraft>(3)
        {
            EntryDraft.Debit(platformCashAccountId, grossAmountCents - stripeFeeCents),
            EntryDraft.Credit(walletAccountId, grossAmountCents),
        };
        if (stripeFeeCents > 0)
        {
            entries.Add(EntryDraft.Debit(stripeFeesAccountId, stripeFeeCents));
        }

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.Topup,
            Entries = entries,
            ExternalRef = externalRef,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
        };
    }

    /// <summary>
    /// <b>lease_hold</b> — earmark the estimated max out of the wallet at <c>POST /leases</c>
    /// (docs/DATA_MODEL.md §8). Debit <c>user_wallet</c>, credit <c>lease_holds</c>. The non-negative
    /// wallet guard (§7d) is the hard gate: an unaffordable hold fails and no compute is provisioned.
    /// </summary>
    public static TransactionDraft LeaseHold(
        Guid walletAccountId,
        Guid leaseHoldsAccountId,
        Guid leaseId,
        long amountCents,
        string? idempotencyKey = null,
        string? memo = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.LeaseHold,
            LeaseId = leaseId,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = new List<EntryDraft>(2)
            {
                EntryDraft.Debit(walletAccountId, amountCents, leaseId),
                EntryDraft.Credit(leaseHoldsAccountId, amountCents, leaseId),
            },
        };
    }

    /// <summary>
    /// <b>lease_charge</b> — bill a metered tick out of the hold (docs/DATA_MODEL.md §8). Debit
    /// <c>lease_holds</c> the charge; credit <c>host_earnings</c> the payout and <c>platform_revenue</c> the
    /// fee, split by <see cref="SplitFee"/> from <c>platform_policy.fee_bps</c> (§11). Because the hold
    /// covered the whole lease, the hold can never be exhausted mid-run.
    /// </summary>
    public static TransactionDraft LeaseCharge(
        Guid leaseHoldsAccountId,
        Guid hostEarningsAccountId,
        Guid platformRevenueAccountId,
        Guid leaseId,
        long amountCents,
        long platformFeeCents,
        string? idempotencyKey = null,
        string? memo = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);
        ArgumentOutOfRangeException.ThrowIfNegative(platformFeeCents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(platformFeeCents, amountCents);

        var hostPayoutCents = amountCents - platformFeeCents;
        var entries = new List<EntryDraft>(3)
        {
            EntryDraft.Debit(leaseHoldsAccountId, amountCents, leaseId),
        };
        if (hostPayoutCents > 0)
        {
            entries.Add(EntryDraft.Credit(hostEarningsAccountId, hostPayoutCents, leaseId));
        }

        if (platformFeeCents > 0)
        {
            entries.Add(EntryDraft.Credit(platformRevenueAccountId, platformFeeCents, leaseId));
        }

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.LeaseCharge,
            LeaseId = leaseId,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = entries,
        };
    }

    /// <summary>
    /// <b>hold_release</b> — return the unused remainder of a hold to the wallet at lease end
    /// (docs/DATA_MODEL.md §8). Debit <c>lease_holds</c>, credit <c>user_wallet</c>.
    /// </summary>
    public static TransactionDraft HoldRelease(
        Guid leaseHoldsAccountId,
        Guid walletAccountId,
        Guid leaseId,
        long amountCents,
        string? idempotencyKey = null,
        string? memo = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.HoldRelease,
            LeaseId = leaseId,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = new List<EntryDraft>(2)
            {
                EntryDraft.Debit(leaseHoldsAccountId, amountCents, leaseId),
                EntryDraft.Credit(walletAccountId, amountCents, leaseId),
            },
        };
    }

    /// <summary>
    /// <b>payout</b> — pay accrued host earnings out via a Stripe Connect transfer (docs/DATA_MODEL.md §8,
    /// docs/PAYMENTS.md §6). Debit <c>host_earnings</c>, credit <c>platform_cash</c> (money leaves the
    /// platform). Keyed by the <c>payouts.id</c> so a retried run can't double-pay.
    /// </summary>
    public static TransactionDraft Payout(
        Guid hostEarningsAccountId,
        Guid platformCashAccountId,
        long amountCents,
        string idempotencyKey,
        string? externalRef = null,
        string? memo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.Payout,
            ExternalRef = externalRef,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = new List<EntryDraft>(2)
            {
                EntryDraft.Debit(hostEarningsAccountId, amountCents),
                EntryDraft.Credit(platformCashAccountId, amountCents),
            },
        };
    }

    /// <summary>
    /// <b>refund</b> — reverse a top-up of unspent wallet credits (docs/DATA_MODEL.md §8, docs/PAYMENTS.md
    /// §3, §7). Debit <c>user_wallet</c> the gross; credit <c>platform_cash</c> the net cash and
    /// <c>stripe_fees</c> the fee. Requires wallet funds (§7d); a shortfall is a clawback policy question.
    /// </summary>
    public static TransactionDraft Refund(
        Guid walletAccountId,
        Guid platformCashAccountId,
        Guid stripeFeesAccountId,
        long grossAmountCents,
        long stripeFeeCents,
        string idempotencyKey,
        string? externalRef = null,
        string? memo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grossAmountCents);
        ArgumentOutOfRangeException.ThrowIfNegative(stripeFeeCents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stripeFeeCents, grossAmountCents);

        var entries = new List<EntryDraft>(3)
        {
            EntryDraft.Debit(walletAccountId, grossAmountCents),
            EntryDraft.Credit(platformCashAccountId, grossAmountCents - stripeFeeCents),
        };
        if (stripeFeeCents > 0)
        {
            entries.Add(EntryDraft.Credit(stripeFeesAccountId, stripeFeeCents));
        }

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.Refund,
            ExternalRef = externalRef,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = entries,
        };
    }

    /// <summary>
    /// <b>chargeback</b> — a consumer disputes a top-up and the card network claws the money back
    /// (docs/DATA_MODEL.md §8, docs/PAYMENTS.md §7, on Stripe <c>charge.dispute.created</c>). Debit
    /// <c>user_wallet</c> the disputed gross (the consumer loses those credits) and credit
    /// <c>platform_cash</c> the same (the money left the platform to the network). This is the <b>one</b>
    /// documented case a wallet may go <b>negative</b> — a genuine debt, when the consumer already spent the
    /// credits (the ledger's non-negative guard makes an explicit exception for <see cref="LedgerTxnKind.Chargeback"/>).
    /// Keyed by the Stripe event id so a re-delivered dispute is a no-op. The platform absorbs already-paid
    /// host earnings (§7) — no host clawback is posted here.
    /// </summary>
    public static TransactionDraft Chargeback(
        Guid walletAccountId,
        Guid platformCashAccountId,
        long amountCents,
        string idempotencyKey,
        string? externalRef = null,
        string? memo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountCents);

        return new TransactionDraft
        {
            Kind = LedgerTxnKind.Chargeback,
            ExternalRef = externalRef,
            IdempotencyKey = idempotencyKey,
            Memo = memo,
            Entries = new List<EntryDraft>(2)
            {
                EntryDraft.Debit(walletAccountId, amountCents),
                EntryDraft.Credit(platformCashAccountId, amountCents),
            },
        };
    }
}
