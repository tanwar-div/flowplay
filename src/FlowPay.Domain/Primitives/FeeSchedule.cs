using System.Runtime.CompilerServices;

namespace FlowPay.Domain.Primitives;

/// <summary>
/// A processing-fee rule: a proportional component in basis points plus a fixed
/// component, clamped to an optional floor and ceiling.
/// </summary>
/// <remarks>
/// <para>
/// The whole computation is 64-bit integer arithmetic. <see cref="Money.MaxMinorUnits"/>
/// (10^12) bounds the amount and <see cref="MaxBasisPoints"/> (10,000) bounds the rate,
/// so the intermediate product cannot exceed 10^16 and stays well inside
/// <see cref="long"/>. That is what keeps fee calculation off the 128-bit and
/// <see cref="decimal"/> paths, both of which are an order of magnitude slower.
/// </para>
/// <para>
/// Rounding is half-to-even (banker's rounding). Always rounding half away from zero
/// would bias every fee upward by a fraction of a minor unit, which over millions of
/// transactions is a real and auditable discrepancy.
/// </para>
/// </remarks>
public readonly struct FeeSchedule
{
    /// <summary>100% expressed in basis points.</summary>
    public const int MaxBasisPoints = 10_000;

    private const long BasisPointDivisor = 10_000L;

    private readonly int _basisPoints;
    private readonly long _fixedMinorUnits;
    private readonly long _minimumMinorUnits;
    private readonly long _maximumMinorUnits;
    private readonly Currency _currency;

    public FeeSchedule(
        Currency currency,
        int basisPoints,
        long fixedMinorUnits = 0L,
        long minimumMinorUnits = 0L,
        long maximumMinorUnits = long.MaxValue)
    {
        if (currency.IsNone)
        {
            throw new ArgumentException("A fee schedule requires a currency.", nameof(currency));
        }

        if ((uint)basisPoints > MaxBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basisPoints), basisPoints, "Basis points must be between 0 and 10000.");
        }

        if (fixedMinorUnits < 0L || minimumMinorUnits < 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixedMinorUnits), "Fee components cannot be negative.");
        }

        if (maximumMinorUnits < minimumMinorUnits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMinorUnits), "The fee ceiling cannot be below the floor.");
        }

        _currency = currency;
        _basisPoints = basisPoints;
        _fixedMinorUnits = fixedMinorUnits;
        _minimumMinorUnits = minimumMinorUnits;
        _maximumMinorUnits = maximumMinorUnits;
    }

    public Currency Currency => _currency;

    public int BasisPoints => _basisPoints;

    public long FixedMinorUnits => _fixedMinorUnits;

    /// <summary>
    /// The standard card-present schedule used by the sample seed data: 2.9% + 30c.
    /// </summary>
    public static FeeSchedule Standard(Currency currency) => new(currency, 290, 30L);

    /// <summary>
    /// Computes the fee for <paramref name="amount"/>. Fails on currency mismatch or an
    /// amount outside the operational range, without throwing or allocating.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryComputeFee(in Money amount, out Money fee)
    {
        if (amount.Currency != _currency || !amount.IsWithinOperationalLimit())
        {
            fee = default;
            return false;
        }

        // Bounded above by 10^12 * 10^4 = 10^16, so no overflow check is needed here.
        long scaled = amount.MinorUnits * _basisPoints;

        long proportional = Math.DivRem(scaled, BasisPointDivisor, out long remainder);

        // Half-to-even: round up on a strict majority, and on an exact half only when
        // doing so lands on an even number of minor units.
        long twiceRemainder = remainder * 2L;
        if (twiceRemainder > BasisPointDivisor ||
            (twiceRemainder == BasisPointDivisor && (proportional & 1L) == 1L))
        {
            proportional++;
        }

        long total = proportional + _fixedMinorUnits;

        if (total < _minimumMinorUnits)
        {
            total = _minimumMinorUnits;
        }
        else if (total > _maximumMinorUnits)
        {
            total = _maximumMinorUnits;
        }

        // A fee can never exceed the amount it is charged against; otherwise the net
        // settlement would be negative and the ledger would need a funding source that
        // does not exist.
        if (total > amount.MinorUnits)
        {
            total = amount.MinorUnits;
        }

        fee = new Money(total, _currency);
        return true;
    }

    /// <summary>
    /// Computes the fee and the resulting net settlement amount in one pass.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryComputeSettlement(in Money amount, out Money fee, out Money net)
    {
        if (!TryComputeFee(amount, out fee))
        {
            net = default;
            return false;
        }

        return amount.TrySubtract(fee, out net);
    }
}
