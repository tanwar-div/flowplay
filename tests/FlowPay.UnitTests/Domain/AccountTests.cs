using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class AccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static Account NewAccount(long openingBalance = 100_000L) => Account.Open(
        AccountId.New(),
        Currency.Usd,
        new Money(openingBalance, Currency.Usd),
        "Test account",
        Now).Value;

    [Fact]
    public void A_new_account_records_its_opening_balance_separately()
    {
        Account account = NewAccount(50_000L);

        Assert.Equal(50_000L, account.BalanceMinorUnits);
        Assert.Equal(50_000L, account.OpeningBalanceMinorUnits);
        Assert.Equal(0L, account.ReservedMinorUnits);
        Assert.Equal(50_000L, account.AvailableMinorUnits);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Open_rejects_an_opening_balance_in_a_different_currency()
    {
        DomainResult<Account> result = Account.Open(
            AccountId.New(), Currency.Usd, new Money(100L, Currency.Eur), "x", Now);

        Assert.Equal(DomainError.CurrencyMismatch, result.Error);
    }

    [Fact]
    public void Reserve_moves_funds_out_of_available_without_changing_the_balance()
    {
        Account account = NewAccount();

        Assert.True(account.TryReserve(new Money(30_000L, Currency.Usd), Now).IsSuccess);

        Assert.Equal(100_000L, account.BalanceMinorUnits);
        Assert.Equal(30_000L, account.ReservedMinorUnits);
        Assert.Equal(70_000L, account.AvailableMinorUnits);
    }

    /// <summary>
    /// The property that makes authorize-then-capture safe: two reservations cannot both
    /// claim the same funds, because the second sees the first's reservation.
    /// </summary>
    [Fact]
    public void A_second_reservation_cannot_claim_already_reserved_funds()
    {
        Account account = NewAccount(100_000L);

        Assert.True(account.TryReserve(new Money(60_000L, Currency.Usd), Now).IsSuccess);

        Assert.Equal(
            DomainError.InsufficientFunds,
            account.TryReserve(new Money(60_000L, Currency.Usd), Now).Error);

        Assert.Equal(60_000L, account.ReservedMinorUnits);
    }

    [Fact]
    public void Reserve_is_rejected_above_the_available_balance()
    {
        Account account = NewAccount(1_000L);

        Assert.Equal(
            DomainError.InsufficientFunds,
            account.TryReserve(new Money(1_001L, Currency.Usd), Now).Error);
    }

    [Fact]
    public void CaptureReserved_draws_down_both_the_reservation_and_the_balance()
    {
        Account account = NewAccount();
        account.TryReserve(new Money(30_000L, Currency.Usd), Now).EnsureSuccess();

        Assert.True(account.TryCaptureReserved(new Money(30_000L, Currency.Usd), Now).IsSuccess);

        Assert.Equal(70_000L, account.BalanceMinorUnits);
        Assert.Equal(0L, account.ReservedMinorUnits);
    }

    [Fact]
    public void CaptureReserved_is_rejected_beyond_what_was_reserved()
    {
        Account account = NewAccount();
        account.TryReserve(new Money(10_000L, Currency.Usd), Now).EnsureSuccess();

        Assert.Equal(
            DomainError.ReservationExceeded,
            account.TryCaptureReserved(new Money(10_001L, Currency.Usd), Now).Error);
    }

    [Fact]
    public void ReleaseReservation_returns_funds_without_moving_the_balance()
    {
        Account account = NewAccount();
        account.TryReserve(new Money(30_000L, Currency.Usd), Now).EnsureSuccess();

        Assert.True(
            account.TryReleaseReservation(new Money(30_000L, Currency.Usd), Now).IsSuccess);

        Assert.Equal(100_000L, account.BalanceMinorUnits);
        Assert.Equal(0L, account.ReservedMinorUnits);
    }

    [Fact]
    public void ReleaseReservation_is_rejected_beyond_what_was_reserved()
    {
        Account account = NewAccount();

        Assert.Equal(
            DomainError.ReservationExceeded,
            account.TryReleaseReservation(new Money(1L, Currency.Usd), Now).Error);
    }

    [Fact]
    public void Credit_increases_the_balance()
    {
        Account account = NewAccount();

        Assert.True(account.TryCredit(new Money(5_000L, Currency.Usd), Now).IsSuccess);
        Assert.Equal(105_000L, account.BalanceMinorUnits);
    }

    /// <summary>
    /// A refund debits the merchant directly, with no prior reservation, and must not be
    /// able to dip into funds another authorization has already claimed.
    /// </summary>
    [Fact]
    public void Debit_cannot_consume_reserved_funds()
    {
        Account account = NewAccount(100_000L);
        account.TryReserve(new Money(90_000L, Currency.Usd), Now).EnsureSuccess();

        Assert.Equal(
            DomainError.InsufficientFunds,
            account.TryDebit(new Money(20_000L, Currency.Usd), Now).Error);

        Assert.True(account.TryDebit(new Money(10_000L, Currency.Usd), Now).IsSuccess);
        Assert.Equal(90_000L, account.BalanceMinorUnits);
    }

    [Fact]
    public void A_frozen_account_can_receive_but_not_send()
    {
        Account account = NewAccount();
        account.Freeze(Now);

        Assert.False(account.CanSend);
        Assert.True(account.CanReceive);

        Assert.Equal(
            DomainError.AccountNotActive,
            account.TryReserve(new Money(100L, Currency.Usd), Now).Error);

        Assert.True(account.TryCredit(new Money(100L, Currency.Usd), Now).IsSuccess);
    }

    [Fact]
    public void A_closed_account_can_neither_send_nor_receive()
    {
        Account account = NewAccount();
        account.TryClose(Now).EnsureSuccess();

        Assert.False(account.CanSend);
        Assert.False(account.CanReceive);

        Assert.Equal(
            DomainError.AccountNotActive,
            account.TryCredit(new Money(100L, Currency.Usd), Now).Error);
    }

    /// <summary>
    /// Closing an account with live reservations would strand in-flight authorizations
    /// with nowhere to settle.
    /// </summary>
    [Fact]
    public void Close_is_rejected_while_funds_are_reserved()
    {
        Account account = NewAccount();
        account.TryReserve(new Money(100L, Currency.Usd), Now).EnsureSuccess();

        Assert.Equal(DomainError.ReservationExceeded, account.TryClose(Now).Error);
        Assert.Equal(AccountStatus.Active, account.Status);
    }

    [Fact]
    public void Reactivate_restores_a_frozen_account_but_not_a_closed_one()
    {
        Account frozen = NewAccount();
        frozen.Freeze(Now);
        frozen.Reactivate(Now);
        Assert.Equal(AccountStatus.Active, frozen.Status);

        Account closed = NewAccount();
        closed.TryClose(Now).EnsureSuccess();
        closed.Reactivate(Now);
        Assert.Equal(AccountStatus.Closed, closed.Status);
    }

    [Fact]
    public void Every_operation_rejects_a_currency_mismatch()
    {
        Account account = NewAccount();
        Money wrongCurrency = new(100L, Currency.Eur);

        Assert.Equal(DomainError.CurrencyMismatch, account.TryReserve(wrongCurrency, Now).Error);
        Assert.Equal(DomainError.CurrencyMismatch, account.TryCredit(wrongCurrency, Now).Error);
        Assert.Equal(DomainError.CurrencyMismatch, account.TryDebit(wrongCurrency, Now).Error);
        Assert.Equal(
            DomainError.CurrencyMismatch,
            account.TryCaptureReserved(wrongCurrency, Now).Error);
        Assert.Equal(
            DomainError.CurrencyMismatch,
            account.TryReleaseReservation(wrongCurrency, Now).Error);
    }

    [Fact]
    public void Credit_reports_overflow_rather_than_wrapping()
    {
        Account account = Account.Open(
            AccountId.New(),
            Currency.Usd,
            new Money(long.MaxValue - 10L, Currency.Usd),
            "x",
            Now).Value;

        Assert.Equal(
            DomainError.ArithmeticOverflow,
            account.TryCredit(new Money(100L, Currency.Usd), Now).Error);
    }
}
