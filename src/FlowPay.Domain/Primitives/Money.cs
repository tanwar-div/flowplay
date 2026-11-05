using System.Globalization;
using System.Runtime.CompilerServices;

namespace FlowPay.Domain.Primitives;

/// <summary>
/// A currency-tagged monetary amount held as a signed count of minor units
/// (cents, paise, yen) rather than a decimal.
/// </summary>
/// <remarks>
/// <para>
/// Integer minor units remove every rounding question from the ledger: a sum of
/// <see cref="Money"/> values is exact, and the double-entry invariant
/// (debits == credits) can be asserted with integer equality.
/// </para>
/// <para>
/// All arithmetic is exposed as <c>Try*</c> methods that report currency mismatch and
/// overflow through a return value instead of an exception. Throwing on the
/// authorization path would allocate an exception object and blow the allocation budget
/// the microbenchmarks assert against, so the failure modes are values, not throws.
/// </para>
/// </remarks>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    /// <summary>
    /// The largest amount FlowPay will accept in a single operation, in minor units
    /// (10^12, i.e. ten billion major units for a two-decimal currency).
    /// </summary>
    /// <remarks>
    /// The cap is not only a business rule. It guarantees <c>amount * basisPoints</c>
    /// stays inside <see cref="long"/> for any basis-point value up to 10,000, which is
    /// what lets <see cref="FeeSchedule"/> do its arithmetic in 64-bit integers instead
    /// of falling back to a slower 128-bit path.
    /// </remarks>
    public const long MaxMinorUnits = 1_000_000_000_000L;

    private readonly long _minorUnits;
    private readonly Currency _currency;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Money(long minorUnits, Currency currency)
    {
        _minorUnits = minorUnits;
        _currency = currency;
    }

    public long MinorUnits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _minorUnits;
    }

    public Currency Currency
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _currency;
    }

    public bool IsZero
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _minorUnits == 0;
    }

    public bool IsPositive
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _minorUnits > 0;
    }

    public bool IsNegative
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _minorUnits < 0;
    }

    /// <summary>A zero amount in the given currency.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Money Zero(Currency currency) => new(0L, currency);

    /// <summary>
    /// Adds two amounts. Fails when the currencies differ or the sum would overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(in Money other, out Money result)
    {
        if (_currency != other._currency)
        {
            result = default;
            return false;
        }

        long sum = unchecked(_minorUnits + other._minorUnits);

        // Signed overflow check without a try/catch: the sum's sign can only disagree
        // with both operands' shared sign if the addition wrapped.
        if (((_minorUnits ^ sum) & (other._minorUnits ^ sum)) < 0)
        {
            result = default;
            return false;
        }

        result = new Money(sum, _currency);
        return true;
    }

    /// <summary>
    /// Subtracts <paramref name="other"/>. Fails on currency mismatch or overflow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TrySubtract(in Money other, out Money result)
    {
        if (_currency != other._currency)
        {
            result = default;
            return false;
        }

        long difference = unchecked(_minorUnits - other._minorUnits);

        if (((_minorUnits ^ other._minorUnits) & (_minorUnits ^ difference)) < 0)
        {
            result = default;
            return false;
        }

        result = new Money(difference, _currency);
        return true;
    }

    /// <summary>
    /// Splits an amount into <paramref name="parts"/> shares whose sum is exactly the
    /// original amount, distributing the remainder one minor unit at a time.
    /// </summary>
    /// <remarks>
    /// Used for multi-destination settlement. Writing into a caller-supplied span keeps
    /// the operation allocation-free even for large splits.
    /// </remarks>
    public bool TryAllocate(int parts, Span<Money> destination)
    {
        if (parts <= 0 || destination.Length < parts || _minorUnits < 0)
        {
            return false;
        }

        long baseShare = Math.DivRem(_minorUnits, parts, out long remainder);

        for (int i = 0; i < parts; i++)
        {
            // The first `remainder` shares absorb the extra minor unit each, so the
            // shares never drift from the total by a rounding error.
            long share = baseShare + (i < remainder ? 1L : 0L);
            destination[i] = new Money(share, _currency);
        }

        return true;
    }

    /// <summary>
    /// True when this amount is within FlowPay's accepted range for a single operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsWithinOperationalLimit() => _minorUnits is > 0 and <= MaxMinorUnits;

    /// <summary>
    /// Parses a decimal major-unit amount (for example <c>"120.45"</c>) into minor units
    /// for the supplied currency, without allocating and without binary floating point.
    /// </summary>
    public static bool TryParseMajorUnits(ReadOnlySpan<char> text, Currency currency, out Money money)
    {
        money = default;

        if (currency.IsNone || text.IsEmpty || text.Length > 32)
        {
            return false;
        }

        bool negative = false;
        if (text[0] is '-' or '+')
        {
            negative = text[0] == '-';
            text = text[1..];
            if (text.IsEmpty)
            {
                return false;
            }
        }

        int separator = text.IndexOf('.');
        ReadOnlySpan<char> wholePart = separator < 0 ? text : text[..separator];
        ReadOnlySpan<char> fractionPart = separator < 0 ? [] : text[(separator + 1)..];

        int digits = currency.MinorUnitDigits;
        if (wholePart.IsEmpty || fractionPart.Length > digits)
        {
            return false;
        }

        if (!long.TryParse(wholePart, NumberStyles.None, CultureInfo.InvariantCulture, out long whole))
        {
            return false;
        }

        long fraction = 0L;
        if (!fractionPart.IsEmpty &&
            !long.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out fraction))
        {
            return false;
        }

        // Pad "1.5" to 150 rather than 15 for a two-decimal currency.
        for (int i = fractionPart.Length; i < digits; i++)
        {
            fraction *= 10L;
        }

        long scale = currency.MinorUnitScale;
        if (whole > MaxMinorUnits / scale)
        {
            return false;
        }

        long minorUnits = (whole * scale) + fraction;
        money = new Money(negative ? -minorUnits : minorUnits, currency);
        return true;
    }

    /// <summary>
    /// The amount in major units. Only for presentation and reporting: the ledger never
    /// round-trips through <see cref="decimal"/>.
    /// </summary>
    public decimal ToDecimal() => (decimal)_minorUnits / _currency.MinorUnitScale;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Money other) => _minorUnits == other._minorUnits && _currency == other._currency;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_minorUnits, _currency.PackedValue);

    /// <summary>
    /// Orders two amounts of the same currency. Comparing across currencies is a
    /// programming error and throws, because there is no meaningful ordering.
    /// </summary>
    public int CompareTo(Money other)
    {
        if (_currency != other._currency)
        {
            throw new InvalidOperationException(
                $"Cannot compare amounts in {_currency} and {other._currency}.");
        }

        return _minorUnits.CompareTo(other._minorUnits);
    }

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public override string ToString()
    {
        string amount = ToDecimal().ToString(
            _currency.MinorUnitDigits == 0 ? "F0" : "F2",
            CultureInfo.InvariantCulture);

        return string.Concat(amount, " ", _currency.ToString());
    }
}
