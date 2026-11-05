using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class PaymentIntentTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly Money Amount = new(129_950L, Currency.Usd);
    private static readonly Money Fee = new(3_800L, Currency.Usd);

    private static PaymentIntent NewPending() => PaymentIntent.Create(
        PaymentId.New(),
        AccountId.New(),
        AccountId.New(),
        Amount,
        PaymentMethod.Card,
        "ORDER-1",
        "Test payment",
        Now).Value;

    private static PaymentIntent NewCaptured()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();
        payment.TryCapture(Fee, Now).EnsureSuccess();
        return payment;
    }

    [Fact]
    public void A_new_payment_starts_pending_with_nothing_refunded()
    {
        PaymentIntent payment = NewPending();

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(0L, payment.RefundedMinorUnits);
        Assert.Equal(0L, payment.FeeMinorUnits);
        Assert.Null(payment.AuthorizedAt);
        Assert.Null(payment.CapturedAt);
        Assert.False(payment.IsTerminal);
        Assert.False(payment.IsSettled);
    }

    [Fact]
    public void Create_rejects_a_payment_to_the_same_account()
    {
        AccountId account = AccountId.New();

        DomainResult<PaymentIntent> result = PaymentIntent.Create(
            PaymentId.New(), account, account, Amount, PaymentMethod.Card, "", "", Now);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Create_rejects_a_non_positive_amount()
    {
        DomainResult<PaymentIntent> result = PaymentIntent.Create(
            PaymentId.New(), AccountId.New(), AccountId.New(),
            new Money(0L, Currency.Usd), PaymentMethod.Card, "", "", Now);

        Assert.Equal(DomainError.AmountNotPositive, result.Error);
    }

    [Fact]
    public void Authorize_starts_the_authorization_clock()
    {
        PaymentIntent payment = NewPending();

        Assert.True(payment.TryAuthorize(Now).IsSuccess);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.Equal(Now, payment.AuthorizedAt);
        Assert.Equal(Now + PaymentIntent.AuthorizationWindow, payment.AuthorizationExpiresAt);
    }

    [Fact]
    public void Capture_records_the_fee_and_the_capture_time()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Assert.True(payment.TryCapture(Fee, Now).IsSuccess);
        Assert.Equal(PaymentStatus.Captured, payment.Status);
        Assert.Equal(Fee.MinorUnits, payment.FeeMinorUnits);
        Assert.Equal(Now, payment.CapturedAt);
        Assert.True(payment.IsSettled);
    }

    [Fact]
    public void Capture_rejects_a_payment_that_was_never_authorized()
    {
        PaymentIntent payment = NewPending();

        Assert.Equal(DomainError.TransitionNotPermitted, payment.TryCapture(Fee, Now).Error);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public void Capture_rejects_a_fee_larger_than_the_amount()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Money excessiveFee = new(Amount.MinorUnits + 1L, Currency.Usd);

        Assert.Equal(DomainError.AmountExceedsLimit, payment.TryCapture(excessiveFee, Now).Error);
    }

    [Fact]
    public void Capture_rejects_a_fee_in_a_different_currency()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Assert.Equal(
            DomainError.CurrencyMismatch,
            payment.TryCapture(new Money(100L, Currency.Eur), Now).Error);
    }

    /// <summary>
    /// A rejected transition must leave the aggregate byte-for-byte unchanged, because
    /// the caller may continue using it — and because the benchmarks assert the rejection
    /// path allocates nothing, which only holds if it does no work.
    /// </summary>
    [Fact]
    public void A_rejected_transition_leaves_the_aggregate_untouched()
    {
        PaymentIntent payment = NewCaptured();

        PaymentStatus status = payment.Status;
        long refunded = payment.RefundedMinorUnits;
        DateTimeOffset updated = payment.UpdatedAt;

        Assert.True(payment.TryVoid(Now.AddHours(1)).IsFailure);

        Assert.Equal(status, payment.Status);
        Assert.Equal(refunded, payment.RefundedMinorUnits);
        Assert.Equal(updated, payment.UpdatedAt);
    }

    [Fact]
    public void A_partial_refund_moves_the_payment_to_PartiallyRefunded()
    {
        PaymentIntent payment = NewCaptured();
        Money partial = new(10_000L, Currency.Usd);

        Assert.True(payment.TryRefund(partial, Now, out PaymentStatus resulting).IsSuccess);

        Assert.Equal(PaymentStatus.PartiallyRefunded, resulting);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Equal(10_000L, payment.RefundedMinorUnits);
        Assert.Equal(Amount.MinorUnits - 10_000L, payment.RefundableAmount.MinorUnits);
    }

    [Fact]
    public void Refunding_the_full_amount_moves_the_payment_to_Refunded()
    {
        PaymentIntent payment = NewCaptured();

        Assert.True(payment.TryRefund(Amount, Now, out PaymentStatus resulting).IsSuccess);

        Assert.Equal(PaymentStatus.Refunded, resulting);
        Assert.True(payment.IsTerminal);
        Assert.Equal(0L, payment.RefundableAmount.MinorUnits);
    }

    /// <summary>
    /// Successive partial refunds that exactly exhaust the amount must land on Refunded,
    /// not leave the payment PartiallyRefunded with zero headroom.
    /// </summary>
    [Fact]
    public void Successive_partial_refunds_close_out_the_payment()
    {
        PaymentIntent payment = NewCaptured();
        Money chunk = new(Amount.MinorUnits / 2L, Currency.Usd);
        Money remainder = new(Amount.MinorUnits - chunk.MinorUnits, Currency.Usd);

        Assert.True(payment.TryRefund(chunk, Now, out _).IsSuccess);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);

        Assert.True(payment.TryRefund(remainder, Now, out _).IsSuccess);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(Amount.MinorUnits, payment.RefundedMinorUnits);
    }

    /// <summary>
    /// The invariant the concurrent refund stress test exists to break: total refunds can
    /// never exceed the captured amount.
    /// </summary>
    [Fact]
    public void A_refund_beyond_the_captured_amount_is_rejected()
    {
        PaymentIntent payment = NewCaptured();
        Money tooMuch = new(Amount.MinorUnits + 1L, Currency.Usd);

        Assert.Equal(
            DomainError.RefundExceedsCapturedAmount,
            payment.TryRefund(tooMuch, Now, out _).Error);

        Assert.Equal(0L, payment.RefundedMinorUnits);
        Assert.Equal(PaymentStatus.Captured, payment.Status);
    }

    [Fact]
    public void A_second_refund_that_would_exceed_the_total_is_rejected()
    {
        PaymentIntent payment = NewCaptured();
        Money mostOfIt = new(Amount.MinorUnits - 100L, Currency.Usd);

        Assert.True(payment.TryRefund(mostOfIt, Now, out _).IsSuccess);

        Assert.Equal(
            DomainError.RefundExceedsCapturedAmount,
            payment.TryRefund(new Money(101L, Currency.Usd), Now, out _).Error);

        Assert.Equal(mostOfIt.MinorUnits, payment.RefundedMinorUnits);
    }

    [Fact]
    public void A_refund_is_rejected_before_capture()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Assert.Equal(
            DomainError.PaymentNotCaptured,
            payment.TryRefund(new Money(100L, Currency.Usd), Now, out _).Error);
    }

    [Fact]
    public void A_refund_in_the_wrong_currency_is_rejected()
    {
        PaymentIntent payment = NewCaptured();

        Assert.Equal(
            DomainError.CurrencyMismatch,
            payment.TryRefund(new Money(100L, Currency.Eur), Now, out _).Error);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    public void A_non_positive_refund_is_rejected(long minorUnits)
    {
        PaymentIntent payment = NewCaptured();

        Assert.Equal(
            DomainError.AmountNotPositive,
            payment.TryRefund(new Money(minorUnits, Currency.Usd), Now, out _).Error);
    }

    /// <summary>
    /// Refund arithmetic adds a client-supplied amount to a stored total, so the overflow
    /// path has to report rather than wrap. A partial refund is applied first: with a
    /// refunded total of zero, adding <see cref="long.MaxValue"/> does not overflow and
    /// is correctly rejected as an over-refund instead.
    /// </summary>
    [Fact]
    public void A_refund_that_would_overflow_the_running_total_is_rejected()
    {
        PaymentIntent payment = NewCaptured();
        payment.TryRefund(new Money(1L, Currency.Usd), Now, out _).EnsureSuccess();

        Assert.Equal(
            DomainError.ArithmeticOverflow,
            payment.TryRefund(new Money(long.MaxValue, Currency.Usd), Now, out _).Error);
    }

    /// <summary>
    /// With nothing refunded yet, an absurdly large refund does not overflow; it is
    /// simply more than was captured.
    /// </summary>
    [Fact]
    public void A_huge_refund_against_an_untouched_payment_is_an_over_refund()
    {
        PaymentIntent payment = NewCaptured();

        Assert.Equal(
            DomainError.RefundExceedsCapturedAmount,
            payment.TryRefund(new Money(long.MaxValue, Currency.Usd), Now, out _).Error);
    }

    [Fact]
    public void Void_releases_an_authorization()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Assert.True(payment.TryVoid(Now).IsSuccess);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.True(payment.IsTerminal);
    }

    /// <summary>
    /// Expiry must not fire before the window has actually elapsed, or a clock skew would
    /// cancel live authorizations.
    /// </summary>
    [Fact]
    public void Expiry_is_rejected_inside_the_authorization_window()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        Assert.Equal(
            DomainError.TransitionNotPermitted,
            payment.TryExpire(Now.AddDays(1)).Error);

        Assert.Equal(PaymentStatus.Authorized, payment.Status);
    }

    [Fact]
    public void Expiry_succeeds_once_the_window_has_elapsed()
    {
        PaymentIntent payment = NewPending();
        payment.TryAuthorize(Now).EnsureSuccess();

        DateTimeOffset afterWindow = Now + PaymentIntent.AuthorizationWindow + TimeSpan.FromSeconds(1);

        Assert.True(payment.TryExpire(afterWindow).IsSuccess);
        Assert.Equal(PaymentStatus.Expired, payment.Status);
        Assert.Equal(PaymentFailureReason.AuthorizationExpired, payment.FailureReason);
    }

    [Fact]
    public void Fail_records_the_reason()
    {
        PaymentIntent payment = NewPending();

        Assert.True(payment.TryFail(PaymentFailureReason.InsufficientFunds, Now).IsSuccess);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(PaymentFailureReason.InsufficientFunds, payment.FailureReason);
        Assert.True(payment.IsTerminal);
    }

    [Fact]
    public void A_terminal_payment_rejects_every_further_transition()
    {
        PaymentIntent payment = NewPending();
        payment.TryFail(PaymentFailureReason.RiskRejected, Now).EnsureSuccess();

        Assert.Equal(DomainError.TerminalState, payment.TryAuthorize(Now).Error);
        Assert.Equal(DomainError.TerminalState, payment.TryCapture(Fee, Now).Error);
        Assert.Equal(DomainError.TerminalState, payment.TryVoid(Now).Error);
        Assert.Equal(DomainError.PaymentNotCaptured,
            payment.TryRefund(new Money(1L, Currency.Usd), Now, out _).Error);
    }
}
