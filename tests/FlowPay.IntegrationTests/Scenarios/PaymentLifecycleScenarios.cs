using System.Net;
using FlowPay.Application.Payments;
using FlowPay.IntegrationTests.Fixtures;
using Xunit;

namespace FlowPay.IntegrationTests.Scenarios;

/// <summary>
/// The payment lifecycle and the double-entry ledger it produces.
/// </summary>
[Collection(FlowPayCollection.Name)]
public class PaymentLifecycleScenarios
{
    private readonly FlowPayClient _client;

    public PaymentLifecycleScenarios(FlowPayFixture fixture)
        => _client = new FlowPayClient(fixture.Api.CreateClient());

    [Fact]
    public async Task An_authorize_and_capture_moves_funds_and_balances_the_ledger()
    {
        const long Amount = 100_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, Amount, NewKey());

        Assert.Equal(HttpStatusCode.Created, payment.Status);
        Assert.Equal("Captured", payment.Body!.Status);

        // 2.9% + 30c on 100,000 minor units: 2,900 + 30.
        long expectedFee = 2_930L;
        Assert.Equal(expectedFee, payment.Body.Fee.MinorUnits);

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        AccountDto payeeAccount = await _client.GetAccountAsync(payee);

        Assert.Equal(400_000L, payerAccount.Balance.MinorUnits);
        Assert.Equal(Amount - expectedFee, payeeAccount.Balance.MinorUnits);

        // The reservation is fully consumed by the capture.
        Assert.Equal(0L, payerAccount.Reserved.MinorUnits);

        LedgerEntryDto[] ledger = await _client.GetLedgerAsync(payment.Body.Id);
        Assert.Equal(3, ledger.Length);

        long net = ledger.Sum(entry =>
            entry.Direction == "Credit" ? entry.Amount.MinorUnits : -entry.Amount.MinorUnits);

