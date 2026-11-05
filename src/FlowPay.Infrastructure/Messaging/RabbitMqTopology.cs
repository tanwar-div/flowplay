using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace FlowPay.Infrastructure.Messaging;

/// <summary>
/// The exchange, queue and binding names, and the code that declares them.
/// </summary>
/// <remarks>
/// <para>
/// The retry strategy is staged delay queues rather than an immediate requeue. A failed
/// message is republished to a queue whose only job is to hold it for a fixed TTL, after
/// which the broker dead-letters it straight back to the main queue. Each attempt uses a
/// longer-lived queue, so the delay grows: 5s, then 30s, then 2 minutes.
/// </para>
/// <para>
/// The reason not to simply <c>nack</c> with requeue is that it produces a hot loop. A
/// message failing because a downstream dependency is down would be redelivered
/// immediately, thousands of times a second, saturating the consumer and the broker while
/// accomplishing nothing. Delay queues put the wait in the broker, where it costs nothing
/// and survives a consumer restart.
/// </para>
/// <para>
/// Messages that exhaust every attempt go to a dead-letter queue rather than being
/// dropped. A payment event that cannot be processed is something a person needs to look
/// at, and the DLQ is what makes that possible instead of leaving a silent gap.
/// </para>
/// </remarks>
public static class RabbitMqTopology
{
    /// <summary>Topic exchange that every payment event is published to.</summary>
    public const string EventsExchange = "flowpay.events";

    /// <summary>Terminal failures. Drained by an operator, never automatically.</summary>
    public const string DeadLetterExchange = "flowpay.dlx";

    public const string DeadLetterQueue = "flowpay.dlq";

    /// <summary>Maintains the settlement projection from capture and refund events.</summary>
    public const string SettlementQueue = "flowpay.settlement";

    /// <summary>Writes the immutable audit trail from every payment event.</summary>
    public const string AuditQueue = "flowpay.audit";

    /// <summary>The staged retry delays, in order.</summary>
    public static readonly int[] RetryDelaysSeconds = [5, 30, 120];

    /// <summary>The routing keys the settlement projection cares about.</summary>
    public static readonly string[] SettlementRoutingKeys =
    [
        "payment.captured",
        "payment.refunded",
    ];

    /// <summary>The audit trail records everything.</summary>
    public static readonly string[] AuditRoutingKeys = ["payment.#"];

    /// <summary>The delay queue for a given attempt number (1-based).</summary>
    public static string RetryQueueName(string queue, int attempt)
    {
        int index = Math.Clamp(attempt - 1, 0, RetryDelaysSeconds.Length - 1);
        return $"{queue}.retry.{RetryDelaysSeconds[index]}s";
    }

    /// <summary>Declares every exchange, queue and binding. Idempotent.</summary>
    public static async Task DeclareAsync(
        IChannel channel,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        await channel.ExchangeDeclareAsync(
            EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.ExchangeDeclareAsync(
            DeadLetterExchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueDeclareAsync(
            DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await channel.QueueBindAsync(
            DeadLetterQueue, DeadLetterExchange, "#",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await DeclareConsumerQueueAsync(
            channel, SettlementQueue, SettlementRoutingKeys, cancellationToken)
            .ConfigureAwait(false);

        await DeclareConsumerQueueAsync(
            channel, AuditQueue, AuditRoutingKeys, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Declared RabbitMQ topology: exchanges {Events}, {Dlx}; queues {Settlement}, {Audit}, {Dlq}.",
            EventsExchange, DeadLetterExchange, SettlementQueue, AuditQueue, DeadLetterQueue);
    }

    private static async Task DeclareConsumerQueueAsync(
        IChannel channel,
        string queue,
        string[] routingKeys,
        CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                // A message rejected outright — as opposed to republished for a staged
                // retry — goes straight to the dead-letter exchange.
                ["x-dead-letter-exchange"] = DeadLetterExchange,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (string routingKey in routingKeys)
        {
            await channel.QueueBindAsync(
                queue, EventsExchange, routingKey,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (int delaySeconds in RetryDelaysSeconds)
        {
            string retryQueue = $"{queue}.retry.{delaySeconds}s";

            await channel.QueueDeclareAsync(
                retryQueue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    // Hold for the delay, then let the broker dead-letter the message
                    // back to the queue it came from. Routing via the default exchange
                    // with the queue's own name targets exactly that queue, so a retry
                    // never fans out to the other consumers bound to the events exchange.
                    ["x-message-ttl"] = delaySeconds * 1000,
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = queue,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
