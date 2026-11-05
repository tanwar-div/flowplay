using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Ledger;

/// <summary>Which side of the double entry a line sits on.</summary>
public enum LedgerDirection : byte
{
    /// <summary>Funds leaving an account.</summary>
    Debit = 0,

    /// <summary>Funds arriving in an account.</summary>
    Credit = 1,
}

/// <summary>What a ledger line represents, for reporting and reconciliation.</summary>
public enum LedgerEntryType : byte
{
    /// <summary>The payer's side of a capture.</summary>
    PaymentDebit = 0,

    /// <summary>The payee's side of a capture, net of fees.</summary>
    PaymentCredit = 1,

    /// <summary>The platform's fee income from a capture.</summary>
    FeeCredit = 2,

    /// <summary>The payee's side of a refund.</summary>
    RefundDebit = 3,

    /// <summary>The payer's side of a refund.</summary>
    RefundCredit = 4,
}

/// <summary>
/// One immutable line in the double-entry ledger.
/// </summary>
/// <remarks>
/// Ledger rows are append-only: there is no update or delete path for them anywhere in
/// the system. A correction is a new balancing pair, which means the ledger is a complete
/// history rather than a current-state cache, and account balances can always be
/// recomputed from it to check them against the stored values.
/// </remarks>
public sealed class LedgerEntry
{
    private LedgerEntry()
    {
        // Materialization constructor for the persistence layer.
    }

    public LedgerEntryId Id { get; private set; }

    public PaymentId PaymentId { get; private set; }

    /// <summary>Set when this line belongs to a refund rather than the original capture.</summary>
    public RefundId? RefundId { get; private set; }

    public AccountId AccountId { get; private set; }

    public LedgerDirection Direction { get; private set; }

    public LedgerEntryType EntryType { get; private set; }

    /// <summary>Always positive. The sign lives in <see cref="Direction"/>.</summary>
    public long AmountMinorUnits { get; private set; }

    public Currency Currency { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Money Amount => new(AmountMinorUnits, Currency);

    /// <summary>The amount signed for summation: negative for debits.</summary>
    public long SignedMinorUnits =>
        Direction == LedgerDirection.Credit ? AmountMinorUnits : -AmountMinorUnits;

    public static LedgerEntry Create(
        PaymentId paymentId,
        RefundId? refundId,
        AccountId accountId,
        LedgerDirection direction,
        LedgerEntryType entryType,
        Money amount,
        DateTimeOffset now)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "A ledger line must carry a positive amount.");
        }

        return new LedgerEntry
        {
            Id = LedgerEntryId.New(),
            PaymentId = paymentId,
            RefundId = refundId,
            AccountId = accountId,
            Direction = direction,
            EntryType = entryType,
            AmountMinorUnits = amount.MinorUnits,
            Currency = amount.Currency,
            CreatedAt = now,
        };
    }
}
