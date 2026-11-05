using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class PaymentValidatorTests
{
    private static readonly AccountId Source = AccountId.New();
    private static readonly AccountId Destination = AccountId.New();

    private static PaymentValidationResult Validate(
        AccountId? source = null,
        AccountId? destination = null,
        long minorUnits = 129_950L,
        Currency? currency = null,
        PaymentMethod method = PaymentMethod.Card,
        string reference = "ORDER-1",
        string description = "Test",
        bool hasIdempotencyKey = true)
    {
        PaymentValidationRequest request = new(
            source ?? Source,
            destination ?? Destination,
            new Money(minorUnits, currency ?? Currency.Usd),
            method,
            reference,
            description,
            hasIdempotencyKey);

        return PaymentValidator.Validate(request);
    }

    [Fact]
    public void A_well_formed_request_passes()
    {
        PaymentValidationResult result = Validate();

        Assert.True(result.IsValid);
        Assert.Equal(PaymentValidationFailures.None, result.Failures);
        Assert.Equal(0, result.FailureCount);
    }

    [Fact]
    public void A_missing_source_account_is_reported()
        => Assert.True(Validate(source: AccountId.Empty)
            .Has(PaymentValidationFailures.MissingSourceAccount));

    [Fact]
    public void A_missing_destination_account_is_reported()
        => Assert.True(Validate(destination: AccountId.Empty)
            .Has(PaymentValidationFailures.MissingDestinationAccount));

    [Fact]
    public void A_payment_to_the_same_account_is_reported()
        => Assert.True(Validate(source: Source, destination: Source)
            .Has(PaymentValidationFailures.SameSourceAndDestination));

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_non_positive_amount_is_reported(long minorUnits)
        => Assert.True(Validate(minorUnits: minorUnits)
            .Has(PaymentValidationFailures.AmountNotPositive));

    [Fact]
    public void An_amount_above_the_global_limit_is_reported()
        => Assert.True(Validate(minorUnits: Money.MaxMinorUnits + 1L)
            .Has(PaymentValidationFailures.AmountExceedsLimit));

    /// <summary>
    /// Per-method limits differ, so the same amount can be valid for a bank transfer and
    /// rejected for a wallet.
    /// </summary>
    [Fact]
    public void Per_method_limits_are_enforced_independently()
    {
        long overWalletLimit = PaymentValidator.WalletLimitMinorUnits + 1L;

        Assert.True(Validate(minorUnits: overWalletLimit, method: PaymentMethod.Wallet)
            .Has(PaymentValidationFailures.AmountExceedsMethodLimit));

        Assert.True(Validate(minorUnits: overWalletLimit, method: PaymentMethod.BankTransfer)
            .IsValid);
    }

    [Fact]
    public void An_unspecified_payment_method_is_reported()
        => Assert.True(Validate(method: PaymentMethod.Unspecified)
            .Has(PaymentValidationFailures.UnspecifiedPaymentMethod));

    [Fact]
    public void A_missing_currency_is_reported()
        => Assert.True(Validate(currency: Currency.None)
            .Has(PaymentValidationFailures.MissingCurrency));

    [Fact]
    public void An_unsupported_currency_is_reported()
    {
        Currency.TryParse("ZWL", out Currency unsupported);

        Assert.True(Validate(currency: unsupported)
            .Has(PaymentValidationFailures.UnsupportedCurrency));
    }

    [Fact]
    public void An_over_length_reference_is_reported()
        => Assert.True(Validate(reference: new string('a', PaymentValidator.MaxReferenceLength + 1))
            .Has(PaymentValidationFailures.ReferenceTooLong));

    [Fact]
    public void An_over_length_description_is_reported()
        => Assert.True(Validate(description: new string('a', PaymentValidator.MaxDescriptionLength + 1))
            .Has(PaymentValidationFailures.DescriptionTooLong));

    /// <summary>
    /// Control characters in a reference would corrupt log lines and CSV exports
    /// downstream of the ledger. Both the C0 range and DEL plus the C1 block are rejected.
    /// </summary>
    [Theory]
    [InlineData("ORDER\n1")]
    [InlineData("ORDER\t1")]
    [InlineData("ORDER\r1")]
    [InlineData("ORDER\u00011")]
    [InlineData("ORDER\u007F1")]
    [InlineData("ORDER\u009F1")]
    public void Control_characters_in_a_reference_are_reported(string reference)
        => Assert.True(Validate(reference: reference)
            .Has(PaymentValidationFailures.ReferenceHasControlCharacters));

    [Fact]
    public void An_ordinary_reference_with_punctuation_is_accepted()
        => Assert.True(Validate(reference: "ORDER-2026/114238_A.1").IsValid);

    [Fact]
    public void A_missing_idempotency_key_is_reported()
        => Assert.True(Validate(hasIdempotencyKey: false)
            .Has(PaymentValidationFailures.MissingIdempotencyKey));

    /// <summary>
    /// All failures are reported in one pass, so the client gets a complete answer rather
    /// than discovering its problems one request at a time.
    /// </summary>
    [Fact]
    public void Every_failure_is_reported_in_a_single_pass()
    {
        PaymentValidationRequest request = new(
            AccountId.Empty,
            AccountId.Empty,
            default,
            PaymentMethod.Unspecified,
            new string('a', PaymentValidator.MaxReferenceLength + 1),
            new string('b', PaymentValidator.MaxDescriptionLength + 1),
            hasIdempotencyKey: false);

        PaymentValidationResult result = PaymentValidator.Validate(request);

        Assert.True(result.Has(PaymentValidationFailures.MissingSourceAccount));
        Assert.True(result.Has(PaymentValidationFailures.MissingDestinationAccount));
        Assert.True(result.Has(PaymentValidationFailures.MissingCurrency));
        Assert.True(result.Has(PaymentValidationFailures.AmountNotPositive));
        Assert.True(result.Has(PaymentValidationFailures.UnspecifiedPaymentMethod));
        Assert.True(result.Has(PaymentValidationFailures.ReferenceTooLong));
        Assert.True(result.Has(PaymentValidationFailures.DescriptionTooLong));
        Assert.True(result.Has(PaymentValidationFailures.MissingIdempotencyKey));

        Assert.Equal(8, result.FailureCount);
    }

    [Fact]
    public void Failure_codes_are_projected_for_every_reported_failure()
    {
        PaymentValidationRequest request = new(
            AccountId.Empty, AccountId.Empty, default, PaymentMethod.Unspecified,
            "", "", hasIdempotencyKey: false);

        PaymentValidationResult result = PaymentValidator.Validate(request);

        string[] codes = new string[result.FailureCount];
        int written = result.GetErrorCodes(codes);

        Assert.Equal(result.FailureCount, written);
        Assert.Contains("source_account_missing", codes);
        Assert.Contains("idempotency_key_missing", codes);
        Assert.All(codes, code => Assert.False(string.IsNullOrEmpty(code)));
    }

    [Fact]
    public void Account_currency_mismatches_are_reported_against_the_loaded_accounts()
    {
        PaymentValidationRequest request = new(
            Source, Destination, new Money(1_000L, Currency.Usd), PaymentMethod.Card,
            "", "", hasIdempotencyKey: true);

        PaymentValidationResult result = PaymentValidator.ValidateAgainstAccounts(
            request, Currency.Eur, Currency.Gbp);

        Assert.True(result.Has(PaymentValidationFailures.SourceCurrencyMismatch));
        Assert.True(result.Has(PaymentValidationFailures.DestinationCurrencyMismatch));
    }

    [Fact]
    public void Matching_account_currencies_pass_the_account_check()
    {
        PaymentValidationRequest request = new(
            Source, Destination, new Money(1_000L, Currency.Usd), PaymentMethod.Card,
            "", "", hasIdempotencyKey: true);

        Assert.True(PaymentValidator
            .ValidateAgainstAccounts(request, Currency.Usd, Currency.Usd)
            .IsValid);
    }
}
