using FlowPay.Domain.Ledger;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class DoubleEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly PaymentId Payment = PaymentId.New();
    private static readonly AccountId Payer = AccountId.New();
    private static readonly AccountId Payee = AccountId.New();
    private static readonly AccountId FeeAccount = AccountId.New();

    [Fact]
    public void A_capture_with_a_fee_writes_three_balanced_lines()
    {
        Money gross = new(10_000L, Currency.Usd);
        Money fee = new(320L, Currency.Usd);

        LedgerEntry[] entries = new LedgerEntry[DoubleEntry.CaptureLinesWithFee];

        int written = DoubleEntry.WriteCapture(
            Payment, Payer, Payee, FeeAccount, gross, fee, Now, entries);

        Assert.Equal(3, written);
        Assert.True(DoubleEntry.IsBalanced(entries.AsSpan(0, written)));

        Assert.Equal(LedgerDirection.Debit, entries[0].Direction);
        Assert.Equal(10_000L, entries[0].AmountMinorUnits);

        Assert.Equal(LedgerDirection.Credit, entries[1].Direction);
        Assert.Equal(9_680L, entries[1].AmountMinorUnits);

        Assert.Equal(LedgerDirection.Credit, entries[2].Direction);
        Assert.Equal(320L, entries[2].AmountMinorUnits);
        Assert.Equal(FeeAccount, entries[2].AccountId);
    }

    /// <summary>
    /// A zero fee must not produce a zero-amount line: a ledger row that moves nothing is
    /// noise in the audit trail, and the amount constraint forbids it anyway.
    /// </summary>
    [Fact]
    public void A_capture_with_no_fee_writes_only_two_lines()
    {
        Money gross = new(10_000L, Currency.Usd);
        Money noFee = Money.Zero(Currency.Usd);

        LedgerEntry[] entries = new LedgerEntry[DoubleEntry.CaptureLinesWithFee];

        int written = DoubleEntry.WriteCapture(
            Payment, Payer, Payee, FeeAccount, gross, noFee, Now, entries);

        Assert.Equal(DoubleEntry.CaptureLinesWithoutFee, written);
        Assert.True(DoubleEntry.IsBalanced(entries.AsSpan(0, written)));
    }

    [Fact]
    public void A_refund_writes_two_balanced_lines()
    {
        RefundId refundId = RefundId.New();
        Money amount = new(2_500L, Currency.Usd);

        LedgerEntry[] entries = new LedgerEntry[DoubleEntry.RefundLines];

        int written = DoubleEntry.WriteRefund(
            Payment, refundId, Payer, Payee, amount, Now, entries);

        Assert.Equal(2, written);
        Assert.True(DoubleEntry.IsBalanced(entries));

        // The payee is debited and the payer credited: the reverse of the capture.
        Assert.Equal(Payee, entries[0].AccountId);
        Assert.Equal(LedgerDirection.Debit, entries[0].Direction);
        Assert.Equal(Payer, entries[1].AccountId);
        Assert.Equal(LedgerDirection.Credit, entries[1].Direction);

        Assert.All(entries, entry => Assert.Equal(refundId, entry.RefundId));
    }

    [Fact]
    public void A_capture_whose_fee_consumes_the_whole_amount_is_rejected()
    {
        Money gross = new(100L, Currency.Usd);
        Money fee = new(100L, Currency.Usd);

        LedgerEntry[] entries = new LedgerEntry[DoubleEntry.CaptureLinesWithFee];

        // Net would be zero, which cannot be written as a line, so the whole set is
        // refused rather than silently reduced to two lines that do not balance.
        Assert.Equal(
            -1,
            DoubleEntry.WriteCapture(
                Payment, Payer, Payee, FeeAccount, gross, fee, Now, entries));
    }

    [Fact]
    public void A_capture_into_too_small_a_buffer_is_rejected()
    {
        LedgerEntry[] tooSmall = new LedgerEntry[1];

        Assert.Equal(
            -1,
            DoubleEntry.WriteCapture(
                Payment, Payer, Payee, FeeAccount,
                new Money(10_000L, Currency.Usd), new Money(320L, Currency.Usd),
                Now, tooSmall));
    }

    [Fact]
    public void An_unbalanced_set_is_detected_and_its_net_reported()
    {
        LedgerEntry[] entries =
        [
            LedgerEntry.Create(
                Payment, null, Payer, LedgerDirection.Debit,
                LedgerEntryType.PaymentDebit, new Money(10_000L, Currency.Usd), Now),
            LedgerEntry.Create(
                Payment, null, Payee, LedgerDirection.Credit,
                LedgerEntryType.PaymentCredit, new Money(9_000L, Currency.Usd), Now),
        ];

        Assert.False(DoubleEntry.IsBalanced(entries));
        Assert.Equal(-1_000L, DoubleEntry.NetMinorUnits(entries));
    }

    [Fact]
    public void A_ledger_line_must_carry_a_positive_amount()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LedgerEntry.Create(
            Payment, null, Payer, LedgerDirection.Debit,
            LedgerEntryType.PaymentDebit, Money.Zero(Currency.Usd), Now));

    [Fact]
    public void SignedMinorUnits_puts_the_sign_on_the_direction()
    {
        LedgerEntry debit = LedgerEntry.Create(
            Payment, null, Payer, LedgerDirection.Debit,
            LedgerEntryType.PaymentDebit, new Money(500L, Currency.Usd), Now);

        LedgerEntry credit = LedgerEntry.Create(
            Payment, null, Payee, LedgerDirection.Credit,
            LedgerEntryType.PaymentCredit, new Money(500L, Currency.Usd), Now);

        Assert.Equal(-500L, debit.SignedMinorUnits);
        Assert.Equal(500L, credit.SignedMinorUnits);
    }
}
