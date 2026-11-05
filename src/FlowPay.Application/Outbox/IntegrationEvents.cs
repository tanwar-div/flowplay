namespace FlowPay.Application.Outbox;

/// <summary>
/// The event names consumers bind to. String constants rather than reflected type names
/// so renaming a C# record cannot silently break a running consumer's routing.
/// </summary>
public static class EventTypes
{
    public const string PaymentAuthorized = "payment.authorized";
    public const string PaymentCaptured = "payment.captured";
    public const string PaymentFailed = "payment.failed";
    public const string PaymentVoided = "payment.voided";
    public const string PaymentExpired = "payment.expired";
    public const string PaymentRefunded = "payment.refunded";

    /// <summary>Every event type, for topology declaration at startup.</summary>
    public static readonly string[] All =
    [
        PaymentAuthorized,
        PaymentCaptured,
        PaymentFailed,
        PaymentVoided,
        PaymentExpired,
        PaymentRefunded,
    ];
}

/// <summary>
/// Fields present on every integration event.
/// </summary>
/// <remarks>
/// <paramref name="EventId"/> is the consumer's deduplication key. The dispatcher
/// guarantees at-least-once delivery, so a consumer that has already recorded this id
/// skips the message rather than applying its effects twice.
/// </remarks>
public abstract record IntegrationEvent(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt);

/// <summary>Funds were reserved on the payer's account.</summary>
public sealed record PaymentAuthorizedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    long AmountMinorUnits,
    string Currency,
    string Method,
    DateTimeOffset AuthorizationExpiresAt)
    : IntegrationEvent(EventId, EventTypes.PaymentAuthorized, OccurredAt);

/// <summary>Funds moved and the ledger lines were written.</summary>
public sealed record PaymentCapturedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    long AmountMinorUnits,
    long FeeMinorUnits,
    long NetMinorUnits,
    string Currency)
    : IntegrationEvent(EventId, EventTypes.PaymentCaptured, OccurredAt);

/// <summary>The payment was rejected; no funds moved.</summary>
public sealed record PaymentFailedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    long AmountMinorUnits,
    string Currency,
    string Reason)
    : IntegrationEvent(EventId, EventTypes.PaymentFailed, OccurredAt);

/// <summary>An authorization was released before capture.</summary>
public sealed record PaymentVoidedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    long AmountMinorUnits,
    string Currency)
    : IntegrationEvent(EventId, EventTypes.PaymentVoided, OccurredAt);

/// <summary>An authorization aged out without being captured.</summary>
public sealed record PaymentExpiredEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    long AmountMinorUnits,
    string Currency)
    : IntegrationEvent(EventId, EventTypes.PaymentExpired, OccurredAt);

/// <summary>Some or all of a captured payment was returned to the payer.</summary>
public sealed record PaymentRefundedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid PaymentId,
    Guid RefundId,
    long RefundAmountMinorUnits,
    long TotalRefundedMinorUnits,
    long PaymentAmountMinorUnits,
    string Currency,
    string ResultingStatus)
    : IntegrationEvent(EventId, EventTypes.PaymentRefunded, OccurredAt);
