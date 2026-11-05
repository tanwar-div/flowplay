namespace FlowPay.Application.Outbox;

/// <summary>Dispatch state of an outbox row.</summary>
public enum OutboxStatus : short
{
    /// <summary>Waiting to be published.</summary>
    Pending = 0,

    /// <summary>Acknowledged by the broker.</summary>
    Dispatched = 1,

    /// <summary>Exhausted its retries; parked for human attention.</summary>
    Failed = 2,
}

/// <summary>
/// An integration event committed alongside the state change that produced it.
/// </summary>
/// <remarks>
/// <para>
/// This row is the reason FlowPay can promise that a captured payment always produces a
/// settlement event. Publishing to RabbitMQ from inside the request would mean choosing
/// which half to lose when the broker is unreachable: either the capture rolls back
/// because the broker is down, or the capture commits and the event vanishes. Writing the
/// event to the same database transaction removes the choice — the event is as durable as
/// the payment — and a separate dispatcher moves it to the broker afterwards.
/// </para>
/// <para>
/// The trade is at-least-once delivery rather than exactly-once: the dispatcher can
/// publish a row and crash before marking it dispatched, so consumers must be idempotent.
/// That is what the consumer-side inbox table provides.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        // Materialization constructor for the persistence layer.
    }

    /// <summary>
    /// A UUIDv7, so rows are stored in roughly the order they were produced.
    /// </summary>
    /// <remarks>
    /// Time-ordered ids matter more here than anywhere else in the schema: the dispatcher
    /// polls this table continuously, and an ordered key keeps the pending rows clustered
    /// at one end of the index instead of scattered through it.
    /// </remarks>
    public Guid Id { get; private set; }

    /// <summary>The event name consumers route on, e.g. <c>payment.captured</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>The aggregate this event concerns, used as the partition key.</summary>
    public Guid AggregateId { get; private set; }

    /// <summary>The serialized event payload.</summary>
    public string Payload { get; private set; } = string.Empty;

    public OutboxStatus Status { get; private set; }

    /// <summary>How many publish attempts have been made.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// Earliest time this row may be attempted again. Backoff lives in the row rather
    /// than in the dispatcher's memory so it survives a restart and applies across every
    /// dispatcher instance.
    /// </summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// The W3C trace id of the request that produced the event, so the asynchronous work
    /// downstream can be correlated with the API call that caused it.
    /// </summary>
    public string? TraceId { get; private set; }

    public static OutboxMessage Create(
        string eventType,
        Guid aggregateId,
        string payload,
        DateTimeOffset now,
        string? traceId) => new()
        {
            Id = Guid.CreateVersion7(),
            EventType = eventType,
            AggregateId = aggregateId,
            Payload = payload,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            AvailableAt = now,
            OccurredAt = now,
            TraceId = traceId,
        };

    public void MarkDispatched(DateTimeOffset now)
    {
        Status = OutboxStatus.Dispatched;
        DispatchedAt = now;
        Attempts++;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one, or parks the row once the
    /// attempt budget is spent.
    /// </summary>
    public void MarkAttemptFailed(
        string error,
        int maxAttempts,
        DateTimeOffset now,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double jitterFactor)
    {
        Attempts++;
        LastError = Truncate(error, 1024);

        if (Attempts >= maxAttempts)
        {
            Status = OutboxStatus.Failed;
            return;
        }

        AvailableAt = now + OutboxBackoff.Compute(Attempts, baseDelay, maxDelay, jitterFactor);
    }

    /// <summary>Returns a parked row to the pending pool, for operator-driven replay.</summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = OutboxStatus.Pending;
        Attempts = 0;
        AvailableAt = now;
        LastError = null;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Backoff schedule for outbox dispatch retries.</summary>
public static class OutboxBackoff
{
    /// <summary>
    /// Exponential backoff with full jitter, capped at <paramref name="maxDelay"/>.
    /// </summary>
    /// <remarks>
    /// The jitter is the important part. Without it, a broker outage leaves every pending
    /// row scheduled for the same instant, and the moment the broker recovers it is hit by
    /// the entire backlog at once — turning a transient failure into a second one. Full
    /// jitter spreads the retries uniformly across the window instead.
    /// </remarks>
    public static TimeSpan Compute(
        int attempt,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        double jitterFactor)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        // Shift rather than Math.Pow, and clamp the exponent so a row with a long failure
        // history cannot overflow the multiplication.
        int exponent = Math.Min(attempt - 1, 16);
        double delayTicks = baseDelay.Ticks * (double)(1L << exponent);

        double cappedTicks = Math.Min(delayTicks, maxDelay.Ticks);

        double jitter = jitterFactor <= 0d
            ? 0d
            : cappedTicks * jitterFactor * Random.Shared.NextDouble();

        return TimeSpan.FromTicks((long)Math.Min(cappedTicks + jitter, maxDelay.Ticks));
    }
}
