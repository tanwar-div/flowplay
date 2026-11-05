using FlowPay.Application.Abstractions;
using FlowPay.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FlowPay.Infrastructure.Projections;

/// <summary>
/// Appends every payment event to an immutable audit log.
/// </summary>
/// <remarks>
/// The log keeps the raw event payload rather than a parsed projection of it. Audit value
/// comes from recording exactly what was published, including fields a future version of
/// the consumer would not know how to interpret; a normalized schema would quietly
/// discard them.
/// </remarks>
public sealed class PaymentAuditStore
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;

    public PaymentAuditStore(FlowPayDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <summary>Appends an event. Silently ignores an event already recorded.</summary>
    public async Task AppendAsync(
        Guid eventId,
        Guid paymentId,
        string eventType,
        DateTimeOffset occurredAt,
        string payload,
        CancellationToken cancellationToken = default)
    {
        const string Sql =
            """
            INSERT INTO payment_audit_log
                (event_id, payment_id, event_type, occurred_at, recorded_at, payload)
            VALUES
                (@event_id, @payment_id, @event_type, @occurred_at, @recorded_at, @payload)
            ON CONFLICT (event_id) DO NOTHING
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("payment_id", paymentId);
        command.Parameters.AddWithValue("event_type", eventType);
        command.Parameters.AddWithValue("occurred_at", occurredAt);
        command.Parameters.AddWithValue("recorded_at", _clock.UtcNow);
        command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts the log entries for a payment. Used by the tests.</summary>
    public async Task<int> CountForPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        const string Sql =
            "SELECT COUNT(*) FROM payment_audit_log WHERE payment_id = @payment_id";

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("payment_id", paymentId);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result);
    }
}
