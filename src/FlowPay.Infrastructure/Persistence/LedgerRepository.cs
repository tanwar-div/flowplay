using FlowPay.Application.Abstractions;
using FlowPay.Domain.Ledger;
using FlowPay.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowPay.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class LedgerRepository : ILedgerRepository
{
    private readonly FlowPayDbContext _context;

    public LedgerRepository(FlowPayDbContext context) => _context = context;

    public void AddRange(ReadOnlySpan<LedgerEntry> entries)
    {
        // Added one at a time from the span rather than via AddRange(IEnumerable), which
        // would require materializing an array the caller deliberately kept on the stack.
        for (int i = 0; i < entries.Length; i++)
        {
            _context.LedgerEntries.Add(entries[i]);
        }
    }

    public async Task<IReadOnlyList<LedgerEntry>> ListForPaymentAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default)
        => await _context.LedgerEntries
            .AsNoTracking()
            .Where(e => e.PaymentId == paymentId)
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.Direction)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<long> SumSignedMinorUnitsAsync(CancellationToken cancellationToken = default)
    {
        // Summed in PostgreSQL over the stored generated column. At a hundred thousand
        // transactions the ledger holds hundreds of thousands of rows; streaming them
        // into the process to add them up would make the correctness check slower than
        // the workload that produced it.
        const string Sql = "SELECT COALESCE(SUM(signed_minor_units), 0) FROM ledger_entries";

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is long value ? value : Convert.ToInt64(result);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentImbalance>> FindUnbalancedPaymentsAsync(
        CancellationToken cancellationToken = default)
    {
        // Grouping by payment localizes an imbalance to the operations that caused it.
        // A whole-ledger total of zero can still hide two offsetting bugs; a per-payment
        // check cannot.
        const string Sql =
            """
            SELECT payment_id, SUM(signed_minor_units) AS net_minor_units
            FROM ledger_entries
            GROUP BY payment_id
            HAVING SUM(signed_minor_units) <> 0
            """;

        List<PaymentImbalance> imbalances = [];

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            imbalances.Add(new PaymentImbalance(
                new PaymentId(reader.GetGuid(0)),
                reader.GetInt64(1)));
        }

        return imbalances;
    }
}
