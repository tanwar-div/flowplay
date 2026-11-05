using FlowPay.Application.Abstractions;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowPay.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class PaymentRepository : IPaymentRepository
{
    private readonly FlowPayDbContext _context;

    public PaymentRepository(FlowPayDbContext context) => _context = context;

    public Task<PaymentIntent?> FindAsync(
        PaymentId id,
        CancellationToken cancellationToken = default)
        => _context.Payments
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<PaymentIntent?> LockForUpdateAsync(
        PaymentId id,
        CancellationToken cancellationToken = default)
        // The refund path's correctness rests on this lock. Without it, two concurrent
        // refunds read the same refunded total, each computes a permissible new total,
        // and both write — leaving the payment refunded beyond what was captured. With
        // it, the second refund waits, reads the first one's total, and is rejected.
        //
        // xmin is projected explicitly because it is a PostgreSQL system column that
        // `SELECT *` omits, and EF wraps this query as a subquery before selecting the
        // mapped columns — including the concurrency token — from it.
        => _context.Payments
            .FromSql($"SELECT *, xmin FROM payments WHERE id = {id.Value} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(PaymentIntent payment) => _context.Payments.Add(payment);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentId>> FindExpiredAuthorizationsAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default)
    {
        List<Guid> ids = await _context.Payments
            .AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Authorized
                && p.AuthorizationExpiresAt != null
                && p.AuthorizationExpiresAt <= asOf)
            .OrderBy(p => p.AuthorizationExpiresAt)
            .Take(limit)
            // Ids only. The sweep re-locks and re-validates each payment individually,
            // so loading whole aggregates here would be work thrown away — and a status
            // read without a lock is stale by the time it is acted on anyway.
            .Select(p => p.Id.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        PaymentId[] result = new PaymentId[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            result[i] = new PaymentId(ids[i]);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentId>> FindOverRefundedAsync(
        CancellationToken cancellationToken = default)
    {
        // Should never return a row: the aggregate rejects over-refunds, the row lock
        // serializes concurrent ones, and a CHECK constraint makes the row unstorable.
        // The query exists so the stress tests can assert that rather than assume it.
        const string Sql =
            """
            SELECT id
            FROM payments
            WHERE refunded_minor_units > amount_minor_units
            """;

        List<PaymentId> overRefunded = [];

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            overRefunded.Add(new PaymentId(reader.GetGuid(0)));
        }

        return overRefunded;
    }
}
