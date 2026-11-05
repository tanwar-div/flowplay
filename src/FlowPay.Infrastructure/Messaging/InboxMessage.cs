namespace FlowPay.Infrastructure.Messaging;

/// <summary>
/// A record that a consumer has already handled a given event.
/// </summary>
/// <remarks>
/// <para>
/// The outbox gives at-least-once delivery: the dispatcher can publish a message and then
/// crash before marking the row dispatched, so the message is published again. This table
/// is the other half of the contract. A consumer inserts the event id here in the same
/// transaction as its side effects, so a redelivery hits the primary key, the transaction
/// is abandoned, and the effects are applied exactly once.
/// </para>
/// <para>
/// Keyed by event id <em>and</em> consumer name, because several consumers legitimately
/// handle the same event and each needs its own record of having done so.
/// </para>
/// </remarks>
public sealed class InboxMessage
{
    private InboxMessage()
    {
        // Materialization constructor for the persistence layer.
    }

    /// <summary>The event's id, carried in the message envelope.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Which consumer handled it.</summary>
    public string Consumer { get; private set; } = string.Empty;

    public string EventType { get; private set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; private set; }

    /// <summary>How many times the broker delivered this message before it stuck.</summary>
    public int DeliveryAttempt { get; private set; }

    public static InboxMessage Create(
        Guid messageId,
        string consumer,
        string eventType,
        int deliveryAttempt,
        DateTimeOffset now) => new()
        {
            MessageId = messageId,
            Consumer = consumer,
            EventType = eventType,
            DeliveryAttempt = deliveryAttempt,
            ProcessedAt = now,
        };
}
