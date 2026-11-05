using System.Text.Json;
using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Application.Payments;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using FlowPay.Infrastructure.Messaging;
using FlowPay.Infrastructure.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Worker.Consumers;

/// <summary>
/// Maintains the per-account settlement totals from capture and refund events.
/// </summary>
/// <remarks>
/// The projection's updates accumulate, so applying one event twice is observable as a
/// wrong total rather than silently absorbed. That is intentional: it makes this consumer
/// the component where a deduplication failure would actually surface.
/// </remarks>
public sealed class SettlementProjectionConsumer : RabbitMqConsumer
{
    public SettlementProjectionConsumer(
        RabbitMqConnectionProvider connections,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<SettlementProjectionConsumer> logger)
        : base(connections, scopeFactory, options, logger)
    {
    }

    protected override string QueueName => RabbitMqTopology.SettlementQueue;

    protected override string ConsumerName => "settlement-projection";

    protected override async Task HandleAsync(
        ConsumedMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        SettlementProjectionStore projections =
            scopedServices.GetRequiredService<SettlementProjectionStore>();

        switch (message.EventType)
        {
            case EventTypes.PaymentCaptured:
            {
                PaymentCapturedEvent? captured = JsonSerializer.Deserialize(
                    message.Payload, FlowPayJsonContext.Default.PaymentCapturedEvent);

                if (captured is null)
                {
                    // A payload that will not deserialize will not deserialize on a retry
                    // either. Throwing would burn the retry budget and then dead-letter
                    // it; logging and returning sends it straight to the inbox as handled,
                    // where the audit log still holds the original bytes for inspection.
                    Logger.LogError(
                        "Capture event {EventId} could not be deserialized; skipping.",
                        message.EventId);
                    return;
                }

                await projections
                    .ApplyCaptureAsync(captured, cancellationToken)
                    .ConfigureAwait(false);

                break;
            }

            case EventTypes.PaymentRefunded:
            {
                PaymentRefundedEvent? refunded = JsonSerializer.Deserialize(
                    message.Payload, FlowPayJsonContext.Default.PaymentRefundedEvent);

                if (refunded is null)
                {
                    Logger.LogError(
                        "Refund event {EventId} could not be deserialized; skipping.",
                        message.EventId);
                    return;
                }

                // The refund event carries the payment id but not the payee, so the
                // payment is read to find it. Including the payee in the event would save
                // the lookup, but the event is a record of what happened rather than a
                // convenience payload for one consumer.
                IPaymentRepository payments =
                    scopedServices.GetRequiredService<IPaymentRepository>();

                PaymentIntent? payment = await payments
                    .FindAsync(new PaymentId(refunded.PaymentId), cancellationToken)
                    .ConfigureAwait(false);

                if (payment is null)
                {
                    Logger.LogWarning(
                        "Refund event {EventId} references unknown payment {PaymentId}; skipping.",
                        message.EventId, refunded.PaymentId);
                    return;
                }

                await projections
                    .ApplyRefundAsync(
                        refunded, payment.DestinationAccountId.Value, cancellationToken)
                    .ConfigureAwait(false);

                break;
            }

            default:
                // The queue's bindings should make this unreachable; logging it surfaces
                // a topology change that was made without updating the consumer.
                Logger.LogWarning(
                    "Settlement projection received unexpected event type {EventType}.",
                    message.EventType);
                break;
        }
    }
}
