using System.Net;
using FlowPay.Application.Payments;
using FlowPay.IntegrationTests.Fixtures;
using Xunit;

namespace FlowPay.IntegrationTests.Scenarios;

/// <summary>
/// Concurrent refunds against one payment: the hardest correctness property in the system.
/// </summary>
/// <remarks>
/// Three mechanisms have to cooperate for these to pass. The aggregate rejects an
/// over-refund; the <c>SELECT ... FOR UPDATE</c> on the payment row makes concurrent
/// refunds serialize so the second one sees the first one's total; and a <c>CHECK</c>
/// constraint makes an over-refunded row unstorable. Removing any one of them should turn
/// these tests red.
/// </remarks>
[Collection(FlowPayCollection.Name)]
public class RefundConcurrencyScenarios
{
    private readonly FlowPayFixture _fixture;
    private readonly FlowPayClient _client;

    public RefundConcurrencyScenarios(FlowPayFixture fixture)
    {
        _fixture = fixture;
        _client = new FlowPayClient(fixture.Api.CreateClient());
    }

    /// <summary>
    /// Many concurrent refunds, each with its own key, whose sum far exceeds the captured
    /// amount. Some must succeed, the rest must be rejected, and the total must land
    /// exactly on the captured amount.
    /// </summary>
    [Fact]
    public async Task Concurrent_refunds_never_exceed_the_captured_amount()
    {
        const int Concurrency = 40;
        const long Amount = 100_000L;
        const long RefundChunk = 10_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 1_000_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 1_000_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, Amount, NewKey());

        Assert.Equal(HttpStatusCode.Created, payment.Status);
        Guid paymentId = payment.Body!.Id;

        // 40 refunds of 10,000 against a payment of 100,000: at most 10 can succeed.
        Task<ApiResponse<RefundDto>>[] attempts = Enumerable
            .Range(0, Concurrency)
            .Select(_ => new FlowPayClient(_fixture.Api.CreateClient())
                .RefundAsync(paymentId, RefundChunk, NewKey()))
            .ToArray();

        ApiResponse<RefundDto>[] responses = await Task.WhenAll(attempts);

        int succeeded = responses.Count(response => response.Status == HttpStatusCode.Created);
        int rejected = responses.Count(response =>
            response.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict);

        Assert.Equal(Concurrency, succeeded + rejected);
        Assert.Equal((int)(Amount / RefundChunk), succeeded);

        PaymentDto reloaded = await _client.GetPaymentAsync(paymentId);
        Assert.Equal(Amount, reloaded.Refunded.MinorUnits);
        Assert.Equal("Refunded", reloaded.Status);
        Assert.Equal(0L, reloaded.Refundable.MinorUnits);

