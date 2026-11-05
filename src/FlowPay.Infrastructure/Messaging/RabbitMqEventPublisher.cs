using System.Text;
using FlowPay.Application.Outbox;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace FlowPay.Infrastructure.Messaging;

/// <inheritdoc />
public sealed class RabbitMqEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqConnectionProvider _connections;
    private readonly ILogger<RabbitMqEventPublisher> _logger;
    private readonly SemaphoreSlim _channelGate = new(1, 1);

    private IChannel? _channel;

    public RabbitMqEventPublisher(
        RabbitMqConnectionProvider connections,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        IChannel channel = await GetChannelAsync(cancellationToken).ConfigureAwait(false);

        BasicProperties properties = new()
        {
            // Persistent, so a broker restart does not discard the message. Publishing
            // transiently would make the outbox pointless: the event would be durable
            // right up to the moment it was handed to a broker that could lose it.
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.Id.ToString("d"),
            Type = message.EventType,
            CorrelationId = message.AggregateId.ToString("d"),
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                [MessageHeaders.EventId] = message.Id.ToString("d"),
                [MessageHeaders.TraceId] = message.TraceId,
                [MessageHeaders.Attempt] = 0,
            },
        };

        byte[] body = Encoding.UTF8.GetBytes(message.Payload);

        // Awaiting the publish on a confirm-tracking channel waits for the broker's ack.
        // A nack or a return surfaces as an exception, which the dispatcher records as a
        // failed attempt and retries with backoff.
        await channel.BasicPublishAsync(
            exchange: RabbitMqTopology.EventsExchange,
            routingKey: message.EventType,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await _channelGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_channel is { IsOpen: true } existing)
            {
                return existing;
            }

            if (_channel is not null)
            {
                _logger.LogWarning("Publish channel was closed; opening a replacement.");
                await _channel.DisposeAsync().ConfigureAwait(false);
                _channel = null;
            }

            _channel = await _connections
                .CreatePublishChannelAsync(cancellationToken)
                .ConfigureAwait(false);

            return _channel;
        }
        finally
        {
            _channelGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
            _channel = null;
        }

        _channelGate.Dispose();
    }
}
