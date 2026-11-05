using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class FeeScheduleTests
{
    [Fact]
    public void The_standard_schedule_computes_two_point_nine_percent_plus_thirty_cents()
    {
        FeeSchedule schedule = FeeSchedule.Standard(Currency.Usd);
        Money amount = new(10_000L, Currency.Usd);

        Assert.True(schedule.TryComputeFee(amount, out Money fee));

        // 10000 * 290 / 10000 = 290, plus the 30 fixed component.
        Assert.Equal(320L, fee.MinorUnits);
    }

    /// <summary>
    /// Banker's rounding, not round-half-up. Rounding every exact half away from zero
    /// would bias fees upward by a fraction of a minor unit on every transaction, which
    /// over millions of payments is a real and auditable discrepancy.
    /// </summary>
    [Theory]
    // 100 * 5000 / 10000 = 50 exactly: no rounding needed.
    [InlineData(100L, 5_000, 50L)]
    // 1 * 5000 / 10000 = 0.5, a tie. Rounds to 0, the even neighbour.
    [InlineData(1L, 5_000, 0L)]
    // 3 * 5000 / 10000 = 1.5, a tie. Rounds to 2, the even neighbour.
    [InlineData(3L, 5_000, 2L)]
    // 1 * 6000 / 10000 = 0.6, a strict majority. Rounds up.
    [InlineData(1L, 6_000, 1L)]
    // 1 * 4000 / 10000 = 0.4, below half. Rounds down.
    [InlineData(1L, 4_000, 0L)]
    public void Proportional_fees_round_half_to_even(
        long amountMinorUnits,
        int basisPoints,
        long expectedFee)
    {
        FeeSchedule schedule = new(Currency.Usd, basisPoints);
        Money amount = new(amountMinorUnits, Currency.Usd);

        Assert.True(schedule.TryComputeFee(amount, out Money fee));
        Assert.Equal(expectedFee, fee.MinorUnits);
    }

    [Fact]
    public void A_fee_floor_is_applied()
    {
        FeeSchedule schedule = new(Currency.Usd, basisPoints: 100, minimumMinorUnits: 50L);

        Assert.True(schedule.TryComputeFee(new Money(1_000L, Currency.Usd), out Money fee));
        Assert.Equal(50L, fee.MinorUnits);
    }

    [Fact]
    public void A_fee_ceiling_is_applied()
    {
        FeeSchedule schedule = new(Currency.Usd, basisPoints: 1_000, maximumMinorUnits: 500L);

        Assert.True(schedule.TryComputeFee(new Money(100_000L, Currency.Usd), out Money fee));
        Assert.Equal(500L, fee.MinorUnits);
    }

    /// <summary>
    /// A fee larger than the amount would make the net settlement negative, which needs a
    /// funding source that does not exist. It is clamped instead.
    /// </summary>
    [Fact]
    public void A_fee_never_exceeds_the_amount_it_is_charged_against()
    {
        FeeSchedule schedule = new(Currency.Usd, basisPoints: 290, fixedMinorUnits: 30L);
        Money tiny = new(10L, Currency.Usd);

        Assert.True(schedule.TryComputeFee(tiny, out Money fee));
        Assert.Equal(10L, fee.MinorUnits);

        Assert.True(schedule.TryComputeSettlement(tiny, out _, out Money net));
        Assert.Equal(0L, net.MinorUnits);
    }

    [Fact]
    public void TryComputeFee_rejects_a_currency_mismatch()
    {
        FeeSchedule schedule = FeeSchedule.Standard(Currency.Usd);

        Assert.False(schedule.TryComputeFee(new Money(1_000L, Currency.Eur), out _));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    [InlineData(Money.MaxMinorUnits + 1L)]
    public void TryComputeFee_rejects_amounts_outside_the_operational_range(long minorUnits)
    {
        FeeSchedule schedule = FeeSchedule.Standard(Currency.Usd);

        Assert.False(schedule.TryComputeFee(new Money(minorUnits, Currency.Usd), out _));
    }

    /// <summary>
    /// The arithmetic must stay in 64-bit integers at the top of the accepted range.
    /// This is the case the amount cap exists to guarantee.
    /// </summary>
    [Fact]
    public void The_maximum_amount_at_the_maximum_rate_does_not_overflow()
    {
        FeeSchedule schedule = new(Currency.Usd, FeeSchedule.MaxBasisPoints);
        Money max = new(Money.MaxMinorUnits, Currency.Usd);

        Assert.True(schedule.TryComputeFee(max, out Money fee));
        Assert.Equal(Money.MaxMinorUnits, fee.MinorUnits);
    }

    [Fact]
    public void Settlement_splits_the_amount_into_fee_and_net_with_no_remainder()
    {
        FeeSchedule schedule = FeeSchedule.Standard(Currency.Usd);
        Money amount = new(129_950L, Currency.Usd);

        Assert.True(schedule.TryComputeSettlement(amount, out Money fee, out Money net));
        Assert.Equal(amount.MinorUnits, fee.MinorUnits + net.MinorUnits);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(FeeSchedule.MaxBasisPoints + 1)]
    public void The_constructor_rejects_an_out_of_range_rate(int basisPoints)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeeSchedule(Currency.Usd, basisPoints));

    [Fact]
    public void The_constructor_rejects_a_ceiling_below_the_floor()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new FeeSchedule(
                Currency.Usd, 100, minimumMinorUnits: 500L, maximumMinorUnits: 100L));
}