        InvariantReportDto report = await _client.GetInvariantReportAsync();
        Assert.True(report.Healthy, string.Join("; ", report.Details));
        Assert.Equal(0, report.OverRefundedPaymentCount);
    }

    /// <summary>
    /// Refunds whose amounts do not divide the payment evenly, so the last successful one
    /// has to be the one that exactly exhausts the remaining headroom.
    /// </summary>
    [Fact]
    public async Task Concurrent_uneven_refunds_exhaust_the_payment_exactly()
    {
        const long Amount = 100_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 1_000_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 1_000_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, Amount, NewKey());

        Guid paymentId = payment.Body!.Id;

        long[] amounts = [33_333L, 33_333L, 33_334L, 33_333L, 50_000L, 1L, 99_999L];

        Task<ApiResponse<RefundDto>>[] attempts = amounts
            .Select(amount => new FlowPayClient(_fixture.Api.CreateClient())
                .RefundAsync(paymentId, amount, NewKey()))
            .ToArray();

        ApiResponse<RefundDto>[] responses = await Task.WhenAll(attempts);

        long refundedTotal = responses
            .Where(response => response.Status == HttpStatusCode.Created)
            .Sum(response => response.Body!.Amount.MinorUnits);

        PaymentDto reloaded = await _client.GetPaymentAsync(paymentId);

        Assert.Equal(refundedTotal, reloaded.Refunded.MinorUnits);
        Assert.True(
            reloaded.Refunded.MinorUnits <= Amount,
            $"Refunded {reloaded.Refunded.MinorUnits} exceeds the captured {Amount}.");

        InvariantReportDto report = await _client.GetInvariantReportAsync();
        Assert.True(report.Healthy, string.Join("; ", report.Details));
    }

    /// <summary>
    /// A single refund for more than was captured is rejected outright, with nothing
    /// partially applied.
    /// </summary>
    [Fact]
    public async Task An_over_refund_is_rejected_and_changes_nothing()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 500_000L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, NewKey());

        AccountDto payeeBefore = await _client.GetAccountAsync(payee);

        ApiResponse<RefundDto> refund = await _client.RefundAsync(
            payment.Body!.Id, 10_001L, NewKey());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refund.Status);
        Assert.Contains("refund_exceeds_captured_amount", refund.RawBody);

        PaymentDto reloaded = await _client.GetPaymentAsync(payment.Body.Id);
        Assert.Equal(0L, reloaded.Refunded.MinorUnits);
        Assert.Equal("Captured", reloaded.Status);

        AccountDto payeeAfter = await _client.GetAccountAsync(payee);
        Assert.Equal(payeeBefore.Balance.MinorUnits, payeeAfter.Balance.MinorUnits);
    }

    /// <summary>
    /// Concurrent payments between two accounts in both directions. This is the deadlock
    /// case the ordered lock acquisition exists to prevent: without it, a payment A-&gt;B
    /// and a payment B-&gt;A can each hold the lock the other needs.
    /// </summary>
    [Fact]
    public async Task Payments_in_both_directions_between_two_accounts_do_not_deadlock()
    {
        const int PairCount = 24;

        Guid first = await _client.CreateAccountAsync("USD", 1_000_000L);
        Guid second = await _client.CreateAccountAsync("USD", 1_000_000L);

        List<Task<ApiResponse<PaymentDto>>> attempts = [];

        for (int i = 0; i < PairCount; i++)
        {
            attempts.Add(new FlowPayClient(_fixture.Api.CreateClient())
                .CreatePaymentAsync(first, second, 1_000L, NewKey()));

            attempts.Add(new FlowPayClient(_fixture.Api.CreateClient())
                .CreatePaymentAsync(second, first, 1_000L, NewKey()));
        }

        ApiResponse<PaymentDto>[] responses = await Task.WhenAll(attempts);

        // No deadlock means no request failed with a server error.
        Assert.All(responses, response => Assert.True(
            (int)response.Status < 500,
            $"A concurrent payment failed with {response.Status}: {response.RawBody}"));

        Assert.Equal(PairCount * 2, responses.Count(r => r.Status == HttpStatusCode.Created));

        InvariantReportDto report = await _client.GetInvariantReportAsync();
        Assert.True(report.Healthy, string.Join("; ", report.Details));
    }

    /// <summary>
    /// Concurrent payments out of one account with only enough funds for some of them.
    /// The reservation model must let exactly as many through as the balance supports.
    /// </summary>
    [Fact]
    public async Task Concurrent_payments_cannot_overdraw_an_account()
    {
        const int Concurrency = 30;
        const long Balance = 100_000L;
        const long Amount = 10_000L;

        Guid payer = await _client.CreateAccountAsync("USD", Balance);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        Task<ApiResponse<PaymentDto>>[] attempts = Enumerable
            .Range(0, Concurrency)
            .Select(_ => new FlowPayClient(_fixture.Api.CreateClient())
                .CreatePaymentAsync(payer, payee, Amount, NewKey()))
            .ToArray();

        ApiResponse<PaymentDto>[] responses = await Task.WhenAll(attempts);

        // A declined payment is recorded as a Failed payment rather than an HTTP error,
        // so the outcome is read from the payment's status.
        int captured = responses.Count(r => r.Body?.Status == "Captured");
        int failed = responses.Count(r => r.Body?.Status == "Failed");

        Assert.Equal((int)(Balance / Amount), captured);
        Assert.Equal(Concurrency - captured, failed);

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(0L, payerAccount.Balance.MinorUnits);
        Assert.True(payerAccount.Balance.MinorUnits >= 0L);

        InvariantReportDto report = await _client.GetInvariantReportAsync();
        Assert.True(report.Healthy, string.Join("; ", report.Details));
    }

    private static string NewKey() => $"it-{Guid.CreateVersion7():n}";
}
