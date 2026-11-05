using System.Diagnostics;
using System.Text;
using FlowPay.Application.Abstractions;
using FlowPay.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FlowPay.Worker.Consumers;

/// <summary>A received message and the metadata the base class extracted from it.</summary>
public readonly record struct ConsumedMessage(
    Guid EventId,
    string EventType,
    string Payload,
    int Attempt,
    string? TraceId);

/// <summary>
/// The shared consume loop: deduplicate, handle transactionally, acknowledge, and on
/// failure schedule a delayed retry or dead-letter the message.
/// </summary>
/// <remarks>
/// <para>
/// The ordering inside <see cref="HandleDeliveryAsync"/> is the whole safety argument:
/// </para>
/// <list type="number">
/// <item><description>open a transaction;</description></item>
/// <item><description>claim the event in the inbox — if it was already handled, commit nothing
/// and acknowledge;</description></item>
/// <item><description>run the handler's work in that same transaction;</description></item>
/// <item><description>commit, then acknowledge the broker.</description></item>
/// </list>
/// <para>
/// Acknowledging only after the commit means a crash in between redelivers the message,
/// and the inbox claim rolled back with everything else, so the retry is a genuine first
/// attempt. Acknowledging first would turn any crash into silently lost work.
/// </para>
/// <para>
/// A handler failure is never requeued immediately. The message is republished to a delay
/// queue with its attempt count incremented, then the original is acknowledged — so a
/// failing message waits in the broker instead of spinning through the consumer
/// thousands of times a second while a dependency is down.
/// </para>
/// </remarks>
public abstract class RabbitMqConsumer : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("FlowPay.Consumers");

    private readonly RabbitMqConnectionProvider _connections;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;

    private IChannel? _channel;

    protected RabbitMqConsumer(
        RabbitMqConnectionProvider connections,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options,
        ILogger logger)
    {
        _connections = connections;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>The queue this consumer reads from.</summary>
    protected abstract string QueueName { get; }

    /// <summary>
    /// This consumer's name, used as part of the inbox key.
    /// </summary>
    /// <remarks>
    /// Must be stable across deployments. Changing it makes every previously handled
    /// event look unhandled, and the next redelivery of any of them would be applied a
    /// second time.
    /// </remarks>
    protected abstract string ConsumerName { get; }

    /// <summary>
    /// Handles one message. Runs inside the transaction that also holds the inbox claim,
    /// so it must not commit or roll back.
    /// </summary>
    protected abstract Task HandleAsync(
        ConsumedMessage message,
        IServiceProvider scopedServices,
        CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConsumerAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                // The broker is unreachable or the channel died. Reconnecting is the
                // correct response: the messages are durable and still queued, so the
                // only thing lost is the time spent waiting.
                Logger.LogError(
                    failure,
                    "Consumer {Consumer} on {Queue} stopped; reconnecting in {Delay}s.",
                    ConsumerName, QueueName, _options.NetworkRecoveryIntervalSeconds);

                await Task
                    .Delay(
                        TimeSpan.FromSeconds(_options.NetworkRecoveryIntervalSeconds),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RunConsumerAsync(CancellationToken stoppingToken)
    {
        _channel = await _connections
            .CreateConsumerChannelAsync(stoppingToken)
            .ConfigureAwait(false);

        // Declared by the consumer as well as at startup. A consumer that reconnects to a
        // broker which has been rebuilt must not sit silently against a queue that no
        // longer exists.
        await RabbitMqTopology
            .DeclareAsync(_channel, Logger, stoppingToken)
            .ConfigureAwait(false);

        AsyncEventingBasicConsumer consumer = new(_channel);
        consumer.ReceivedAsync += (_, delivery) => HandleDeliveryAsync(delivery, stoppingToken);

        await _channel.BasicConsumeAsync(
            queue: QueueName,
            // Manual acknowledgement. With autoAck the broker considers a message
            // delivered the moment it hits the socket, so a consumer crash would lose it
            // outright.
            autoAck: false,
            consumerTag: $"{ConsumerName}-{Environment.MachineName}",
            consumer: consumer,
            cancellationToken: stoppingToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Consumer {Consumer} is listening on {Queue} with prefetch {Prefetch}.",
            ConsumerName, QueueName, _options.PrefetchCount);

        // Hold the channel open until shutdown; the broker pushes work to the handler.
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleDeliveryAsync(
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        IChannel? channel = _channel;

        if (channel is null)
        {
            return;
        }

        ConsumedMessage message = Extract(delivery);

        using Activity? span = ActivitySource.StartActivity(
            $"{ConsumerName} process", ActivityKind.Consumer);

        span?.SetTag("messaging.message.id", message.EventId);
        span?.SetTag("messaging.destination.name", QueueName);
        span?.SetTag("flowpay.event.type", message.EventType);
        span?.SetTag("flowpay.delivery.attempt", message.Attempt);

        try
        {
            bool handled = await ProcessAsync(message, stoppingToken).ConfigureAwait(false);

            if (!handled)
            {
                Logger.LogDebug(
                    "Consumer {Consumer} skipped event {EventId}: already processed.",
                    ConsumerName, message.EventId);
            }

            await channel
                .BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            span?.SetStatus(ActivityStatusCode.Error, failure.Message);

            await ScheduleRetryOrDeadLetterAsync(channel, delivery, message, failure, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims the event and runs the handler in one transaction.
    /// </summary>
    /// <returns>False when the event had already been handled.</returns>
    private async Task<bool> ProcessAsync(ConsumedMessage message, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IInboxStore inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            bool first = await inbox
                .TryBeginAsync(
                    message.EventId,
                    ConsumerName,
                    message.EventType,
                    message.Attempt + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!first)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            await HandleAsync(message, scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);

            // The claim and the effects commit together. Either both are durable or
            // neither is, which is what makes the redelivery safe.
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ScheduleRetryOrDeadLetterAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        ConsumedMessage message,
        Exception failure,
        CancellationToken cancellationToken)
    {
        int nextAttempt = message.Attempt + 1;

        try
        {
            if (nextAttempt < _options.MaxDeliveryAttempts)
            {
                string retryQueue = RabbitMqTopology.RetryQueueName(QueueName, nextAttempt);

                Logger.LogWarning(
                    failure,
                    "Consumer {Consumer} failed event {EventId} on attempt {Attempt}; retrying via {RetryQueue}.",
                    ConsumerName, message.EventId, nextAttempt, retryQueue);

                // Published to the delay queue through the default exchange, whose
                // routing key is the queue name. The delay queue holds the message for
                // its TTL and then dead-letters it back to this queue.
                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: retryQueue,
                    mandatory: false,
                    basicProperties: BuildForwardProperties(delivery, nextAttempt, failure),
                    body: delivery.Body.ToArray(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Logger.LogError(
                    failure,
                    "Consumer {Consumer} exhausted {Attempts} attempts on event {EventId}; dead-lettering.",
                    ConsumerName, nextAttempt, message.EventId);

                await channel.BasicPublishAsync(
                    exchange: RabbitMqTopology.DeadLetterExchange,
                    routingKey: message.EventType,
                    mandatory: false,
                    basicProperties: BuildForwardProperties(delivery, nextAttempt, failure),
                    body: delivery.Body.ToArray(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // Acknowledged only after the copy is safely republished, so the message
            // cannot be dropped between the two steps.
            await channel
                .BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception forwardFailure)
        {
            Logger.LogError(
                forwardFailure,
                "Could not reschedule event {EventId}; returning it to {Queue} for redelivery.",
                message.EventId, QueueName);

            // The republish failed, so nacking with requeue is the only way not to lose
            // the message. It risks a tight redelivery loop, which is why it is the
            // fallback rather than the normal path.
            await channel
                .BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private BasicProperties BuildForwardProperties(
        BasicDeliverEventArgs delivery,
        int attempt,
        Exception failure)
    {
        Dictionary<string, object?> headers = new(StringComparer.Ordinal)
        {
            [MessageHeaders.Attempt] = attempt,
            [MessageHeaders.EventId] = delivery.BasicProperties.MessageId,
            [MessageHeaders.OriginQueue] = QueueName,
            [MessageHeaders.FailureReason] = Truncate(failure.Message, 512),
        };

        if (delivery.BasicProperties.Headers?.TryGetValue(
            MessageHeaders.TraceId, out object? traceId) == true)
        {
            headers[MessageHeaders.TraceId] = traceId;
        }

        return new BasicProperties
        {
            Persistent = true,
            ContentType = delivery.BasicProperties.ContentType ?? "application/json",
            MessageId = delivery.BasicProperties.MessageId,
            Type = delivery.BasicProperties.Type,
            CorrelationId = delivery.BasicProperties.CorrelationId,
            Headers = headers,
        };
    }

    private static ConsumedMessage Extract(BasicDeliverEventArgs delivery)
    {
        IReadOnlyBasicProperties properties = delivery.BasicProperties;

        // The event id comes from the header first and the AMQP MessageId second. Both
        // are set by the publisher; the header survives the republish hops that staged
        // retries introduce, which is why it is preferred.
        Guid eventId = ReadGuidHeader(properties, MessageHeaders.EventId)
            ?? (Guid.TryParse(properties.MessageId, out Guid fromMessageId)
                ? fromMessageId
                : Guid.Empty);

        int attempt = ReadIntHeader(properties, MessageHeaders.Attempt) ?? 0;

        string eventType = properties.Type
            ?? delivery.RoutingKey
            ?? "unknown";

        return new ConsumedMessage(
            eventId,
            eventType,
            Encoding.UTF8.GetString(delivery.Body.Span),
            attempt,
            ReadStringHeader(properties, MessageHeaders.TraceId));
    }

    private static Guid? ReadGuidHeader(IReadOnlyBasicProperties properties, string name)
    {
        string? value = ReadStringHeader(properties, name);
        return Guid.TryParse(value, out Guid parsed) ? parsed : null;
    }

    private static int? ReadIntHeader(IReadOnlyBasicProperties properties, string name)
    {
        if (properties.Headers?.TryGetValue(name, out object? raw) != true)
        {
            return null;
        }

        return raw switch
        {
            int value => value,
            long value => (int)value,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out int parsed) => parsed,
            string text when int.TryParse(text, out int parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadStringHeader(IReadOnlyBasicProperties properties, string name)
    {
        if (properties.Headers?.TryGetValue(name, out object? raw) != true)
        {
            return null;
        }

        // RabbitMQ's client surfaces AMQP long strings as byte arrays, so a header that
        // was written as a string reads back as bytes.
        return raw switch
        {
            string text => text,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => raw?.ToString(),
        };
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }
    }
}
