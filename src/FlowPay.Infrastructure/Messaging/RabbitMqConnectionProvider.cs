using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FlowPay.Infrastructure.Messaging;

/// <summary>
/// Owns the process's single RabbitMQ connection and hands out channels.
/// </summary>
/// <remarks>
/// <para>
/// One TCP connection per process, many channels on it: that is the shape RabbitMQ is
/// designed for. A connection per publish would make the TLS and AMQP handshake part of
/// the request path, and the broker's connection table would grow with traffic rather
/// than with deployment size.
/// </para>
/// <para>
/// The connection is created on first use rather than at startup, so a broker that is
/// slow to come up does not stop the API from serving reads or accepting payments — the
/// outbox absorbs the events until the dispatcher can reach the broker. Automatic
/// recovery is left enabled so a broker restart heals without a deploy.
/// </para>
/// </remarks>
public sealed class RabbitMqConnectionProvider : IAsyncDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnectionProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;

    public RabbitMqConnectionProvider(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnectionProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Returns the shared connection, opening it if necessary.</summary>
    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-checked under the lock: several callers can arrive while the first one
            // is still connecting, and only one connection should result.
            if (_connection is { IsOpen: true } existing)
            {
                return existing;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            ConnectionFactory factory = new()
            {
                HostName = _options.Host,
                Port = _options.Port,
                VirtualHost = _options.VirtualHost,
                UserName = _options.UserName,
                Password = _options.Password,
                ClientProvidedName = _options.ClientProvidedName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval =
                    TimeSpan.FromSeconds(_options.NetworkRecoveryIntervalSeconds),
            };

            _connection = await factory
                .CreateConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Connected to RabbitMQ at {Host}:{Port} as {ClientName}.",
                _options.Host, _options.Port, _options.ClientProvidedName);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens a channel with publisher confirmations enabled.
    /// </summary>
    /// <remarks>
    /// Confirmations are what let the dispatcher know a message is actually safe on the
    /// broker. Without them, <c>BasicPublish</c> returns as soon as the bytes are handed
    /// to the socket, and the dispatcher would mark the outbox row dispatched for a
    /// message the broker never persisted — losing the event and the only record that it
    /// was pending.
    /// </remarks>
    public async Task<IChannel> CreatePublishChannelAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens a channel for consuming, with the configured prefetch window.</summary>
    public async Task<IChannel> CreateConsumerChannelAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);

        IChannel channel = await connection
            .CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _gate.Dispose();
    }
}
