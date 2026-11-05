using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class CurrencyTests
{
    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    [InlineData("UsD")]
    public void TryParse_is_case_insensitive(string code)
    {
        Assert.True(Currency.TryParse(code, out Currency currency));
        Assert.Equal(Currency.Usd, currency);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("US1")]
    [InlineData("U S")]
    [InlineData("")]
    public void TryParse_rejects_anything_but_three_letters(string code)
        => Assert.False(Currency.TryParse(code, out _));

    /// <summary>
    /// Parsing and policy are separate: a well-formed code FlowPay does not settle in
    /// parses successfully and fails the support check, so the API can tell "that is not
    /// a currency" from "we do not handle that currency".
    /// </summary>
    [Fact]
    public void A_wellformed_but_unsupported_code_parses_and_fails_the_support_check()
    {
        Assert.True(Currency.TryParse("ZWL", out Currency currency));
        Assert.False(currency.IsSupported);
        Assert.False(Currency.TryParseSupported("ZWL", out _));
    }

    [Fact]
    public void ToString_round_trips_through_TryParse()
    {
        Currency[] all =
        [
            Currency.Usd, Currency.Eur, Currency.Gbp, Currency.Inr, Currency.Jpy,
            Currency.Aud, Currency.Cad, Currency.Chf, Currency.Sgd, Currency.Aed,
        ];

        foreach (Currency currency in all)
        {
            Assert.True(Currency.TryParse(currency.ToString(), out Currency parsed));
            Assert.Equal(currency, parsed);
        }
    }

    [Fact]
    public void PackedValue_round_trips_through_the_database_representation()
    {
        Currency original = Currency.Gbp;
        Currency restored = Currency.FromPackedValue(original.PackedValue);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void TryFormat_writes_the_code_without_allocating()
    {
        Span<char> buffer = stackalloc char[3];

        Assert.True(Currency.Eur.TryFormat(buffer, out int written));
        Assert.Equal(3, written);
        Assert.Equal("EUR", new string(buffer));
    }

    [Fact]
    public void TryFormat_fails_rather_than_truncating_into_a_short_buffer()
    {
        Span<char> tooSmall = stackalloc char[2];

        Assert.False(Currency.Eur.TryFormat(tooSmall, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void MinorUnitDigits_reflects_the_currency_exponent()
    {
        Assert.Equal(2, Currency.Usd.MinorUnitDigits);
        Assert.Equal(100L, Currency.Usd.MinorUnitScale);

        Assert.Equal(0, Currency.Jpy.MinorUnitDigits);
        Assert.Equal(1L, Currency.Jpy.MinorUnitScale);
    }

    [Fact]
    public void The_default_value_is_not_a_usable_currency()
    {
        Assert.True(Currency.None.IsNone);
        Assert.False(Currency.None.IsSupported);
    }
}
