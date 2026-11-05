using FlowPay.Application.Abstractions;
using FlowPay.Infrastructure.Persistence;
using Npgsql;

namespace FlowPay.Infrastructure.Messaging;

/// <inheritdoc />
public sealed class PostgresInboxStore : IInboxStore
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;

    public PostgresInboxStore(FlowPayDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginAsync(
        Guid messageId,
        string consumer,
        string eventType,
        int deliveryAttempt,
        CancellationToken cancellationToken = default)
    {
        // The same claim pattern the idempotency store uses, for the same reason: the
        // primary key makes it atomic, and a competing transaction holding the same key
        // blocks here until it resolves rather than both proceeding.
        const string Sql =
            """
            INSERT INTO inbox_messages
                (message_id, consumer, event_type, processed_at, delivery_attempt)
            VALUES
                (@message_id, @consumer, @event_type, @processed_at, @delivery_attempt)
            ON CONFLICT (message_id, consumer) DO NOTHING
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("consumer", consumer);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("processed_at", _clock.UtcNow);
        command.Parameters.AddWithValue("delivery_attempt", deliveryAttempt);

        int inserted = await command
            .ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return inserted == 1;
    }

    /// <inheritdoc />
    public async Task<int> PurgeOlderThanAsync(
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Retention has to outlast the longest possible redelivery window — the full
        // staged-retry schedule plus any broker backlog — or a late redelivery would find
        // no record and apply the message a second time.
        const string Sql =
            """
            DELETE FROM inbox_messages
            WHERE (message_id, consumer) IN (
                SELECT message_id, consumer
                FROM inbox_messages
                WHERE processed_at < @cutoff
                ORDER BY processed_at
                LIMIT @batch_size
            )
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("cutoff", _clock.UtcNow - retention);
        command.Parameters.AddWithValue("batch_size", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
