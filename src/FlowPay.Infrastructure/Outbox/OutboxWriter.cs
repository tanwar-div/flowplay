using System.Diagnostics;
using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Infrastructure.Persistence;

namespace FlowPay.Infrastructure.Outbox;

/// <inheritdoc />
public sealed class OutboxWriter : IOutboxWriter
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;

    public OutboxWriter(FlowPayDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <inheritdoc />
    public void Enqueue(string eventType, Guid aggregateId, string payload)
    {
        // Added to the change tracker, not written. It reaches the database only when the
        // caller's transaction commits, which is precisely the guarantee the outbox
        // exists to provide: the event and the state change it describes are the same
        // atomic fact.
        OutboxMessage message = OutboxMessage.Create(
            eventType,
            aggregateId,
            payload,
            _clock.UtcNow,
            // The request's trace id, so the asynchronous work this event triggers can be
            // correlated back to the API call that caused it. Spans are not linked
            // automatically across a durable queue, so the id has to be carried.
            Activity.Current?.TraceId.ToString());

        _context.OutboxMessages.Add(message);
    }
}
