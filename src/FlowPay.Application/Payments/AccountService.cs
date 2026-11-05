using FlowPay.Application.Abstractions;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace FlowPay.Application.Payments;

/// <summary>Create an account, or return the existing one with that id.</summary>
public sealed record ProvisionAccountCommand(
    Guid? Id,
    string Currency,
    long OpeningBalanceMinorUnits,
    string DisplayName);

/// <summary>
/// Account provisioning.
/// </summary>
/// <remarks>
/// Separate from <see cref="PaymentService"/> because it has a different transactional
/// shape: it creates a row and has no idempotency key, no row locks and no outbox event.
/// Folding it in would mean the payment service's uniform "lock, validate, mutate,
/// enqueue" structure had an exception in it.
/// </remarks>
public sealed class AccountService
{
    private readonly IAccountRepository _accounts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        IAccountRepository accounts,
        IUnitOfWork unitOfWork,
        IClock clock,
        ILogger<AccountService> logger)
    {
        _accounts = accounts;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Creates the account, or returns the existing one if the supplied id is taken.
    /// </summary>
    /// <remarks>
    /// Returning the existing account rather than conflicting makes provisioning safe to
    /// re-run. The platform fee accounts are created this way at every startup, by every
    /// replica, so "create if absent" has to be the normal case rather than an error.
    /// </remarks>
    public async Task<OperationResponseBody> ProvisionAsync(
        ProvisionAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!Currency.TryParseSupported(command.Currency, out Currency currency))
        {
            return PaymentProblems.Unprocessable(
                "currency_unsupported", "The currency is not one FlowPay settles in.");
        }

        if (command.OpeningBalanceMinorUnits < 0L ||
            command.OpeningBalanceMinorUnits > Money.MaxMinorUnits)
        {
            return PaymentProblems.Unprocessable(
                "opening_balance_out_of_range",
                "The opening balance must be between zero and the per-operation limit.");
        }

        AccountId id = command.Id is { } supplied && supplied != Guid.Empty
            ? new AccountId(supplied)
            : AccountId.New();

        Account? existing = await _accounts
            .FindAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return new OperationResponseBody(
                200, FlowPayJson.Serialize(PaymentMapper.ToDto(existing)));
        }

        DateTimeOffset now = _clock.UtcNow;

        DomainResult<Account> created = Account.Open(
            id,
            currency,
            new Money(command.OpeningBalanceMinorUnits, currency),
            command.DisplayName,
            now);

        if (!created.TryGetValue(out Account? account))
        {
            return PaymentProblems.ForDomainError(created.Error);
        }

        _accounts.Add(account);

        await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            // Two replicas provisioning the same fee account at startup will race, and
            // one of them loses on the primary key. Re-reading turns that race into the
            // same answer both callers wanted rather than a startup failure.
            Account? raced = await _accounts
                .FindAsync(id, cancellationToken)
                .ConfigureAwait(false);

            if (raced is not null)
            {
                _logger.LogDebug(
                    failure,
                    "Account {AccountId} was created concurrently; returning the existing row.",
                    id.Value);

                return new OperationResponseBody(
                    200, FlowPayJson.Serialize(PaymentMapper.ToDto(raced)));
            }

            throw;
        }

        _logger.LogInformation(
            "Provisioned account {AccountId} ({Currency}) with opening balance {Balance}.",
            id.Value, currency, command.OpeningBalanceMinorUnits);

        return new OperationResponseBody(
            201, FlowPayJson.Serialize(PaymentMapper.ToDto(account)));
    }
}
