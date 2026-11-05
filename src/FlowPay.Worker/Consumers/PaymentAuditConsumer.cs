using System.Text.Json;
using FlowPay.Infrastructure.Messaging;
using FlowPay.Infrastructure.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Worker.Consumers;

/// <summary>
/// Appends every payment event to the audit log.
/// </summary>
/// <remarks>
/// Bound to <c>payment.#</c>, so it records the whole lifecycle including the failures and
/// voids the settlement projection ignores. Two consumers on the same events, each with
/// its own inbox record, is exactly what the per-consumer inbox key is for.
/// </remarks>
public sealed class PaymentAuditConsumer : RabbitMqConsumer
{
    public PaymentAuditConsumer(
        RabbitMqConnectionProvider connections,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger<PaymentAuditConsumer> logger)
        : base(connections, scopeFactory, options, logger)
    {
    }

    protected override string QueueName => RabbitMqTopology.AuditQueue;

    protected override string ConsumerName => "payment-audit";

    protected override async Task HandleAsync(
        ConsumedMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        PaymentAuditStore audit = scopedServices.GetRequiredService<PaymentAuditStore>();

        // The audit log is schema-agnostic by design: it stores the raw payload and only
        // reads the two fields it needs to index by. A new event type is recorded
        // correctly without this consumer being redeployed.
        using JsonDocument document = JsonDocument.Parse(message.Payload);

        Guid paymentId = document.RootElement.TryGetProperty("paymentId", out JsonElement element)
            && element.TryGetGuid(out Guid parsed)
                ? parsed
                : Guid.Empty;

        DateTimeOffset occurredAt =
            document.RootElement.TryGetProperty("occurredAt", out JsonElement occurred)
            && occurred.TryGetDateTimeOffset(out DateTimeOffset parsedOccurred)
                ? parsedOccurred
                : DateTimeOffset.UtcNow;

        await audit
            .AppendAsync(
                message.EventId,
                paymentId,
                message.EventType,
                occurredAt,
                message.Payload,
                cancellationToken)
            .ConfigureAwait(false);

        Logger.LogDebug(
            "Audited {EventType} for payment {PaymentId}.", message.EventType, paymentId);
    }
}
