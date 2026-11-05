using System.Text;
using FlowPay.Application.Abstractions;
using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Application.Payments;

/// <summary>
/// The read side of the API, plus the invariant check the correctness suite calls.
/// </summary>
/// <remarks>
/// Reads never open a transaction and never take row locks. A payment lookup that queued
/// behind a refund's <c>FOR UPDATE</c> would put write contention into read latency, and
/// a reader that sees a payment one transaction out of date is harmless.
/// </remarks>
public sealed class PaymentQueryService
{
    private readonly IPaymentRepository _payments;
    private readonly IAccountRepository _accounts;
    private readonly ILedgerRepository _ledger;

    public PaymentQueryService(
        IPaymentRepository payments,
        IAccountRepository accounts,
        ILedgerRepository ledger)
    {
        _payments = payments;
        _accounts = accounts;
        _ledger = ledger;
    }

    public async Task<PaymentDto?> GetPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        PaymentIntent? payment = await _payments
            .FindAsync(new PaymentId(paymentId), cancellationToken)
            .ConfigureAwait(false);

        return payment is null ? null : PaymentMapper.ToDto(payment);
    }

    public async Task<AccountDto?> GetAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        Account? account = await _accounts
            .FindAsync(new AccountId(accountId), cancellationToken)
            .ConfigureAwait(false);

        return account is null ? null : PaymentMapper.ToDto(account);
    }

    public async Task<LedgerEntryDto[]> GetPaymentLedgerAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LedgerEntry> entries = await _ledger
            .ListForPaymentAsync(new PaymentId(paymentId), cancellationToken)
            .ConfigureAwait(false);

        LedgerEntryDto[] dtos = new LedgerEntryDto[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            dtos[i] = PaymentMapper.ToDto(entries[i]);
        }

        return dtos;
    }

    /// <summary>
    /// Checks every system-wide invariant and reports what, if anything, is broken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the oracle the load simulation and the stress tests assert against. The
    /// four checks are chosen so that between them, no code path can move money
    /// incorrectly without one of them noticing:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the ledger's signed total must be zero — money is neither
    /// created nor destroyed;</description></item>
    /// <item><description>each payment's own lines must balance — which localizes any
    /// imbalance to the operation that caused it;</description></item>
    /// <item><description>no payment may be refunded beyond its captured amount — the
    /// invariant the concurrent refund tests attack;</description></item>
    /// <item><description>every stored balance must equal the ledger's view of it — which
    /// catches a balance update that happened without a matching ledger line.</description></item>
    /// </list>
    /// </remarks>
    public async Task<InvariantReportDto> CheckInvariantsAsync(
        CancellationToken cancellationToken = default)
    {
        long ledgerNet = await _ledger
            .SumSignedMinorUnitsAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PaymentImbalance> unbalanced = await _ledger
            .FindUnbalancedPaymentsAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PaymentId> overRefunded = await _payments
            .FindOverRefundedAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<BalanceDiscrepancy> discrepancies = await _accounts
            .FindBalanceDiscrepanciesAsync(cancellationToken)
            .ConfigureAwait(false);

        List<string> details = [];

        if (ledgerNet != 0L)
        {
            details.Add($"Ledger does not balance: net {ledgerNet} minor units.");
        }

        foreach (PaymentImbalance imbalance in unbalanced.Take(20))
        {
            details.Add(
                $"Payment {imbalance.PaymentId.Value} is unbalanced by {imbalance.NetMinorUnits} minor units.");
        }

        foreach (PaymentId paymentId in overRefunded.Take(20))
        {
            details.Add($"Payment {paymentId.Value} is refunded beyond its captured amount.");
        }

        foreach (BalanceDiscrepancy discrepancy in discrepancies.Take(20))
        {
            details.Add(
                $"Account {discrepancy.AccountId.Value} balance {discrepancy.StoredMinorUnits} " +
                $"differs from ledger {discrepancy.LedgerMinorUnits} " +
                $"(delta {discrepancy.DeltaMinorUnits}).");
        }

        bool healthy = ledgerNet == 0L
            && unbalanced.Count == 0
            && overRefunded.Count == 0
            && discrepancies.Count == 0;

        return new InvariantReportDto(
            healthy,
            ledgerNet,
            unbalanced.Count,
            overRefunded.Count,
            discrepancies.Count,
            details.ToArray());
    }
}
