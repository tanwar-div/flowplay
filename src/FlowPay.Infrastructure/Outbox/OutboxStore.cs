using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowPay.Infrastructure.Outbox;

/// <inheritdoc />
public sealed class OutboxStore : IOutboxStore
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;

    public OutboxStore(FlowPayDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // FOR UPDATE SKIP LOCKED is what makes the dispatcher horizontally scalable.
        // Several worker instances poll this same table concurrently; each one locks the
        // rows it takes and skips rows another instance already holds, so every instance
        // gets a disjoint batch. No leader election, no partition assignment, no row
        // published twice — and adding a worker just adds throughput.
        //
        // Ordered by availability then occurrence so that retried rows do not starve
        // behind a growing backlog of fresh ones, and so events for one aggregate keep
        // their relative order.
        FormattableString sql =
            $"""
            SELECT *
            FROM outbox_messages
            WHERE status = 0 AND available_at <= {_clock.UtcNow}
            ORDER BY available_at, occurred_at
            LIMIT {batchSize}
            FOR UPDATE SKIP LOCKED
            """;

        return await _context.OutboxMessages
            .FromSql(sql)
            .AsTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OutboxDepth> GetDepthAsync(CancellationToken cancellationToken = default)
    {
        const string Sql =
            """
            SELECT
                COUNT(*) FILTER (WHERE status = 0) AS pending,
                COUNT(*) FILTER (WHERE status = 2) AS failed,
                COALESCE(
                    EXTRACT(EPOCH FROM (now() - MIN(occurred_at) FILTER (WHERE status = 0))),
                    0
                ) AS oldest_pending_age_seconds
            FROM outbox_messages
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new OutboxDepth(0L, 0L, 0L);
        }

        return new OutboxDepth(
            reader.GetInt64(0),
            reader.GetInt64(1),
            (long)reader.GetDouble(2));
    }

    /// <inheritdoc />
    public async Task<int> RequeueFailedAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Operator-driven replay after a downstream outage has been fixed. Resetting
        // attempts means the rows get the full retry budget again rather than being
        // parked on their first new failure.
        const string Sql =
            """
            UPDATE outbox_messages
            SET status       = 0,
                attempts     = 0,
                available_at = @now,
                last_error   = NULL
            WHERE id IN (
                SELECT id FROM outbox_messages
                WHERE status = 2
                ORDER BY occurred_at
                LIMIT @batch_size
            )
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("now", _clock.UtcNow);
        command.Parameters.AddWithValue("batch_size", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
