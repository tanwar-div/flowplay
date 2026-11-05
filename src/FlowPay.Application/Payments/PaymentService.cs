using FlowPay.Application.Abstractions;
using FlowPay.Application.Idempotency;
using FlowPay.Application.Outbox;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.Extensions.Logging;

namespace FlowPay.Application.Payments;

/// <summary>
/// The write side of the payments API.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is designed to be invoked <em>inside</em>
/// <see cref="IdempotentExecutor.ExecuteAsync"/>, which owns the transaction. That is why
/// they return a serialized <see cref="OperationResponse"/> rather than a domain object:
/// the executor needs the exact bytes it will store for replay, and a client retrying a
/// timed-out request has to receive the original response, not a fresh projection of a
/// resource that may have moved on since.
/// </para>
/// <para>
/// The ordering inside each method is uniform and deliberate: lock the rows, validate
/// against the locked state, mutate, append ledger lines, enqueue the outbox event. The
/// locks come first so the validation cannot be invalidated between the check and the
/// write, and the outbox enqueue comes last so the event describes what actually happened.
/// </para>
/// </remarks>
public sealed class PaymentService
{
    private readonly IPaymentRepository _payments;
    private readonly IAccountRepository _accounts;
    private readonly IRefundRepository _refunds;
    private readonly SettlementEngine _settlement;
    private readonly IOutboxWriter _outbox;
    private readonly IClock _clock;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IPaymentRepository payments,
        IAccountRepository accounts,
        IRefundRepository refunds,
        SettlementEngine settlement,
        IOutboxWriter outbox,
        IClock clock,
        ILogger<PaymentService> logger)
    {
        _payments = payments;
        _accounts = accounts;
        _refunds = refunds;
        _settlement = settlement;
        _outbox = outbox;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Creates a payment, reserves funds on the payer, and optionally captures it in the
    /// same transaction.
    /// </summary>
    public async Task<OperationResponse> AuthorizeAsync(
        AuthorizePaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (!Currency.TryParse(command.Currency, out Currency currency))
        {
            return Fail(PaymentProblems.Unprocessable(
                "currency_unparseable", "The currency must be a three-letter ISO-4217 code."));
        }

        AccountId sourceId = new(command.SourceAccountId);
        AccountId destinationId = new(command.DestinationAccountId);
        Money amount = new(command.AmountMinorUnits, currency);

        // Cheap, allocation-free rejection of anything malformed, before spending a
        // database round trip or taking any row locks.
        //
        // The validation request is a ref struct, so it cannot live across an await.
        // Building it inside these synchronous local functions keeps it on the stack
        // either side of the account lookup, which is the point of the type: validation
        // is the one thing every request pays for, and it should not allocate.
        PaymentValidationResult ValidateShape()
        {
            PaymentValidationRequest request = new(
                sourceId,
                destinationId,
                amount,
                command.Method,
                command.Reference,
                command.Description,
                hasIdempotencyKey: true);

            return PaymentValidator.Validate(request);
        }

        PaymentValidationResult ValidateAgainstAccounts(Currency sourceCurrency, Currency destinationCurrency)
        {
            PaymentValidationRequest request = new(
                sourceId,
                destinationId,
                amount,
                command.Method,
                command.Reference,
                command.Description,
                hasIdempotencyKey: true);

            return PaymentValidator.ValidateAgainstAccounts(
                request, sourceCurrency, destinationCurrency);
        }

        PaymentValidationResult validation = ValidateShape();

        if (!validation.IsValid)
        {
            return Fail(PaymentProblems.ForValidation(validation));
        }

        // Resolved before any locks are taken: a capture in a currency with no fee
        // account configured must be rejected up front, not after the payer's funds have
        // already been moved.
        // The payment id is minted before the locks are taken, because the fee account
        // shard is derived from it and has to be part of the lock set.
        PaymentId paymentId = PaymentId.New();

        AccountId feeAccountId = default;

        if (command.CaptureImmediately &&
            !_settlement.TryGetFeeAccountId(currency, paymentId, out feeAccountId))
        {
            _logger.LogError(
                "No platform fee account is configured for {Currency}; capture cannot proceed.",
                currency);

            return Fail(PaymentProblems.Unprocessable(
                "fee_account_missing",
                "No platform fee account is configured for this currency."));
        }

        // The fee account is locked along with the other two only when the capture will
        // actually happen in this transaction; an authorize-only call never touches it.
        List<AccountId> toLock = command.CaptureImmediately
            ? [sourceId, destinationId, feeAccountId]
            : [sourceId, destinationId];

        AccountLockSet locked = await _accounts
            .LockForUpdateAsync(toLock, cancellationToken)
            .ConfigureAwait(false);

        Account? source = locked.Find(sourceId);
        Account? destination = locked.Find(destinationId);

        if (source is null || destination is null)
        {
            return Fail(PaymentProblems.NotFound("account"));
        }

        PaymentValidationResult accountValidation =
            ValidateAgainstAccounts(source.Currency, destination.Currency);

        if (!accountValidation.IsValid)
        {
            return Fail(PaymentProblems.ForValidation(accountValidation));
        }

        DateTimeOffset now = _clock.UtcNow;

        DomainResult<PaymentIntent> created = PaymentIntent.Create(
            paymentId,
            sourceId,
            destinationId,
            amount,
            command.Method,
            command.Reference,
            command.Description,
            now);

        if (!created.TryGetValue(out PaymentIntent? payment))
        {
            return Fail(PaymentProblems.ForDomainError(created.Error));
        }

        _payments.Add(payment);

        DomainResult reserved = source.TryReserve(amount, now);
        if (reserved.IsFailure)
        {
            // A declined authorization is a recorded business outcome, not an error: the
            // payment is persisted as Failed so the client can see why, and the failure
            // event is published for downstream risk and notification consumers.
            return RecordAuthorizationFailure(payment, reserved.Error, now);
        }

        DomainResult authorized = payment.TryAuthorize(now);
        if (authorized.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(authorized.Error));
        }

        _outbox.Enqueue(
            EventTypes.PaymentAuthorized,
            payment.Id.Value,
            FlowPayJson.Serialize(new PaymentAuthorizedEvent(
                Guid.CreateVersion7(),
                now,
                payment.Id.Value,
                sourceId.Value,
                destinationId.Value,
                amount.MinorUnits,
                currency.ToString(),
                command.Method.ToString(),
                payment.AuthorizationExpiresAt ?? now)));

        if (!command.CaptureImmediately)
        {
            return Created(payment);
        }

        Account? feeAccount = locked.Find(feeAccountId);

        if (feeAccount is null)
        {
            _logger.LogError(
                "Platform fee account {FeeAccountId} is configured but does not exist.",
                feeAccountId.Value);

            return Fail(PaymentProblems.Unprocessable(
                "fee_account_missing", "The platform fee account is not provisioned."));
        }

        DomainResult captured = _settlement.Capture(
            payment, source, destination, feeAccount, now, out SettlementBreakdown breakdown);

        if (captured.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(captured.Error));
        }

        EnqueueCapturedEvent(payment, breakdown, now);

        return Created(payment);
    }

    /// <summary>Captures a payment that was authorized by an earlier request.</summary>
    public async Task<OperationResponse> CaptureAsync(
        CapturePaymentCommand command,
        CancellationToken cancellationToken)
    {
        PaymentId paymentId = new(command.PaymentId);

        PaymentIntent? payment = await _payments
            .LockForUpdateAsync(paymentId, cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return Fail(PaymentProblems.NotFound("payment"));
        }

        if (!_settlement.TryGetFeeAccountId(
            payment.Currency, payment.Id, out AccountId feeAccountId))
        {
            _logger.LogError(
                "No platform fee account is configured for {Currency}; capture cannot proceed.",
                payment.Currency);

            return Fail(PaymentProblems.Unprocessable(
                "fee_account_missing",
                "No platform fee account is configured for this currency."));
        }

        AccountLockSet locked = await _accounts
            .LockForUpdateAsync(
                [payment.SourceAccountId, payment.DestinationAccountId, feeAccountId],
                cancellationToken)
            .ConfigureAwait(false);

        Account? source = locked.Find(payment.SourceAccountId);
        Account? destination = locked.Find(payment.DestinationAccountId);
        Account? feeAccount = locked.Find(feeAccountId);

        if (source is null || destination is null || feeAccount is null)
        {
            return Fail(PaymentProblems.NotFound("account"));
        }

        DateTimeOffset now = _clock.UtcNow;

        DomainResult captured = _settlement.Capture(
            payment, source, destination, feeAccount, now, out SettlementBreakdown breakdown);

        if (captured.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(captured.Error));
        }

        EnqueueCapturedEvent(payment, breakdown, now);

        return Ok(payment);
    }

    /// <summary>Releases an authorization and returns the reserved funds.</summary>
    public async Task<OperationResponse> VoidAsync(
        VoidPaymentCommand command,
        CancellationToken cancellationToken)
    {
        PaymentId paymentId = new(command.PaymentId);

        PaymentIntent? payment = await _payments
            .LockForUpdateAsync(paymentId, cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return Fail(PaymentProblems.NotFound("payment"));
        }

        Account? source = await _accounts
            .LockForUpdateAsync(payment.SourceAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (source is null)
        {
            return Fail(PaymentProblems.NotFound("account"));
        }

        DateTimeOffset now = _clock.UtcNow;

        // The void is attempted on the payment first. If the payment is not in a voidable
        // state, the reservation must not be released: releasing funds for a payment that
        // was already captured would hand the payer money that has already been settled.
        DomainResult voided = payment.TryVoid(now);
        if (voided.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(voided.Error));
        }

        DomainResult released = source.TryReleaseReservation(payment.Amount, now);
        if (released.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(released.Error));
        }

        _outbox.Enqueue(
            EventTypes.PaymentVoided,
            payment.Id.Value,
            FlowPayJson.Serialize(new PaymentVoidedEvent(
                Guid.CreateVersion7(),
                now,
                payment.Id.Value,
                payment.AmountMinorUnits,
                payment.Currency.ToString())));

        return Ok(payment);
    }

    /// <summary>
    /// Refunds some or all of a captured payment.
    /// </summary>
    /// <remarks>
    /// The payment row is locked before its refunded total is read, which is what makes
    /// concurrent refunds safe. Two requests refunding the same payment serialize on that
    /// lock, so the second one reads a total that already includes the first and is
    /// rejected if the sum would exceed the captured amount.
    /// </remarks>
    public async Task<OperationResponse> RefundAsync(
        RefundPaymentCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!Currency.TryParse(command.Currency, out Currency currency))
        {
            return Fail(PaymentProblems.Unprocessable(
                "currency_unparseable", "The currency must be a three-letter ISO-4217 code."));
        }

        PaymentId paymentId = new(command.PaymentId);

        PaymentIntent? payment = await _payments
            .LockForUpdateAsync(paymentId, cancellationToken)
            .ConfigureAwait(false);

        if (payment is null)
        {
            return Fail(PaymentProblems.NotFound("payment"));
        }

        Money amount = new(command.AmountMinorUnits, currency);

        if (!amount.IsPositive)
        {
            return Fail(PaymentProblems.ForDomainError(DomainError.AmountNotPositive));
        }

        AccountLockSet locked = await _accounts
            .LockForUpdateAsync(
                [payment.SourceAccountId, payment.DestinationAccountId],
                cancellationToken)
            .ConfigureAwait(false);

        Account? payer = locked.Find(payment.SourceAccountId);
        Account? payee = locked.Find(payment.DestinationAccountId);

        if (payer is null || payee is null)
        {
            return Fail(PaymentProblems.NotFound("account"));
        }

        DateTimeOffset now = _clock.UtcNow;
        RefundId refundId = RefundId.New();

        DomainResult result = _settlement.Refund(
            payment, payer, payee, refundId, amount, now, out PaymentStatus resultingStatus);

        if (result.IsFailure)
        {
            return Fail(PaymentProblems.ForDomainError(result.Error));
        }

        Refund refund = Refund.Succeeded(
            refundId,
            paymentId,
            amount,
            command.Reason,
            idempotencyKey,
            resultingStatus,
            now);

        _refunds.Add(refund);

        _outbox.Enqueue(
            EventTypes.PaymentRefunded,
            payment.Id.Value,
            FlowPayJson.Serialize(new PaymentRefundedEvent(
                Guid.CreateVersion7(),
                now,
                payment.Id.Value,
                refundId.Value,
                amount.MinorUnits,
                payment.RefundedMinorUnits,
                payment.AmountMinorUnits,
                currency.ToString(),
                resultingStatus.ToString())));

        string json = FlowPayJson.Serialize(
            PaymentMapper.ToDto(refund, payment.RefundedAmount));

        return new OperationResponse(201, json, refundId.Value);
    }

    private OperationResponse RecordAuthorizationFailure(
        PaymentIntent payment,
        DomainError error,
        DateTimeOffset now)
    {
        PaymentFailureReason reason = error switch
        {
            DomainError.InsufficientFunds => PaymentFailureReason.InsufficientFunds,
            DomainError.AccountNotActive => PaymentFailureReason.AccountFrozen,
            DomainError.CurrencyMismatch => PaymentFailureReason.CurrencyMismatch,
            _ => PaymentFailureReason.ValidationFailed,
        };

        payment.TryFail(reason, now).EnsureSuccess();

        _outbox.Enqueue(
            EventTypes.PaymentFailed,
            payment.Id.Value,
            FlowPayJson.Serialize(new PaymentFailedEvent(
                Guid.CreateVersion7(),
                now,
                payment.Id.Value,
                payment.AmountMinorUnits,
                payment.Currency.ToString(),
                reason.ToString())));

        _logger.LogInformation(
            "Payment {PaymentId} declined: {Reason}.", payment.Id.Value, reason);

        // 201 with a Failed payment, not an HTTP error. The request succeeded in creating
        // a durable, addressable record of a declined payment, and the client needs that
        // record's id to reconcile against. Returning 4xx here would leave the client
        // unable to reference the decline it just caused.
        return Created(payment);
    }

    private void EnqueueCapturedEvent(
        PaymentIntent payment,
        in SettlementBreakdown breakdown,
        DateTimeOffset now)
        => _outbox.Enqueue(
            EventTypes.PaymentCaptured,
            payment.Id.Value,
            FlowPayJson.Serialize(new PaymentCapturedEvent(
                Guid.CreateVersion7(),
                now,
                payment.Id.Value,
                payment.SourceAccountId.Value,
                payment.DestinationAccountId.Value,
                breakdown.Gross.MinorUnits,
                breakdown.Fee.MinorUnits,
                breakdown.Net.MinorUnits,
                breakdown.Gross.Currency.ToString())));

    private static OperationResponse Created(PaymentIntent payment)
        => new(201, FlowPayJson.Serialize(PaymentMapper.ToDto(payment)), payment.Id.Value);

    private static OperationResponse Ok(PaymentIntent payment)
        => new(200, FlowPayJson.Serialize(PaymentMapper.ToDto(payment)), payment.Id.Value);

    /// <summary>
    /// Wraps a rejection so the idempotency layer does not persist it.
    /// </summary>
    /// <remarks>
    /// A rejected request leaves no state behind, so there is nothing to make idempotent.
    /// Storing the rejection would be actively harmful: a client that fixed its request
    /// and retried with the same key would be handed the cached failure forever.
    /// </remarks>
    private static OperationResponse Fail(OperationResponseBody body)
        => OperationResponse.Transient(body.StatusCode, body.Json);
}
