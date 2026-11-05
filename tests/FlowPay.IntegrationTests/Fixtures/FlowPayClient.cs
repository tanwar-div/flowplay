using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FlowPay.Api.Contracts;
using FlowPay.Application.Payments;

namespace FlowPay.IntegrationTests.Fixtures;

/// <summary>An API response with its status, headers of interest, and parsed body.</summary>
public sealed record ApiResponse<T>(
    HttpStatusCode Status,
    T? Body,
    string RawBody,
    bool IsIdempotentReplay)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>
/// A thin typed client over the API, so the tests read as scenarios rather than as HTTP
/// plumbing.
/// </summary>
/// <remarks>
/// Goes through real HTTP rather than calling the handlers directly. The idempotency key
/// arrives as a header, the response is serialized and reparsed, and model binding and
/// routing are exercised — all of which are part of the behaviour under test.
/// </remarks>
public sealed class FlowPayClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public FlowPayClient(HttpClient http) => _http = http;

    public async Task<Guid> CreateAccountAsync(
        string currency,
        long openingBalanceMinorUnits,
        string? displayName = null)
    {
        ApiResponse<AccountDto> response = await PostAsync<CreateAccountRequest, AccountDto>(
            "/v1/accounts",
            new CreateAccountRequest(null, currency, openingBalanceMinorUnits, displayName ?? "test"),
            idempotencyKey: null);

        if (!response.IsSuccess || response.Body is null)
        {
            throw new InvalidOperationException(
                $"Account creation failed ({response.Status}): {response.RawBody}");
        }

        return response.Body.Id;
    }

    public Task<ApiResponse<PaymentDto>> CreatePaymentAsync(
        Guid source,
        Guid destination,
        long amountMinorUnits,
        string idempotencyKey,
        string currency = "USD",
        string method = "Card",
        string reference = "ORDER-1",
        string description = "integration test",
        bool capture = true)
        => PostAsync<CreatePaymentRequest, PaymentDto>(
            "/v1/payments",
            new CreatePaymentRequest(
                source, destination, amountMinorUnits, currency, method,
                reference, description, capture),
            idempotencyKey);

    public Task<ApiResponse<PaymentDto>> CapturePaymentAsync(Guid paymentId, string idempotencyKey)
        => PostEmptyAsync<PaymentDto>($"/v1/payments/{paymentId}/capture", idempotencyKey);

    public Task<ApiResponse<PaymentDto>> VoidPaymentAsync(Guid paymentId, string idempotencyKey)
        => PostEmptyAsync<PaymentDto>($"/v1/payments/{paymentId}/void", idempotencyKey);

    public Task<ApiResponse<RefundDto>> RefundAsync(
        Guid paymentId,
        long amountMinorUnits,
        string idempotencyKey,
        string currency = "USD",
        string reason = "customer request")
        => PostAsync<CreateRefundRequest, RefundDto>(
            $"/v1/payments/{paymentId}/refunds",
            new CreateRefundRequest(amountMinorUnits, currency, reason),
            idempotencyKey);

    public async Task<PaymentDto> GetPaymentAsync(Guid paymentId)
        => await _http.GetFromJsonAsync<PaymentDto>($"/v1/payments/{paymentId}", Json)
            ?? throw new InvalidOperationException($"Payment {paymentId} was not found.");

    public async Task<AccountDto> GetAccountAsync(Guid accountId)
        => await _http.GetFromJsonAsync<AccountDto>($"/v1/accounts/{accountId}", Json)
            ?? throw new InvalidOperationException($"Account {accountId} was not found.");

    public async Task<LedgerEntryDto[]> GetLedgerAsync(Guid paymentId)
        => await _http.GetFromJsonAsync<LedgerEntryDto[]>($"/v1/payments/{paymentId}/ledger", Json)
            ?? [];

    /// <summary>
    /// Reads the invariant report. The endpoint answers 500 when an invariant is broken,
    /// so the status is read rather than thrown on.
    /// </summary>
    public async Task<InvariantReportDto> GetInvariantReportAsync()
    {
        using HttpResponseMessage response = await _http.GetAsync("/v1/admin/invariants");
        string body = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<InvariantReportDto>(body, Json)
            ?? throw new InvalidOperationException($"Could not read the invariant report: {body}");
    }

    private async Task<ApiResponse<TResponse>> PostAsync<TRequest, TResponse>(
        string path,
        TRequest payload,
        string? idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: Json),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await SendAsync<TResponse>(request);
    }

    private async Task<ApiResponse<TResponse>> PostEmptyAsync<TResponse>(
        string path,
        string idempotencyKey)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await SendAsync<TResponse>(request);
    }

    private async Task<ApiResponse<TResponse>> SendAsync<TResponse>(HttpRequestMessage request)
    {
        using HttpResponseMessage response = await _http.SendAsync(request);
        string raw = await response.Content.ReadAsStringAsync();

        bool replay =
            response.Headers.TryGetValues("Idempotent-Replay", out IEnumerable<string>? values)
            && values.Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase));

        TResponse? body = default;

        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                body = JsonSerializer.Deserialize<TResponse>(raw, Json);
            }
            catch (JsonException)
            {
                // An error body has a different shape. The raw text is kept so the test
                // can assert on it, which is more useful than a deserialization failure.
            }
        }

        return new ApiResponse<TResponse>(response.StatusCode, body, raw, replay);
    }
}
