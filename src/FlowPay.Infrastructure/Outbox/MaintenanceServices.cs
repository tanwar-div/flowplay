using FlowPay.Application.Abstractions;
using FlowPay.Application.Idempotency;
using FlowPay.Application.Outbox;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Infrastructure.Outbox;

/// <summary>
/// Expires authorizations that were never captured, releasing their reserved funds.
/// </summary>
/// <remarks>
/// <para>
/// Without this sweep, an abandoned authorization reserves the payer's funds forever:
/// the money is neither spent nor available, and the account slowly becomes unusable.
/// </para>
/// <para>
/// The sweep finds candidate ids without locking, then locks and revalidates each one
/// individually before expiring it. A payment can be captured between the scan and the
/// lock, and expiring a captured payment would release funds that have already settled —
/// so the decision to expire is made only against the locked row, never against the scan's
/// result.
/// </para>
/// </remarks>
public sealed class AuthorizationExpirySweep : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowPayOptions _options;
    private readonly ILogger<AuthorizationExpirySweep> _logger;

    public AuthorizationExpirySweep(
        IServiceScopeFactory scopeFactory,
        IOptions<FlowPayOptions> options,
        ILogger<AuthorizationExpirySweep> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_options.Maintenance.AuthorizationExpirySweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                int expired = await SweepAsync(stoppingToken).ConfigureAwait(false);

                if (expired > 0)
                {
                    _logger.LogInformation("Expired {Count} stale authorizations.", expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Authorization expiry sweep failed.");
            }
        }
    }

    private async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IPaymentRepository payments = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        IClock clock = scope.ServiceProvider.GetRequiredService<IClock>();

        IReadOnlyList<PaymentId> candidates = await payments
            .FindExpiredAuthorizationsAsync(
                clock.UtcNow,
                _options.Maintenance.AuthorizationExpiryBatchSize,
                cancellationToken)
            .ConfigureAwait(false);

        int expired = 0;

        foreach (PaymentId paymentId in candidates)
        {
            // One transaction per payment. A single transaction over the whole batch
            // would hold locks on hundreds of payments and their payers' accounts,
            // blocking live traffic for those accounts; and one payment that cannot be
            // expired would roll back the rest of the batch with it.
            if (await ExpireOneAsync(paymentId, cancellationToken).ConfigureAwait(false))
            {
                expired++;
            }
        }

        return expired;
    }

    private async Task<bool> ExpireOneAsync(PaymentId paymentId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IPaymentRepository payments = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
        IAccountRepository accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        IOutboxWriter outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        IClock clock = scope.ServiceProvider.GetRequiredService<IClock>();

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            PaymentIntent? payment = await payments
                .LockForUpdateAsync(paymentId, cancellationToken)
                .ConfigureAwait(false);

            if (payment is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            Account? source = await accounts
                .LockForUpdateAsync(payment.SourceAccountId, cancellationToken)
                .ConfigureAwait(false);

            if (source is null)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            DateTimeOffset now = clock.UtcNow;

            // Revalidated against the locked row: the payment may have been captured or
            // voided since the scan listed it.
            DomainResult expired = payment.TryExpire(now);

            if (expired.IsFailure)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            DomainResult released = source.TryReleaseReservation(payment.Amount, now);

            if (released.IsFailure)
            {
                _logger.LogWarning(
                    "Could not release the reservation for expired payment {PaymentId}: {Error}.",
                    paymentId.Value,
                    DomainErrors.Code(released.Error));

                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            outbox.Enqueue(
                EventTypes.PaymentExpired,
                payment.Id.Value,
                Application.Payments.FlowPayJson.Serialize(
                    new PaymentExpiredEvent(
                        Guid.CreateVersion7(),
                        now,
                        payment.Id.Value,
                        payment.AmountMinorUnits,
                        payment.Currency.ToString())));

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Deletes idempotency records past their retention window.
/// </summary>
/// <remarks>
/// The table is written on every mutating request, so it grows at the full request rate.
/// Left alone it becomes the largest table in the database within days, and the unique
/// index the whole deduplication scheme depends on gets slower with it.
/// </remarks>
public sealed class IdempotencyPurgeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowPayOptions _options;
    private readonly ILogger<IdempotencyPurgeService> _logger;

    public IdempotencyPurgeService(
        IServiceScopeFactory scopeFactory,
        IOptions<FlowPayOptions> options,
        ILogger<IdempotencyPurgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_options.Maintenance.IdempotencyPurgeInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                IIdempotencyStore store =
                    scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();

                int deleted = await store
                    .PurgeExpiredAsync(
                        _options.Maintenance.IdempotencyPurgeBatchSize, stoppingToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    _logger.LogInformation("Purged {Count} expired idempotency records.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Idempotency purge failed.");
            }
        }
    }
}
