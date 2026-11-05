using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace FlowPay.Infrastructure.Projections;

/// <summary>Per-account settlement totals, as maintained by the consumers.</summary>
public sealed record SettlementSummary(
    Guid AccountId,
    string Currency,
    long CapturedCount,
    long CapturedGrossMinorUnits,
    long CapturedNetMinorUnits,
    long FeeMinorUnits,
    long RefundedCount,
    long RefundedMinorUnits,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Maintains the settlement read model from capture and refund events.
/// </summary>
/// <remarks>
/// The updates are accumulating upserts — <c>captured_count = captured_count + 1</c> —
/// which is deliberate. An idempotent "set to this value" projection would hide a
/// duplicate delivery; an accumulating one cannot. That makes this projection the place
/// where a failure of the inbox deduplication would actually show up, rather than
/// something that silently tolerates it.
/// </remarks>
public sealed class SettlementProjectionStore
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;

    public SettlementProjectionStore(FlowPayDbContext context, IClock clock)
    {
        _context = context;
        _clock = clock;
    }

    /// <summary>Applies a capture to the payer's and payee's summaries.</summary>
    public async Task ApplyCaptureAsync(
        PaymentCapturedEvent captured,
        CancellationToken cancellationToken = default)
    {
        // The payer's gross outflow and the payee's net inflow are recorded on separate
        // rows, so the projection can answer "what did this account send" and "what did
        // it receive" independently.
        await UpsertAsync(
            captured.DestinationAccountId,
            captured.Currency,
            capturedCount: 1L,
            capturedGross: captured.AmountMinorUnits,
            capturedNet: captured.NetMinorUnits,
            fee: captured.FeeMinorUnits,
            refundedCount: 0L,
            refunded: 0L,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies a refund to the payee's summary.</summary>
    public async Task ApplyRefundAsync(
        PaymentRefundedEvent refunded,
        Guid payeeAccountId,
        CancellationToken cancellationToken = default)
        => await UpsertAsync(
            payeeAccountId,
            refunded.Currency,
            capturedCount: 0L,
            capturedGross: 0L,
            capturedNet: 0L,
            fee: 0L,
            refundedCount: 1L,
            refunded: refunded.RefundAmountMinorUnits,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Reads one account's summary.</summary>
    public async Task<SettlementSummary?> FindAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        const string Sql =
            """
            SELECT account_id, currency, captured_count, captured_gross_minor_units,
                   captured_net_minor_units, fee_minor_units, refunded_count,
                   refunded_minor_units, updated_at
            FROM settlement_summaries
            WHERE account_id = @account_id
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("account_id", accountId);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SettlementSummary(
            reader.GetGuid(0),
            reader.GetString(1).Trim(),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    private async Task UpsertAsync(
        Guid accountId,
        string currency,
        long capturedCount,
        long capturedGross,
        long capturedNet,
        long fee,
        long refundedCount,
        long refunded,
        CancellationToken cancellationToken)
    {
        const string Sql =
            """
            INSERT INTO settlement_summaries (
                account_id, currency, captured_count, captured_gross_minor_units,
                captured_net_minor_units, fee_minor_units, refunded_count,
                refunded_minor_units, updated_at
            ) VALUES (
                @account_id, @currency, @captured_count, @captured_gross,
                @captured_net, @fee, @refunded_count, @refunded, @updated_at
            )
            ON CONFLICT (account_id) DO UPDATE SET
                captured_count             = settlement_summaries.captured_count + EXCLUDED.captured_count,
                captured_gross_minor_units = settlement_summaries.captured_gross_minor_units + EXCLUDED.captured_gross_minor_units,
                captured_net_minor_units   = settlement_summaries.captured_net_minor_units + EXCLUDED.captured_net_minor_units,
                fee_minor_units            = settlement_summaries.fee_minor_units + EXCLUDED.fee_minor_units,
                refunded_count             = settlement_summaries.refunded_count + EXCLUDED.refunded_count,
                refunded_minor_units       = settlement_summaries.refunded_minor_units + EXCLUDED.refunded_minor_units,
                updated_at                 = EXCLUDED.updated_at
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("account_id", accountId);
        command.Parameters.Add("currency", NpgsqlDbType.Char).Value = currency;
        command.Parameters.AddWithValue("captured_count", capturedCount);
        command.Parameters.AddWithValue("captured_gross", capturedGross);
        command.Parameters.AddWithValue("captured_net", capturedNet);
        command.Parameters.AddWithValue("fee", fee);
        command.Parameters.AddWithValue("refunded_count", refundedCount);
        command.Parameters.AddWithValue("refunded", refunded);
        command.Parameters.AddWithValue("updated_at", _clock.UtcNow);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
