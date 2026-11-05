using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Ledger;

/// <summary>
/// Builds and checks balanced sets of ledger lines.
/// </summary>
/// <remarks>
/// Every money movement in FlowPay is written as a set of lines whose debits and credits
/// sum to the same value. That single property is the system's correctness oracle: the
/// integration tests and the 100,000-transaction simulation both verify it over the whole
/// ledger rather than asserting on individual balances, which means they catch a bug in
/// any code path that moved money, including ones they do not know about.
/// </remarks>
public static class DoubleEntry
{
    /// <summary>Number of lines a capture produces when a fee is charged.</summary>
    public const int CaptureLinesWithFee = 3;

    /// <summary>Number of lines a capture produces when the fee is zero.</summary>
    public const int CaptureLinesWithoutFee = 2;

    /// <summary>Number of lines a refund produces.</summary>
    public const int RefundLines = 2;

    /// <summary>
    /// Writes the lines for a capture: the payer is debited the gross amount, the payee
    /// is credited the net, and the platform fee account is credited the fee.
    /// </summary>
    /// <returns>The number of lines written, or -1 if the inputs are inconsistent.</returns>
    public static int WriteCapture(
        PaymentId paymentId,
        AccountId payerAccountId,
        AccountId payeeAccountId,
        AccountId feeAccountId,
        Money grossAmount,
        Money fee,
        DateTimeOffset now,
        Span<LedgerEntry> destination)
    {
        if (destination.Length < CaptureLinesWithFee ||
            grossAmount.Currency != fee.Currency ||
            !grossAmount.IsPositive ||
            fee.IsNegative ||
            !grossAmount.TrySubtract(fee, out Money net) ||
            !net.IsPositive)
        {
            return -1;
        }

        int count = 0;

        destination[count++] = LedgerEntry.Create(
            paymentId, null, payerAccountId,
            LedgerDirection.Debit, LedgerEntryType.PaymentDebit, grossAmount, now);

        destination[count++] = LedgerEntry.Create(
            paymentId, null, payeeAccountId,
            LedgerDirection.Credit, LedgerEntryType.PaymentCredit, net, now);

        // A zero fee would otherwise produce a zero-amount line, which the ledger
        // forbids: a line that moves nothing is noise in the audit trail.
        if (fee.IsPositive)
        {
            destination[count++] = LedgerEntry.Create(
                paymentId, null, feeAccountId,
                LedgerDirection.Credit, LedgerEntryType.FeeCredit, fee, now);
        }

        return count;
    }

    /// <summary>
    /// Writes the lines for a refund: the payee is debited and the payer is credited.
    /// </summary>
    /// <remarks>
    /// The processing fee is not returned, which matches how card networks and most
    /// processors behave. It also keeps the pair balanced without needing a third line
    /// to claw back a fraction of a fee that was already recognized as income.
    /// </remarks>
    /// <returns>The number of lines written, or -1 if the inputs are inconsistent.</returns>
    public static int WriteRefund(
        PaymentId paymentId,
        RefundId refundId,
        AccountId payerAccountId,
        AccountId payeeAccountId,
        Money amount,
        DateTimeOffset now,
        Span<LedgerEntry> destination)
    {
        if (destination.Length < RefundLines || !amount.IsPositive)
        {
            return -1;
        }

        destination[0] = LedgerEntry.Create(
            paymentId, refundId, payeeAccountId,
            LedgerDirection.Debit, LedgerEntryType.RefundDebit, amount, now);

        destination[1] = LedgerEntry.Create(
            paymentId, refundId, payerAccountId,
            LedgerDirection.Credit, LedgerEntryType.RefundCredit, amount, now);

        return RefundLines;
    }

    /// <summary>
    /// True when debits and credits in <paramref name="entries"/> cancel out exactly.
    /// </summary>
    public static bool IsBalanced(ReadOnlySpan<LedgerEntry> entries)
    {
        long net = 0L;

        for (int i = 0; i < entries.Length; i++)
        {
            net += entries[i].SignedMinorUnits;
        }

        return net == 0L;
    }

    /// <summary>
    /// The net of a set of lines, for reporting the size of an imbalance rather than
    /// just its existence.
    /// </summary>
    public static long NetMinorUnits(ReadOnlySpan<LedgerEntry> entries)
    {
        long net = 0L;

        for (int i = 0; i < entries.Length; i++)
        {
            net += entries[i].SignedMinorUnits;
        }

        return net;
    }
}