        Assert.Equal(0L, net);
    }

    [Fact]
    public async Task An_authorization_reserves_funds_without_moving_them()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, NewKey(), capture: false);

        Assert.Equal("Authorized", payment.Body!.Status);
        Assert.NotNull(payment.Body.AuthorizationExpiresAt);

        AccountDto payerAccount = await _client.GetAccountAsync(payer);

        // Balance unchanged, but the funds are no longer available.
        Assert.Equal(500_000L, payerAccount.Balance.MinorUnits);
        Assert.Equal(100_000L, payerAccount.Reserved.MinorUnits);
        Assert.Equal(400_000L, payerAccount.Available.MinorUnits);

        // No money has moved, so there are no ledger lines yet.
        Assert.Empty(await _client.GetLedgerAsync(payment.Body.Id));
    }

    [Fact]
    public async Task A_void_releases_the_reservation()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, NewKey(), capture: false);

        ApiResponse<PaymentDto> voided = await _client.VoidPaymentAsync(
            payment.Body!.Id, NewKey());

        Assert.Equal(HttpStatusCode.OK, voided.Status);
        Assert.Equal("Voided", voided.Body!.Status);

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(500_000L, payerAccount.Balance.MinorUnits);
        Assert.Equal(0L, payerAccount.Reserved.MinorUnits);
    }

    [Fact]
    public async Task A_captured_payment_cannot_be_voided()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, NewKey());

        ApiResponse<PaymentDto> voided = await _client.VoidPaymentAsync(
            payment.Body!.Id, NewKey());

        Assert.Equal(HttpStatusCode.Conflict, voided.Status);
        Assert.Contains("transition_not_permitted", voided.RawBody);
    }

    [Fact]
    public async Task A_partial_refund_leaves_the_payment_refundable()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 500_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, NewKey());

        ApiResponse<RefundDto> refund = await _client.RefundAsync(
            payment.Body!.Id, 40_000L, NewKey());

        Assert.Equal(HttpStatusCode.Created, refund.Status);
        Assert.Equal("PartiallyRefunded", refund.Body!.ResultingPaymentStatus);

        PaymentDto reloaded = await _client.GetPaymentAsync(payment.Body.Id);
        Assert.Equal("PartiallyRefunded", reloaded.Status);
        Assert.Equal(40_000L, reloaded.Refunded.MinorUnits);
        Assert.Equal(60_000L, reloaded.Refundable.MinorUnits);

        // Capture produced three lines; the refund adds a balanced pair.
        LedgerEntryDto[] ledger = await _client.GetLedgerAsync(payment.Body.Id);
        Assert.Equal(5, ledger.Length);

        long net = ledger.Sum(entry =>
            entry.Direction == "Credit" ? entry.Amount.MinorUnits : -entry.Amount.MinorUnits);

        Assert.Equal(0L, net);
    }

    /// <summary>
    /// The refund returns the gross amount to the payer: the processing fee is not
    /// returned, matching how card networks behave.
    /// </summary>
    [Fact]
    public async Task A_full_refund_returns_the_gross_amount_to_the_payer()
    {
        const long Amount = 100_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 500_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, Amount, NewKey());

        await _client.RefundAsync(payment.Body!.Id, Amount, NewKey());

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(500_000L, payerAccount.Balance.MinorUnits);

        // The payee received the net but returned the gross, so it is out of pocket by
        // exactly the fee.
        AccountDto payeeAccount = await _client.GetAccountAsync(payee);
        Assert.Equal(500_000L - 2_930L, payeeAccount.Balance.MinorUnits);

        PaymentDto reloaded = await _client.GetPaymentAsync(payment.Body.Id);
        Assert.Equal("Refunded", reloaded.Status);
    }

    [Fact]
    public async Task A_payment_with_insufficient_funds_is_recorded_as_failed()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 1_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, NewKey());

        // 201 with a Failed payment, not a 4xx: the request did create a durable,
        // addressable record of a decline, and the client needs its id.
        Assert.Equal(HttpStatusCode.Created, payment.Status);
        Assert.Equal("Failed", payment.Body!.Status);
        Assert.Equal("InsufficientFunds", payment.Body.FailureReason);

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(1_000L, payerAccount.Balance.MinorUnits);
        Assert.Equal(0L, payerAccount.Reserved.MinorUnits);
    }

    [Fact]
    public async Task A_payment_between_mismatched_currencies_is_rejected()
    {
        Guid usdAccount = await _client.CreateAccountAsync("USD", 500_000L);
        Guid eurAccount = await _client.CreateAccountAsync("EUR", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            usdAccount, eurAccount, 10_000L, NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, payment.Status);
        Assert.Contains("destination_account_currency_mismatch", payment.RawBody);
    }

    [Fact]
    public async Task A_payment_to_an_unknown_account_is_not_found()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, Guid.CreateVersion7(), 10_000L, NewKey());

        Assert.Equal(HttpStatusCode.NotFound, payment.Status);
    }

    [Fact]
    public async Task A_payment_reports_the_transitions_available_from_its_state()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> authorized = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, NewKey(), capture: false);

        Assert.Contains("Captured", authorized.Body!.AvailableTransitions);
        Assert.Contains("Voided", authorized.Body.AvailableTransitions);
        Assert.DoesNotContain("Refunded", authorized.Body.AvailableTransitions);

        ApiResponse<PaymentDto> captured = await _client.CapturePaymentAsync(
            authorized.Body.Id, NewKey());

        Assert.Contains("Refunded", captured.Body!.AvailableTransitions);
        Assert.DoesNotContain("Voided", captured.Body.AvailableTransitions);
    }

    [Fact]
    public async Task EUR_payments_settle_against_the_EUR_fee_account()
    {
        Guid payer = await _client.CreateAccountAsync("EUR", 500_000L);
        Guid payee = await _client.CreateAccountAsync("EUR", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, NewKey(), currency: "EUR");

        Assert.Equal(HttpStatusCode.Created, payment.Status);
        Assert.Equal("Captured", payment.Body!.Status);
        Assert.Equal("EUR", payment.Body.Amount.Currency);

        LedgerEntryDto[] ledger = await _client.GetLedgerAsync(payment.Body.Id);

        // Fee income is sharded, so the assertion is that the fee landed somewhere in the
        // EUR family rather than on one fixed account.
        IReadOnlyList<Guid> eurFeeAccounts =
            FlowPayFixture.FeeAccountFamily(FlowPayFixture.EurFeeAccountBaseId);

        Assert.Contains(ledger, entry =>
            entry.EntryType == "FeeCredit" && eurFeeAccounts.Contains(entry.AccountId));
    }

    private static string NewKey() => $"it-{Guid.CreateVersion7():n}";
}
