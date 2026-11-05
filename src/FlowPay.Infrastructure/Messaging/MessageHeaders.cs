namespace FlowPay.Infrastructure.Messaging;

/// <summary>The AMQP headers FlowPay sets on every message.</summary>
public static class MessageHeaders
{
    /// <summary>How many handler attempts this message has already had.</summary>
    /// <remarks>
    /// Carried in the message rather than tracked by the consumer, because a staged retry
    /// means the next attempt may well be handled by a different replica. The broker's own
    /// <c>x-death</c> header counts dead-letter hops, which is not the same thing once
    /// messages are republished explicitly.
    /// </remarks>
    public const string Attempt = "x-flowpay-attempt";

    /// <summary>The trace id of the API request that produced the event.</summary>
    public const string TraceId = "x-flowpay-trace-id";

    /// <summary>The event's id, used as the consumer-side deduplication key.</summary>
    public const string EventId = "x-flowpay-event-id";

    /// <summary>Why the message was dead-lettered.</summary>
    public const string FailureReason = "x-flowpay-failure-reason";

    /// <summary>Which queue the message was being handled from when it failed.</summary>
    public const string OriginQueue = "x-flowpay-origin-queue";
}
