using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void TryAdd_rejects_a_currency_mismatch()
    {
        Money usd = new(100L, Currency.Usd);
        Money eur = new(100L, Currency.Eur);

        Assert.False(usd.TryAdd(eur, out Money result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryAdd_sums_amounts_in_the_same_currency()
    {
        Money left = new(1_250L, Currency.Usd);
        Money right = new(799L, Currency.Usd);

        Assert.True(left.TryAdd(right, out Money sum));
        Assert.Equal(2_049L, sum.MinorUnits);
        Assert.Equal(Currency.Usd, sum.Currency);
    }

    /// <summary>
    /// Overflow is reported, not thrown, because the refund path adds a client-supplied
    /// amount to a stored total and an exception there would allocate on the hot path.
    /// </summary>
    [Fact]
    public void TryAdd_reports_overflow_instead_of_wrapping()
    {
        Money nearMax = new(long.MaxValue - 10L, Currency.Usd);
        Money overflowing = new(100L, Currency.Usd);

        Assert.False(nearMax.TryAdd(overflowing, out _));
    }

    [Fact]
    public void TrySubtract_reports_overflow_at_the_negative_boundary()
    {
        Money nearMin = new(long.MinValue + 10L, Currency.Usd);
        Money overflowing = new(100L, Currency.Usd);

        Assert.False(nearMin.TrySubtract(overflowing, out _));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(Money.MaxMinorUnits, true)]
    [InlineData(Money.MaxMinorUnits + 1L, false)]
    public void IsWithinOperationalLimit_bounds_the_accepted_range(long minorUnits, bool expected)
    {
        Money amount = new(minorUnits, Currency.Usd);

        Assert.Equal(expected, amount.IsWithinOperationalLimit());
    }

    [Theory]
    [InlineData("0.01", 1L)]
    [InlineData("1", 100L)]
    [InlineData("1.5", 150L)]
    [InlineData("1.50", 150L)]
    [InlineData("1250.75", 125_075L)]
    [InlineData("-12.34", -1_234L)]
    public void TryParseMajorUnits_scales_decimal_text_to_minor_units(string text, long expected)
    {
        Assert.True(Money.TryParseMajorUnits(text, Currency.Usd, out Money money));
        Assert.Equal(expected, money.MinorUnits);
    }

    [Theory]
    [InlineData("1.234")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    public void TryParseMajorUnits_rejects_malformed_input(string text)
        => Assert.False(Money.TryParseMajorUnits(text, Currency.Usd, out _));

    /// <summary>
    /// JPY has no minor unit, so a fractional amount is not a rounding question but an
    /// invalid amount.
    /// </summary>
    [Fact]
    public void TryParseMajorUnits_respects_a_zero_decimal_currency()
    {
        Assert.True(Money.TryParseMajorUnits("1250", Currency.Jpy, out Money yen));
        Assert.Equal(1_250L, yen.MinorUnits);

        Assert.False(Money.TryParseMajorUnits("1250.50", Currency.Jpy, out _));
    }

    /// <summary>
    /// The property that makes splitting safe: the shares must sum to exactly the
    /// original amount, with no minor unit created or lost to rounding.
    /// </summary>
    [Theory]
    [InlineData(100L, 3)]
    [InlineData(1L, 3)]
    [InlineData(129_950L, 7)]
    [InlineData(10L, 10)]
    [InlineData(999_983L, 13)]
    public void TryAllocate_distributes_the_exact_total(long minorUnits, int parts)
    {
        Money amount = new(minorUnits, Currency.Usd);
        Money[] shares = new Money[parts];

        Assert.True(amount.TryAllocate(parts, shares));

        long total = 0L;
        foreach (Money share in shares)
        {
            total += share.MinorUnits;
        }

        Assert.Equal(minorUnits, total);
    }

    [Fact]
    public void TryAllocate_spreads_the_remainder_one_unit_at_a_time()
    {
        Money amount = new(100L, Currency.Usd);
        Money[] shares = new Money[3];

        Assert.True(amount.TryAllocate(3, shares));

        Assert.Equal(34L, shares[0].MinorUnits);
        Assert.Equal(33L, shares[1].MinorUnits);
        Assert.Equal(33L, shares[2].MinorUnits);
    }

    [Fact]
    public void CompareTo_throws_across_currencies_because_there_is_no_ordering()
    {
        Money usd = new(100L, Currency.Usd);
        Money eur = new(100L, Currency.Eur);

        Assert.Throws<InvalidOperationException>(() => usd.CompareTo(eur));
    }

    [Fact]
    public void Equality_distinguishes_the_same_amount_in_different_currencies()
    {
        Money usd = new(100L, Currency.Usd);
        Money eur = new(100L, Currency.Eur);

        Assert.NotEqual(usd, eur);
        Assert.Equal(usd, new Money(100L, Currency.Usd));
    }
}
