using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Payments;

/// <summary>Lifecycle of a single refund.</summary>
public enum RefundStatus : byte
{
    /// <summary>Accepted and applied to the payment, ledger entries written.</summary>
    Succeeded = 0,

    /// <summary>Rejected before any funds moved.</summary>
    Failed = 1,
}

/// <summary>
/// One refund issued against a captured payment.
/// </summary>
/// <remarks>
/// Refunds are stored as individual rows rather than folded into a single running total
/// on the payment. The payment keeps the total for invariant enforcement, but the rows
/// are what make a partially refunded payment auditable: without them, four refunds of
/// 25 and one of 100 are indistinguishable after the fact.
/// </remarks>
public sealed class Refund
{
    private Refund()
    {
        // Materialization constructor for the persistence layer.
    }

    public RefundId Id { get; private set; }

    public PaymentId PaymentId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public Currency Currency { get; private set; }

    public RefundStatus Status { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>
    /// The idempotency key the refund was requested under, retained so a replayed
    /// refund request can be answered from the stored row.
    /// </summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>
    /// The payment's status once this refund was applied, so the response to a replay
    /// matches the original byte for byte rather than reflecting later refunds.
    /// </summary>
    public PaymentStatus ResultingPaymentStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Money Amount => new(AmountMinorUnits, Currency);

    public static Refund Succeeded(
        RefundId id,
        PaymentId paymentId,
        Money amount,
        string reason,
        string idempotencyKey,
        PaymentStatus resultingPaymentStatus,
        DateTimeOffset now) => new()
        {
            Id = id,
            PaymentId = paymentId,
            AmountMinorUnits = amount.MinorUnits,
            Currency = amount.Currency,
            Status = RefundStatus.Succeeded,
            Reason = reason,
            IdempotencyKey = idempotencyKey,
            ResultingPaymentStatus = resultingPaymentStatus,
            CreatedAt = now,
        };
}
