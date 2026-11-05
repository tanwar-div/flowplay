using FlowPay.Application.Abstractions;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.EntityFrameworkCore;

namespace FlowPay.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class RefundRepository : IRefundRepository
{
    private readonly FlowPayDbContext _context;

    public RefundRepository(FlowPayDbContext context) => _context = context;

    public void Add(Refund refund) => _context.Refunds.Add(refund);

    public Task<Refund?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default)
        => _context.Refunds
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<Refund>> ListForPaymentAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default)
        => await _context.Refunds
            .AsNoTracking()
            .Where(r => r.PaymentId == paymentId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
