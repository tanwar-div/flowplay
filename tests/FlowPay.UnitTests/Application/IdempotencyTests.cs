using FlowPay.Application.Idempotency;
using FlowPay.Application.Payments;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Application;

public class IdempotencyKeyTests
{
    [Theory]
    [InlineData("idem-7f3a91c4")]
    [InlineData("ORDER_2026.11.05:001")]
    [InlineData("a")]
    [InlineData("0123456789")]
    public void Well_formed_keys_parse(string raw)
    {
        Assert.True(IdempotencyKey.TryParse(
            raw, out IdempotencyKey key, out IdempotencyKeyError error));

        Assert.Equal(IdempotencyKeyError.None, error);
        Assert.False(key.IsEmpty);
    }

    [Fact]
    public void An_empty_key_is_rejected()
    {
        Assert.False(IdempotencyKey.TryParse("", out _, out IdempotencyKeyError error));
        Assert.Equal(IdempotencyKeyError.Empty, error);
    }

    [Fact]
    public void A_key_at_the_length_limit_is_accepted_and_one_over_is_not()
    {
        Assert.True(IdempotencyKey.TryParse(
            new string('a', IdempotencyKey.MaxLength), out _, out _));

        Assert.False(IdempotencyKey.TryParse(
            new string('a', IdempotencyKey.MaxLength + 1), out _, out IdempotencyKeyError error));

        Assert.Equal(IdempotencyKeyError.TooLong, error);
    }

    /// <summary>
    /// Restricting the character set means keys can be echoed into logs and error bodies
    /// without escaping, and rules out whitespace variants that would produce two
    /// "different" keys for the same intent.
    /// </summary>
    [Theory]
    [InlineData("has spaces")]
    [InlineData("has\nnewline")]
    [InlineData("has/slash")]
    [InlineData("has#hash")]
    [InlineData("has-accent-e")]
    public void Keys_outside_the_accepted_character_set_are_rejected(string raw)
    {
        // The last case is the control: it is inside the accepted set and must parse.
        bool parsed = IdempotencyKey.TryParse(raw, out _, out IdempotencyKeyError error);

        if (raw == "has-accent-e")
        {
            Assert.True(parsed);
            return;
        }

        Assert.False(parsed);
        Assert.Equal(IdempotencyKeyError.InvalidCharacter, error);
    }

    [Fact]
    public void A_non_ascii_character_is_rejected()
    {
        Assert.False(IdempotencyKey.TryParse(
            "café-key", out _, out IdempotencyKeyError error));

        Assert.Equal(IdempotencyKeyError.InvalidCharacter, error);
    }

    [Fact]
    public void The_same_key_fingerprints_identically()
    {
        IdempotencyKey.TryParse("idem-7f3a91c4", out IdempotencyKey first, out _);
        IdempotencyKey.TryParse("idem-7f3a91c4", out IdempotencyKey second, out _);

        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Different_keys_fingerprint_differently()
    {
        IdempotencyKey.TryParse("idem-7f3a91c4", out IdempotencyKey first, out _);
        IdempotencyKey.TryParse("idem-7f3a91c5", out IdempotencyKey second, out _);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FromTrusted_matches_TryParse_for_a_valid_key()
    {
        IdempotencyKey.TryParse("idem-7f3a91c4", out IdempotencyKey parsed, out _);
        IdempotencyKey trusted = IdempotencyKey.FromTrusted("idem-7f3a91c4");

        Assert.Equal(parsed, trusted);
    }

    [Fact]
    public void Stripes_stay_inside_the_requested_range()
    {
        for (int i = 0; i < 500; i++)
        {
            IdempotencyKey.TryParse($"key-{i}", out IdempotencyKey key, out _);
            Assert.InRange(key.GetStripe(64), 0, 63);
        }
    }
}

public class RequestFingerprintTests
{
    private static AuthorizePaymentCommand Command(
        long amountMinorUnits = 129_950L,
        string currency = "USD",
        string reference = "ORDER-1",
        string description = "Test payment",
        bool capture = true) => new(
            Guid.Parse("0199e000-0000-7000-8000-000000000001"),
            Guid.Parse("0199e000-0000-7000-8000-000000000002"),
            amountMinorUnits,
            currency,
            PaymentMethod.Card,
            reference,
            description,
            capture);

    [Fact]
    public void The_fingerprint_is_the_expected_width()
        => Assert.Equal(
            RequestFingerprint.SizeInBytes,
            RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(Command())).Length);

    [Fact]
    public void Identical_requests_fingerprint_identically()
    {
        byte[] first = RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(Command()));
        byte[] second = RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(Command()));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// The point of the fingerprint: reusing a key for a materially different request has
    /// to be detectable, or a client could be handed the first request's response and
    /// believe it moved money it did not.
    /// </summary>
    [Theory]
    [InlineData(129_951L, "USD", "ORDER-1", true)]
    [InlineData(129_950L, "EUR", "ORDER-1", true)]
    [InlineData(129_950L, "USD", "ORDER-2", true)]
    [InlineData(129_950L, "USD", "ORDER-1", false)]
    public void Any_change_to_a_significant_field_changes_the_fingerprint(
        long amount,
        string currency,
        string reference,
        bool capture)
    {
        byte[] baseline = RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(Command()));

