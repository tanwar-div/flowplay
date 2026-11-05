using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace FlowPay.Domain.Primitives;

/// <summary>
/// Every rejection the domain can produce. Stable values: these map onto API error codes.
/// </summary>
public enum DomainError : byte
{
    None = 0,

    /// <summary>The operation is not legal from the aggregate's current status.</summary>
    TransitionNotPermitted = 1,

    /// <summary>The aggregate has already reached a terminal status.</summary>
    TerminalState = 2,

    /// <summary>The stored status is not a defined enum value.</summary>
    UnknownState = 3,

    /// <summary>Two amounts in the operation are denominated differently.</summary>
    CurrencyMismatch = 4,

    /// <summary>The amount is zero or negative where a positive amount is required.</summary>
    AmountNotPositive = 5,

    /// <summary>The amount is above FlowPay's per-operation ceiling.</summary>
    AmountExceedsLimit = 6,

    /// <summary>Arithmetic on the amount would overflow 64 bits.</summary>
    ArithmeticOverflow = 7,

    /// <summary>The source account's available balance cannot cover the amount.</summary>
    InsufficientFunds = 8,

    /// <summary>The account is closed or frozen.</summary>
    AccountNotActive = 9,

    /// <summary>The refund would push total refunds above the captured amount.</summary>
    RefundExceedsCapturedAmount = 10,

    /// <summary>The payment has not been captured, so there is nothing to refund.</summary>
    PaymentNotCaptured = 11,

    /// <summary>Releasing more than was reserved on the account.</summary>
    ReservationExceeded = 12,

    /// <summary>A concurrent writer changed the aggregate first.</summary>
    ConcurrencyConflict = 13,
}

/// <summary>Human-readable and client-facing projections of <see cref="DomainError"/>.</summary>
public static class DomainErrors
{
    /// <summary>The stable snake_case code sent to API clients.</summary>
    public static string Code(DomainError error) => error switch
    {
        DomainError.None => "none",
        DomainError.TransitionNotPermitted => "transition_not_permitted",
        DomainError.TerminalState => "payment_in_terminal_state",
        DomainError.UnknownState => "unknown_payment_state",
        DomainError.CurrencyMismatch => "currency_mismatch",
        DomainError.AmountNotPositive => "amount_not_positive",
        DomainError.AmountExceedsLimit => "amount_exceeds_limit",
        DomainError.ArithmeticOverflow => "amount_overflow",
        DomainError.InsufficientFunds => "insufficient_funds",
        DomainError.AccountNotActive => "account_not_active",
        DomainError.RefundExceedsCapturedAmount => "refund_exceeds_captured_amount",
        DomainError.PaymentNotCaptured => "payment_not_captured",
        DomainError.ReservationExceeded => "reservation_exceeded",
        DomainError.ConcurrencyConflict => "concurrency_conflict",
        _ => "domain_error",
    };

    /// <summary>A short explanation suitable for an API error payload.</summary>
    public static string Message(DomainError error) => error switch
    {
        DomainError.None => "No error.",
        DomainError.TransitionNotPermitted => "The payment cannot move to the requested state.",
        DomainError.TerminalState => "The payment has already reached a final state.",
        DomainError.UnknownState => "The payment is in an unrecognized state.",
        DomainError.CurrencyMismatch => "The amounts involved use different currencies.",
        DomainError.AmountNotPositive => "The amount must be greater than zero.",
        DomainError.AmountExceedsLimit => "The amount exceeds the per-operation limit.",
        DomainError.ArithmeticOverflow => "The amount is too large to process.",
        DomainError.InsufficientFunds => "The source account has insufficient available funds.",
        DomainError.AccountNotActive => "The account is not active.",
        DomainError.RefundExceedsCapturedAmount =>
            "The refund would exceed the amount captured for this payment.",
        DomainError.PaymentNotCaptured => "The payment has not been captured.",
        DomainError.ReservationExceeded => "The release exceeds the amount reserved.",
        DomainError.ConcurrencyConflict => "The payment was modified concurrently.",
        _ => "The operation was rejected by the domain.",
    };

    /// <summary>
    /// Whether retrying the same request could plausibly succeed. Drives whether the
    /// API answers 409 (retry may help) or 422 (it will not).
    /// </summary>
    public static bool IsRetryable(DomainError error) => error is DomainError.ConcurrencyConflict;
}

/// <summary>
/// The outcome of a domain operation: success, or a single <see cref="DomainError"/>.
/// </summary>
/// <remarks>
/// A one-byte struct, so returning a failure from a state transition costs nothing. The
/// alternative — throwing — would allocate an exception plus a stack trace on a path the
/// microbenchmarks assert is allocation-free, and failures here are routine business
/// outcomes rather than bugs.
/// </remarks>
public readonly struct DomainResult
{
    private readonly DomainError _error;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DomainResult(DomainError error) => _error = error;

    public static DomainResult Success
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DomainResult Failure(DomainError error) => new(error);

    public DomainError Error
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _error;
    }

    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _error == DomainError.None;
    }

    public bool IsFailure
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _error != DomainError.None;
    }

    /// <summary>Throws when the result is a failure. For tests and seed code only.</summary>
    public void EnsureSuccess()
    {
        if (IsFailure)
        {
            throw new InvalidOperationException(DomainErrors.Message(_error));
        }
    }

    public override string ToString() => DomainErrors.Code(_error);
}

/// <summary>A domain operation that yields a value on success.</summary>
public readonly struct DomainResult<T>
{
    private readonly T? _value;
    private readonly DomainError _error;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DomainResult(T? value, DomainError error)
    {
        _value = value;
        _error = error;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DomainResult<T> Success(T value) => new(value, DomainError.None);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DomainResult<T> Failure(DomainError error) => new(default, error);

    public DomainError Error => _error;

    public bool IsSuccess => _error == DomainError.None;

    public bool IsFailure => _error != DomainError.None;

    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value;
        return _error == DomainError.None;
    }

    public T Value => _error == DomainError.None
        ? _value!
        : throw new InvalidOperationException(DomainErrors.Message(_error));
}
