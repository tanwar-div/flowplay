using System.Numerics;
using System.Runtime.CompilerServices;
using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Payments;

/// <summary>
/// Every way a payment request can be rejected by domain validation.
/// </summary>
/// <remarks>
/// A flags enum rather than a list of error objects: validation reports all failures at
/// once (so the caller gets one complete 422 instead of a game of whack-a-mole) while
/// the result stays a single 32-bit value with nothing allocated.
/// </remarks>
[Flags]
public enum PaymentValidationFailures : uint
{
    None = 0,
    MissingSourceAccount = 1 << 0,
    MissingDestinationAccount = 1 << 1,
    SameSourceAndDestination = 1 << 2,
    MissingCurrency = 1 << 3,
    UnsupportedCurrency = 1 << 4,
    AmountNotPositive = 1 << 5,
    AmountExceedsLimit = 1 << 6,
    AmountExceedsMethodLimit = 1 << 7,
    UnspecifiedPaymentMethod = 1 << 8,
    ReferenceTooLong = 1 << 9,
    ReferenceHasControlCharacters = 1 << 10,
    DescriptionTooLong = 1 << 11,
    MissingIdempotencyKey = 1 << 12,
    SourceCurrencyMismatch = 1 << 13,
    DestinationCurrencyMismatch = 1 << 14,
}

/// <summary>
/// The outcome of validating a payment request: a bitmask of failures and nothing else.
/// </summary>
public readonly struct PaymentValidationResult
{
    private readonly PaymentValidationFailures _failures;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PaymentValidationResult(PaymentValidationFailures failures) => _failures = failures;

    public PaymentValidationFailures Failures
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _failures;
    }

    public bool IsValid
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _failures == PaymentValidationFailures.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(PaymentValidationFailures failure) => (_failures & failure) != 0;

    /// <summary>Number of distinct failures recorded.</summary>
    public int FailureCount => BitOperations.PopCount((uint)_failures);

    /// <summary>
    /// Writes each recorded failure's stable error code into
    /// <paramref name="destination"/> and returns how many were written.
    /// </summary>
    /// <remarks>
    /// The codes are compile-time string literals, so they come from the interned string
    /// pool and projecting them costs no allocation beyond the caller's own span.
    /// </remarks>
    public int GetErrorCodes(Span<string> destination)
    {
        int count = 0;
        uint bits = (uint)_failures;

        while (bits != 0 && count < destination.Length)
        {
            int index = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            destination[count++] = PaymentValidationCodes.ForBit(index);
        }

        return count;
    }
}

/// <summary>Stable, client-facing error codes for each validation failure.</summary>
public static class PaymentValidationCodes
{
    // A static readonly array, not a span-over-RVA-data trick: that optimization only
    // applies to primitive constants. This allocates once per process at type
    // initialization and never again, so projecting codes stays allocation-free per call.
    private static readonly string[] Codes =
    [
        "source_account_missing",
        "destination_account_missing",
        "source_equals_destination",
        "currency_missing",
        "currency_unsupported",
        "amount_not_positive",
        "amount_exceeds_limit",
        "amount_exceeds_method_limit",
        "payment_method_unspecified",
        "reference_too_long",
        "reference_invalid_characters",
        "description_too_long",
        "idempotency_key_missing",
        "source_account_currency_mismatch",
        "destination_account_currency_mismatch",
    ];

    internal static string ForBit(int bitIndex)
        => (uint)bitIndex < (uint)Codes.Length ? Codes[bitIndex] : "validation_failed";

    /// <summary>The code for a single failure flag.</summary>
    public static string For(PaymentValidationFailures failure)
    {
        uint bits = (uint)failure;
        return bits == 0 ? "none" : ForBit(BitOperations.TrailingZeroCount(bits));
    }
}

/// <summary>
/// A payment request in the shape validation needs it: a stack-only snapshot with no
/// strings except the two free-text fields, which arrive as spans.
/// </summary>
public readonly ref struct PaymentValidationRequest
{
    public PaymentValidationRequest(
        AccountId sourceAccountId,
        AccountId destinationAccountId,
        Money amount,
        PaymentMethod method,
        ReadOnlySpan<char> reference,
        ReadOnlySpan<char> description,
        bool hasIdempotencyKey)
    {
        SourceAccountId = sourceAccountId;
        DestinationAccountId = destinationAccountId;
        Amount = amount;
        Method = method;
        Reference = reference;
        Description = description;
        HasIdempotencyKey = hasIdempotencyKey;
    }

    public AccountId SourceAccountId { get; }

    public AccountId DestinationAccountId { get; }

    public Money Amount { get; }

    public PaymentMethod Method { get; }

    public ReadOnlySpan<char> Reference { get; }

    public ReadOnlySpan<char> Description { get; }

    public bool HasIdempotencyKey { get; }
}

