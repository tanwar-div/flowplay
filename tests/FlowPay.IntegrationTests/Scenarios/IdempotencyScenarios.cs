using System.Net;
using System.Net.Http.Json;
using FlowPay.Application.Payments;
using FlowPay.IntegrationTests.Fixtures;
using Xunit;

namespace FlowPay.IntegrationTests.Scenarios;

/// <summary>
/// The duplicate-prevention guarantee, end to end through HTTP and PostgreSQL.
/// </summary>
[Collection(FlowPayCollection.Name)]
public class IdempotencyScenarios
{
    private readonly FlowPayFixture _fixture;
    private readonly FlowPayClient _client;

    public IdempotencyScenarios(FlowPayFixture fixture)
    {
        _fixture = fixture;
        _client = new FlowPayClient(fixture.Api.CreateClient());
    }

    [Fact]
    public async Task A_repeated_request_returns_the_original_payment_and_charges_once()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);
        string key = NewKey();

        ApiResponse<PaymentDto> first = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, key);

        ApiResponse<PaymentDto> second = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, key);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.False(first.IsIdempotentReplay);

        // The replay returns the original response verbatim, not a fresh projection.
        Assert.True(second.IsIdempotentReplay);
        Assert.Equal(first.RawBody, second.RawBody);
        Assert.Equal(first.Body!.Id, second.Body!.Id);

        // The decisive assertion: the payer was debited once, not twice.
        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(400_000L, payerAccount.Balance.MinorUnits);
    }

    /// <summary>
    /// The headline guarantee. Every replay must return the same payment id and leave
    /// exactly one payment's worth of financial effect behind.
    /// </summary>
    [Fact]
    public async Task Hundreds_of_replays_produce_exactly_one_financial_effect()
    {
        const int ReplayAttempts = 250;
        const long Amount = 50_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 1_000_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);
        string key = NewKey();

        ApiResponse<PaymentDto> original = await _client.CreatePaymentAsync(
            payer, payee, Amount, key);

        Assert.Equal(HttpStatusCode.Created, original.Status);
        Guid paymentId = original.Body!.Id;

        for (int i = 0; i < ReplayAttempts; i++)
        {
            ApiResponse<PaymentDto> replay = await _client.CreatePaymentAsync(
                payer, payee, Amount, key);

            Assert.True(replay.IsIdempotentReplay);
            Assert.Equal(paymentId, replay.Body!.Id);
        }

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(1_000_000L - Amount, payerAccount.Balance.MinorUnits);

        LedgerEntryDto[] ledger = await _client.GetLedgerAsync(paymentId);
        Assert.Equal(3, ledger.Length);

        InvariantReportDto report = await _client.GetInvariantReportAsync();
        Assert.True(report.Healthy, string.Join("; ", report.Details));
    }

    /// <summary>
    /// Concurrent duplicates are the case the single-transaction protocol exists for:
    /// the losers block on the unique index, then replay the winner's stored response.
    /// </summary>
    [Fact]
    public async Task Concurrent_duplicate_requests_produce_one_payment()
    {
        const int Concurrency = 32;
        const long Amount = 25_000L;

        Guid payer = await _client.CreateAccountAsync("USD", 1_000_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);
        string key = NewKey();

        // Each task gets its own HttpClient so the requests genuinely overlap rather than
        // queueing behind one client's connection limit.
        Task<ApiResponse<PaymentDto>>[] attempts = Enumerable
            .Range(0, Concurrency)
            .Select(_ => new FlowPayClient(_fixture.Api.CreateClient())
                .CreatePaymentAsync(payer, payee, Amount, key))
            .ToArray();

        ApiResponse<PaymentDto>[] responses = await Task.WhenAll(attempts);

        Guid[] paymentIds = responses
            .Where(response => response.Body is not null)
            .Select(response => response.Body!.Id)
            .Distinct()
            .ToArray();

        // Every response that carries a payment must name the same payment.
        Assert.Single(paymentIds);

        // Exactly one request did the work; the rest replayed or were told to retry.
        Assert.Equal(1, responses.Count(r => r.Status == HttpStatusCode.Created && !r.IsIdempotentReplay));
        Assert.All(responses, response => Assert.True(
            response.Status is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {response.Status}: {response.RawBody}"));

        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(1_000_000L - Amount, payerAccount.Balance.MinorUnits);
    }

    /// <summary>
    /// Reusing a key for a different request is worse than a duplicate: the client would
    /// be told it moved money it had not. It must be rejected, not replayed.
    /// </summary>
    [Fact]
    public async Task Reusing_a_key_with_different_parameters_is_rejected()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);
        string key = NewKey();

        await _client.CreatePaymentAsync(payer, payee, 10_000L, key);

        ApiResponse<PaymentDto> different = await _client.CreatePaymentAsync(
            payer, payee, 20_000L, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.Status);
        Assert.Contains("idempotency_key_reuse", different.RawBody);

        // Only the first payment's amount moved.
        AccountDto payerAccount = await _client.GetAccountAsync(payer);
        Assert.Equal(490_000L, payerAccount.Balance.MinorUnits);
    }

    [Fact]
    public async Task The_same_key_is_usable_on_different_endpoints()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        // One key for the whole business transaction, reused across authorize and capture.
        string key = NewKey();

        ApiResponse<PaymentDto> authorized = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, key, capture: false);

        Assert.Equal(HttpStatusCode.Created, authorized.Status);
        Assert.Equal("Authorized", authorized.Body!.Status);

        // Scoping keys by endpoint is what makes this work: the capture is not mistaken
        // for a duplicate of the authorization.
        ApiResponse<PaymentDto> captured = await _client.CapturePaymentAsync(
            authorized.Body.Id, key);

        Assert.Equal(HttpStatusCode.OK, captured.Status);
        Assert.Equal("Captured", captured.Body!.Status);
        Assert.False(captured.IsIdempotentReplay);
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_rejected()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        using HttpClient raw = _fixture.Api.CreateClient();

        using HttpResponseMessage response = await raw.PostAsJsonAsync(
            "/v1/payments",
            new
            {
                sourceAccountId = payer,
                destinationAccountId = payee,
                amountMinorUnits = 1_000L,
                currency = "USD",
                method = "Card",
                capture = true,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "idempotency_key_invalid",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    public async Task A_malformed_idempotency_key_is_rejected(string key)
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> response = await _client.CreatePaymentAsync(
            payer, payee, 1_000L, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Contains("idempotency_key_invalid", response.RawBody);
    }

    /// <summary>
    /// A rejected request leaves no state, so there is nothing to make idempotent. Caching
    /// the rejection would trap a client that fixed its request and retried with the same
    /// key.
    /// </summary>
    [Fact]
    public async Task A_rejection_does_not_consume_the_key()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);
        string key = NewKey();

        ApiResponse<PaymentDto> invalid = await _client.CreatePaymentAsync(
            payer, payee, amountMinorUnits: 0L, key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);

        // Same key, now with a valid amount: it must be accepted rather than replaying
        // the stored rejection.
        ApiResponse<PaymentDto> corrected = await _client.CreatePaymentAsync(
            payer, payee, 10_000L, key);

        Assert.Equal(HttpStatusCode.Created, corrected.Status);
        Assert.False(corrected.IsIdempotentReplay);
    }

    /// <summary>
    /// Refunds carry their own idempotency, which matters more than anywhere else: a
    /// duplicated refund gives money away.
    /// </summary>
    [Fact]
    public async Task A_replayed_refund_refunds_once()
    {
        Guid payer = await _client.CreateAccountAsync("USD", 500_000L);
        Guid payee = await _client.CreateAccountAsync("USD", 0L);

        ApiResponse<PaymentDto> payment = await _client.CreatePaymentAsync(
            payer, payee, 100_000L, NewKey());

        string refundKey = NewKey();

        ApiResponse<RefundDto> first = await _client.RefundAsync(
            payment.Body!.Id, 30_000L, refundKey);

        ApiResponse<RefundDto> replay = await _client.RefundAsync(
            payment.Body.Id, 30_000L, refundKey);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.True(replay.IsIdempotentReplay);
        Assert.Equal(first.Body!.Id, replay.Body!.Id);

        PaymentDto reloaded = await _client.GetPaymentAsync(payment.Body.Id);
        Assert.Equal(30_000L, reloaded.Refunded.MinorUnits);
    }

    private static string NewKey() => $"it-{Guid.CreateVersion7():n}";
}