        byte[] changed = RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(
            Command(amount, currency, reference, capture: capture)));

        Assert.NotEqual(baseline, changed);
    }

    /// <summary>
    /// Currency case must not change the fingerprint, or a client that switched from
    /// <c>usd</c> to <c>USD</c> between a request and its retry would be told its key was
    /// reused.
    /// </summary>
    [Fact]
    public void Currency_case_does_not_change_the_fingerprint()
    {
        byte[] upper = RequestFingerprint.Compute(
            CanonicalRequest.ForAuthorize(Command(currency: "USD")));

        byte[] lower = RequestFingerprint.Compute(
            CanonicalRequest.ForAuthorize(Command(currency: "usd")));

        Assert.Equal(upper, lower);
    }

    [Fact]
    public void Different_operations_on_the_same_resource_fingerprint_differently()
    {
        Guid paymentId = Guid.Parse("0199e000-0000-7000-8000-000000000003");

        byte[] capture = RequestFingerprint.Compute(
            CanonicalRequest.ForCapture(new CapturePaymentCommand(paymentId)));

        byte[] voided = RequestFingerprint.Compute(
            CanonicalRequest.ForVoid(new VoidPaymentCommand(paymentId)));

        Assert.NotEqual(capture, voided);
    }

    [Fact]
    public void Refund_fingerprints_distinguish_amount_and_reason()
    {
        Guid paymentId = Guid.Parse("0199e000-0000-7000-8000-000000000004");

        byte[] baseline = RequestFingerprint.Compute(CanonicalRequest.ForRefund(
            new RefundPaymentCommand(paymentId, 1_000L, "USD", "customer request")));

        byte[] differentAmount = RequestFingerprint.Compute(CanonicalRequest.ForRefund(
            new RefundPaymentCommand(paymentId, 2_000L, "USD", "customer request")));

        byte[] differentReason = RequestFingerprint.Compute(CanonicalRequest.ForRefund(
            new RefundPaymentCommand(paymentId, 1_000L, "USD", "fraud")));

        Assert.NotEqual(baseline, differentAmount);
        Assert.NotEqual(baseline, differentReason);
    }

    /// <summary>
    /// The canonical form has a heap fallback for long inputs; this drives it past the
    /// stack budget to make sure the result is still a full fingerprint.
    /// </summary>
    [Fact]
    public void A_long_canonical_form_fingerprints_without_truncating()
    {
        AuthorizePaymentCommand longForm = Command(
            reference: new string('r', 64),
            description: new string('d', 512));

        byte[] fingerprint = RequestFingerprint.Compute(
            CanonicalRequest.ForAuthorize(longForm));

        Assert.Equal(RequestFingerprint.SizeInBytes, fingerprint.Length);

        AuthorizePaymentCommand slightlyDifferent = Command(
            reference: new string('r', 64),
            description: new string('d', 511) + "e");

        Assert.NotEqual(
            fingerprint,
            RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(slightlyDifferent)));
    }

    /// <summary>
    /// Idempotency scopes keep one key usable across different operations on the same
    /// business transaction, so a client that reuses its key for authorize and then
    /// capture is not told its capture is a duplicate.
    /// </summary>
    [Fact]
    public void Idempotency_scopes_are_distinct_per_endpoint()
    {
        string[] scopes =
        [
            IdempotencyScopes.AuthorizePayment,
            IdempotencyScopes.CapturePayment,
            IdempotencyScopes.RefundPayment,
            IdempotencyScopes.VoidPayment,
        ];

        Assert.Equal(scopes.Length, scopes.Distinct(StringComparer.Ordinal).Count());
    }
}
