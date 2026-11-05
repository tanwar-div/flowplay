using FlowPay.Application.Abstractions;
using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.Extensions.Options;

namespace FlowPay.Application.Payments;

/// <summary>The outcome of a settlement, for the caller's response and event payload.</summary>
public readonly record struct SettlementBreakdown(Money Gross, Money Fee, Money Net);

/// <summary>
/// The mechanics of moving money: fee calculation, balance updates and ledger lines.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the capture-on-authorize path and the explicit capture endpoint so there is
/// one implementation of how a capture affects balances and the ledger. Two copies of
/// this logic that drifted apart would be a silent correctness bug, not a style problem.
/// </para>
/// <para>
/// The methods mutate the aggregates they are given as they go and return on the first
/// failure, which can leave the in-memory objects partially updated. That is safe because
/// every caller runs inside a transaction that it rolls back on failure, and the
/// change-tracked entities are discarded with it — nothing partially applied is ever
/// persisted. The alternative, validating everything before touching anything, would mean
/// duplicating each aggregate's invariants here, where they could fall out of step with
/// the aggregate's own checks.
/// </para>
/// </remarks>
public sealed class SettlementEngine
{
    private readonly ILedgerRepository _ledger;
    private readonly FlowPayOptions _options;

    public SettlementEngine(ILedgerRepository ledger, IOptions<FlowPayOptions> options)
    {
        _ledger = ledger;
        _options = options.Value;
    }

    /// <summary>
    /// The fee account a given payment's fee is credited to.
    /// </summary>
    /// <remarks>
    /// The shard is derived from the payment id, so a retried capture always resolves to
    /// the same account. Sharding exists because a single fee account per currency puts
    /// one row in every capture's transaction and serializes the entire capture path —
    /// see <see cref="FeeAccountShards"/>.
    /// </remarks>
    /// <returns>False when no fee account is configured for that currency.</returns>
    public bool TryGetFeeAccountId(Currency currency, PaymentId paymentId, out AccountId feeAccountId)
    {
        if (!_options.PlatformFeeAccounts.TryGetValue(currency.ToString(), out Guid baseId)
            || baseId == Guid.Empty)
        {
            feeAccountId = default;
            return false;
        }

        int shardCount = Math.Clamp(_options.FeeAccountShards, 1, FeeAccountShards.MaxShards);
        int shard = FeeAccountShards.SelectShard(paymentId, shardCount);

        feeAccountId = FeeAccountShards.Derive(baseId, shard);
        return true;
    }

    /// <summary>Every fee account id for a currency, for provisioning and reporting.</summary>
    public IReadOnlyList<AccountId> GetFeeAccountFamily(string currencyCode)
    {
        if (!_options.PlatformFeeAccounts.TryGetValue(currencyCode, out Guid baseId)
            || baseId == Guid.Empty)
        {
            return [];
        }

        int shardCount = Math.Clamp(_options.FeeAccountShards, 1, FeeAccountShards.MaxShards);

        AccountId[] family = new AccountId[shardCount];
        FeeAccountShards.DeriveAll(baseId, shardCount, family);
        return family;
    }

    /// <summary>The fee rule for a currency.</summary>
    public FeeSchedule ScheduleFor(Currency currency)
        => new(currency, _options.FeeBasisPoints, _options.FeeFixedMinorUnits);

    /// <summary>
    /// Settles an authorized payment: draws down the payer's reservation, credits the
    /// payee the net amount, credits the fee account, and appends the ledger lines.
    /// </summary>
    public DomainResult Capture(
        PaymentIntent payment,
        Account payer,
        Account payee,
        Account feeAccount,
        DateTimeOffset now,
        out SettlementBreakdown breakdown)
    {
        breakdown = default;

        Money gross = payment.Amount;

        if (!ScheduleFor(gross.Currency).TryComputeSettlement(gross, out Money fee, out Money net))
        {
            return DomainResult.Failure(DomainError.AmountExceedsLimit);
        }

        DomainResult result = payer.TryCaptureReserved(gross, now);
        if (result.IsFailure)
        {
            return result;
        }

        result = payee.TryCredit(net, now);
        if (result.IsFailure)
        {
            return result;
        }

        if (fee.IsPositive)
        {
            result = feeAccount.TryCredit(fee, now);
            if (result.IsFailure)
            {
                return result;
            }
        }

        result = payment.TryCapture(fee, now);
        if (result.IsFailure)
        {
            return result;
        }

        Span<LedgerEntry> entries = new LedgerEntry[DoubleEntry.CaptureLinesWithFee];

        int written = DoubleEntry.WriteCapture(
            payment.Id,
            payer.Id,
            payee.Id,
            feeAccount.Id,
            gross,
            fee,
            now,
            entries);

        if (written < 0)
        {
            return DomainResult.Failure(DomainError.ArithmeticOverflow);
        }

        _ledger.AddRange(entries[..written]);

        breakdown = new SettlementBreakdown(gross, fee, net);
        return DomainResult.Success;
    }

    /// <summary>
    /// Applies a refund: debits the payee, credits the payer, updates the payment's
    /// refunded total and appends the ledger lines.
    /// </summary>
    public DomainResult Refund(
        PaymentIntent payment,
        Account payer,
        Account payee,
        RefundId refundId,
        in Money amount,
        DateTimeOffset now,
        out PaymentStatus resultingStatus)
    {
        // The payment's own check runs first: it is the one that enforces the
        // over-refund invariant, and it must be evaluated against the FOR UPDATE-locked
        // row before any balance is touched.
        DomainResult result = payment.TryRefund(amount, now, out resultingStatus);
        if (result.IsFailure)
        {
            return result;
        }

        result = payee.TryDebit(amount, now);
        if (result.IsFailure)
        {
            return result;
        }

        result = payer.TryCredit(amount, now);
        if (result.IsFailure)
        {
            return result;
        }

        Span<LedgerEntry> entries = new LedgerEntry[DoubleEntry.RefundLines];

        int written = DoubleEntry.WriteRefund(
            payment.Id,
            refundId,
            payer.Id,
            payee.Id,
            amount,
            now,
            entries);

        if (written < 0)
        {
            return DomainResult.Failure(DomainError.ArithmeticOverflow);
        }

        _ledger.AddRange(entries[..written]);

        return DomainResult.Success;
    }
}