/// <summary>
/// Domain validation for payment requests.
/// </summary>
/// <remarks>
/// This is the first thing every authorization touches, so it is written to run without
/// allocating: a <c>ref struct</c> request, span-based text checks, and a bitmask result.
/// Invalid input is an expected outcome rather than an exceptional one, so nothing here
/// throws. An exception on this path would allocate and would cost orders of magnitude
/// more than the checks themselves.
/// </remarks>
public static class PaymentValidator
{
    /// <summary>Maximum length of the client-supplied merchant reference.</summary>
    public const int MaxReferenceLength = 64;

    /// <summary>Maximum length of the client-supplied description.</summary>
    public const int MaxDescriptionLength = 512;

    /// <summary>Per-transaction ceiling for card payments, in minor units.</summary>
    public const long CardLimitMinorUnits = 2_000_000L;

    /// <summary>Per-transaction ceiling for wallet payments, in minor units.</summary>
    public const long WalletLimitMinorUnits = 500_000L;

    /// <summary>Per-transaction ceiling for direct debits, in minor units.</summary>
    public const long DirectDebitLimitMinorUnits = 10_000_000L;

    // DEL plus the C1 control block. These corrupt log lines and CSV exports downstream
    // of the ledger just as surely as the C0 range does.
    private const char ExtendedControlStart = (char)0x7F;
    private const char ExtendedControlEnd = (char)0x9F;

    /// <summary>
    /// Validates a request against the rules that need no database access.
    /// </summary>
    public static PaymentValidationResult Validate(in PaymentValidationRequest request)
    {
        PaymentValidationFailures failures = PaymentValidationFailures.None;

        if (request.SourceAccountId.IsEmpty)
        {
            failures |= PaymentValidationFailures.MissingSourceAccount;
        }

        if (request.DestinationAccountId.IsEmpty)
        {
            failures |= PaymentValidationFailures.MissingDestinationAccount;
        }

        if (!request.SourceAccountId.IsEmpty &&
            request.SourceAccountId == request.DestinationAccountId)
        {
            failures |= PaymentValidationFailures.SameSourceAndDestination;
        }

        Currency currency = request.Amount.Currency;

        if (currency.IsNone)
        {
            failures |= PaymentValidationFailures.MissingCurrency;
        }
        else if (!currency.IsSupported)
        {
            failures |= PaymentValidationFailures.UnsupportedCurrency;
        }

        long minorUnits = request.Amount.MinorUnits;

        if (minorUnits <= 0L)
        {
            failures |= PaymentValidationFailures.AmountNotPositive;
        }
        else if (minorUnits > Money.MaxMinorUnits)
        {
            failures |= PaymentValidationFailures.AmountExceedsLimit;
        }
        else if (minorUnits > MethodLimit(request.Method))
        {
            failures |= PaymentValidationFailures.AmountExceedsMethodLimit;
        }

        if (request.Method == PaymentMethod.Unspecified)
        {
            failures |= PaymentValidationFailures.UnspecifiedPaymentMethod;
        }

        if (request.Reference.Length > MaxReferenceLength)
        {
            failures |= PaymentValidationFailures.ReferenceTooLong;
        }
        else if (ContainsControlCharacter(request.Reference))
        {
            failures |= PaymentValidationFailures.ReferenceHasControlCharacters;
        }

        if (request.Description.Length > MaxDescriptionLength)
        {
            failures |= PaymentValidationFailures.DescriptionTooLong;
        }

        if (!request.HasIdempotencyKey)
        {
            failures |= PaymentValidationFailures.MissingIdempotencyKey;
        }

        return new PaymentValidationResult(failures);
    }

    /// <summary>
    /// Validates the request against the two accounts it names. Kept separate from
    /// <see cref="Validate"/> because it needs loaded state, while the rules above do not.
    /// </summary>
    public static PaymentValidationResult ValidateAgainstAccounts(
        in PaymentValidationRequest request,
        Currency sourceCurrency,
        Currency destinationCurrency)
    {
        PaymentValidationFailures failures = Validate(request).Failures;

        Currency currency = request.Amount.Currency;

        if (!currency.IsNone)
        {
            if (!sourceCurrency.IsNone && sourceCurrency != currency)
            {
                failures |= PaymentValidationFailures.SourceCurrencyMismatch;
            }

            if (!destinationCurrency.IsNone && destinationCurrency != currency)
            {
                failures |= PaymentValidationFailures.DestinationCurrencyMismatch;
            }
        }

        return new PaymentValidationResult(failures);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long MethodLimit(PaymentMethod method) => method switch
    {
        PaymentMethod.Card => CardLimitMinorUnits,
        PaymentMethod.Wallet => WalletLimitMinorUnits,
        PaymentMethod.DirectDebit => DirectDebitLimitMinorUnits,
        _ => Money.MaxMinorUnits,
    };

    private static bool ContainsControlCharacter(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < ' ' || (c >= ExtendedControlStart && c <= ExtendedControlEnd))
            {
                return true;
            }
        }

        return false;
    }
}
