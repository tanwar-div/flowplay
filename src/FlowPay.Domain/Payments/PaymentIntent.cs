using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Payments;

/// <summary>
/// A payment from one account to another, and the aggregate that owns its lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation goes through <see cref="PaymentStateMachine"/>, so there is exactly one
/// definition of the legal lifecycle and no code path can invent a transition. The
/// methods are <c>Try*</c> and return <see cref="DomainResult"/>: a rejected transition
/// leaves the aggregate byte-for-byte unchanged and allocates nothing, which is what lets
/// the state-transition microbenchmarks assert zero managed allocations while still
/// exercising the real production code rather than a stripped-down copy of it.
/// </para>
/// <para>
/// The aggregate deliberately does not raise domain events. Collecting events would mean
/// allocating a list and an event object per transition, on the hottest path in the
/// system. Instead the application layer writes the corresponding outbox row inside the
/// same database transaction as the state change, which gives the same
/// all-or-nothing guarantee without putting an allocation in the domain.
/// </para>
/// </remarks>
public sealed class PaymentIntent
{
    /// <summary>How long an authorization stays valid before it can be expired.</summary>
    public static readonly TimeSpan AuthorizationWindow = TimeSpan.FromDays(7);

    private PaymentIntent()
    {
        // Materialization constructor for the persistence layer.
    }

    public PaymentId Id { get; private set; }

    public AccountId SourceAccountId { get; private set; }

    public AccountId DestinationAccountId { get; private set; }

    public long AmountMinorUnits { get; private set; }

    public Currency Currency { get; private set; }

    /// <summary>The processing fee retained from the captured amount.</summary>
    public long FeeMinorUnits { get; private set; }

    /// <summary>
    /// Total refunded so far, in minor units. The single number that bounds every
    /// refund: the invariant <c>RefundedMinorUnits &lt;= AmountMinorUnits</c> is what the
    /// concurrent refund stress test exists to break.
    /// </summary>
    public long RefundedMinorUnits { get; private set; }

    public PaymentStatus Status { get; private set; }

    public PaymentMethod Method { get; private set; }

