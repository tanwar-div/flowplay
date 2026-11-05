using FlowPay.Application.Outbox;

namespace FlowPay.Infrastructure.Messaging;

/// <summary>Publishes an outbox row to the broker.</summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publishes <paramref name="message"/> and returns only once the broker has
    /// confirmed it is persisted.
    /// </summary>
    /// <remarks>
    /// Waiting for the confirmation is the contract. The dispatcher marks the outbox row
    /// dispatched based on this call returning, so a fire-and-forget publish would let it
    /// discard the only durable record of an event the broker never received.
    /// </remarks>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