    public PaymentFailureReason FailureReason { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? AuthorizedAt { get; private set; }

    public DateTimeOffset? CapturedAt { get; private set; }

    /// <summary>When an un-captured authorization becomes eligible for expiry.</summary>
    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, mapped as the optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    public Money Amount => new(AmountMinorUnits, Currency);

    public Money Fee => new(FeeMinorUnits, Currency);

    public Money RefundedAmount => new(RefundedMinorUnits, Currency);

    /// <summary>How much of this payment can still be refunded.</summary>
    public Money RefundableAmount => new(AmountMinorUnits - RefundedMinorUnits, Currency);

    public bool IsTerminal => PaymentStateMachine.IsTerminal(Status);

    public bool IsSettled => PaymentStateMachine.IsSettled(Status);

    /// <summary>Creates a payment intent in <see cref="PaymentStatus.Pending"/>.</summary>
    public static DomainResult<PaymentIntent> Create(
        PaymentId id,
        AccountId sourceAccountId,
        AccountId destinationAccountId,
        Money amount,
        PaymentMethod method,
        string reference,
        string description,
        DateTimeOffset now)
    {
        PaymentValidationRequest request = new(
            sourceAccountId,
            destinationAccountId,
            amount,
            method,
            reference,
            description,
            hasIdempotencyKey: true);

        PaymentValidationResult validation = PaymentValidator.Validate(request);

        if (!validation.IsValid)
        {
            // Map the first failure onto the coarser domain taxonomy. The caller that
            // needs the full bitmask runs PaymentValidator itself before getting here;
            // this is the backstop that keeps an invalid aggregate unconstructable.
            return DomainResult<PaymentIntent>.Failure(MapValidationFailure(validation));
        }

        return DomainResult<PaymentIntent>.Success(new PaymentIntent
        {
            Id = id,
            SourceAccountId = sourceAccountId,
            DestinationAccountId = destinationAccountId,
            AmountMinorUnits = amount.MinorUnits,
            Currency = amount.Currency,
            FeeMinorUnits = 0L,
            RefundedMinorUnits = 0L,
            Status = PaymentStatus.Pending,
            Method = method,
            FailureReason = PaymentFailureReason.None,
            Reference = reference,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// Moves the payment to <see cref="PaymentStatus.Authorized"/> and starts the
    /// authorization clock.
    /// </summary>
    public DomainResult TryAuthorize(DateTimeOffset now)
    {
        DomainResult transition = EnsureTransition(PaymentStatus.Authorized);
        if (transition.IsFailure)
        {
            return transition;
        }

        Status = PaymentStatus.Authorized;
        AuthorizedAt = now;
        AuthorizationExpiresAt = now + AuthorizationWindow;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Moves the payment to <see cref="PaymentStatus.Captured"/> and records the fee
    /// retained from the captured amount.
    /// </summary>
    public DomainResult TryCapture(in Money fee, DateTimeOffset now)
    {
        DomainResult transition = EnsureTransition(PaymentStatus.Captured);
        if (transition.IsFailure)
        {
            return transition;
        }

        if (fee.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (fee.IsNegative || fee.MinorUnits > AmountMinorUnits)
        {
            return DomainResult.Failure(DomainError.AmountExceedsLimit);
        }

        Status = PaymentStatus.Captured;
        FeeMinorUnits = fee.MinorUnits;
        CapturedAt = now;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>Marks the payment failed with a specific reason.</summary>
    public DomainResult TryFail(PaymentFailureReason reason, DateTimeOffset now)
    {
        DomainResult transition = EnsureTransition(PaymentStatus.Failed);
        if (transition.IsFailure)
        {
            return transition;
        }

        Status = PaymentStatus.Failed;
        FailureReason = reason;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>Releases an authorization before capture.</summary>
    public DomainResult TryVoid(DateTimeOffset now)
    {
        DomainResult transition = EnsureTransition(PaymentStatus.Voided);
        if (transition.IsFailure)
        {
            return transition;
        }

        Status = PaymentStatus.Voided;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Expires an authorization that was never captured. Rejected before the
    /// authorization window has actually elapsed, so a clock skew or an over-eager sweep
    /// cannot cancel live authorizations.
    /// </summary>
    public DomainResult TryExpire(DateTimeOffset now)
    {
        DomainResult transition = EnsureTransition(PaymentStatus.Expired);
        if (transition.IsFailure)
        {
            return transition;
        }

        if (AuthorizationExpiresAt is { } expiresAt && now < expiresAt)
        {
            return DomainResult.Failure(DomainError.TransitionNotPermitted);
        }

        Status = PaymentStatus.Expired;
        FailureReason = PaymentFailureReason.AuthorizationExpired;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Applies a refund of <paramref name="amount"/> and reports the resulting status.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the aggregate's most contended operation. Two refunds racing on the same
    /// payment must not both succeed if their sum exceeds the captured amount, and the
    /// check here is the innermost of three defences:
    /// </para>
    /// <list type="number">
    /// <item><description>this method, which rejects the over-refund outright;</description></item>
    /// <item><description>the repository, which reads the row <c>FOR UPDATE</c> so the two
    /// refunds serialize instead of both reading the same pre-refund total;</description></item>
    /// <item><description>a <c>CHECK (refunded_minor_units &lt;= amount_minor_units)</c>
    /// constraint, which makes an over-refunded row impossible to store at all.</description></item>
    /// </list>
    /// </remarks>
    public DomainResult TryRefund(in Money amount, DateTimeOffset now, out PaymentStatus resultingStatus)
    {
        resultingStatus = Status;

        if (!IsSettled)
        {
            return DomainResult.Failure(DomainError.PaymentNotCaptured);
        }

        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (!RefundedAmount.TryAdd(amount, out Money totalRefunded))
        {
            return DomainResult.Failure(DomainError.ArithmeticOverflow);
        }

        if (totalRefunded.MinorUnits > AmountMinorUnits)
        {
            return DomainResult.Failure(DomainError.RefundExceedsCapturedAmount);
        }

        PaymentStatus target = totalRefunded.MinorUnits == AmountMinorUnits
            ? PaymentStatus.Refunded
            : PaymentStatus.PartiallyRefunded;

        DomainResult transition = EnsureTransition(target);
        if (transition.IsFailure)
        {
            return transition;
        }

        RefundedMinorUnits = totalRefunded.MinorUnits;
        Status = target;
        UpdatedAt = now;
        resultingStatus = target;
        return DomainResult.Success;
    }

    /// <summary>
    /// Checks a transition against the state machine and translates its rejection into
    /// the domain error taxonomy.
    /// </summary>
    private DomainResult EnsureTransition(PaymentStatus target)
    {
        if (PaymentStateMachine.TryTransition(Status, target, out TransitionError error))
        {
            return DomainResult.Success;
        }

        return DomainResult.Failure(error switch
        {
            TransitionError.UnknownStatus => DomainError.UnknownState,
            TransitionError.TerminalState => DomainError.TerminalState,
            _ => DomainError.TransitionNotPermitted,
        });
    }

    private static DomainError MapValidationFailure(PaymentValidationResult validation)
    {
        if (validation.Has(PaymentValidationFailures.AmountNotPositive))
        {
            return DomainError.AmountNotPositive;
        }

        if (validation.Has(PaymentValidationFailures.AmountExceedsLimit) ||
            validation.Has(PaymentValidationFailures.AmountExceedsMethodLimit))
        {
            return DomainError.AmountExceedsLimit;
        }

        if (validation.Has(PaymentValidationFailures.MissingCurrency) ||
            validation.Has(PaymentValidationFailures.UnsupportedCurrency))
        {
            return DomainError.CurrencyMismatch;
        }

        return DomainError.TransitionNotPermitted;
    }
}
