using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowPay.LoadTest;

/// <summary>What a single API call produced.</summary>
public readonly record struct CallResult(
    HttpStatusCode Status,
    string Body,
    bool IsIdempotentReplay,
    TimeSpan Elapsed)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>Minimal shapes the harness needs from API responses.</summary>
public sealed record PaymentView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("refunded")] MoneyView Refunded,
    [property: JsonPropertyName("amount")] MoneyView Amount);

public sealed record MoneyView(
    [property: JsonPropertyName("minorUnits")] long MinorUnits,
    [property: JsonPropertyName("currency")] string Currency);

public sealed record AccountView(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("balance")] MoneyView Balance,
    [property: JsonPropertyName("reserved")] MoneyView Reserved);

public sealed record InvariantView(
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("ledgerNetMinorUnits")] long LedgerNetMinorUnits,
    [property: JsonPropertyName("unbalancedPaymentCount")] int UnbalancedPaymentCount,
    [property: JsonPropertyName("overRefundedPaymentCount")] int OverRefundedPaymentCount,
    [property: JsonPropertyName("balanceDiscrepancyCount")] int BalanceDiscrepancyCount,
    [property: JsonPropertyName("details")] string[] Details);

/// <summary>
/// The harness's HTTP client.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="HttpClient"/> over a single <see cref="SocketsHttpHandler"/> shared by
/// every worker. A client per worker would open its own connection pool and spend the
/// measurement on TCP handshakes; the handler's connection limit is raised instead, so
/// concurrency is bounded by the harness's own setting rather than by the default
/// per-server cap.
/// </para>
/// <para>
/// Timings are taken around the full request including reading the body, because a
/// response is not delivered until its bytes are.
/// </para>
/// </remarks>
public sealed class LoadTestClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SocketsHttpHandler _handler;

    public LoadTestClient(Uri baseAddress, int maxConnections, TimeSpan timeout)
    {
        _handler = new SocketsHttpHandler
        {
            // Headroom over the requested concurrency, so queueing for a connection
            // never shows up as server latency.
            MaxConnectionsPerServer = Math.Max(maxConnections * 2, 64),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        _http = new HttpClient(_handler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout,
        };
    }

    public async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using HttpResponseMessage response =
                    await _http.GetAsync("/health/ready", cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // The API is not listening yet. Retrying is the whole point of this loop.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return false;
    }

    public async Task<Guid> CreateAccountAsync(
        string currency,
        long openingBalanceMinorUnits,
        string displayName,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/v1/accounts",
            new
            {
                currency,
                openingBalanceMinorUnits,
                displayName,
            },
            Json,
            cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Account creation failed ({response.StatusCode}): {body}");
        }

        return JsonSerializer.Deserialize<AccountView>(body, Json)?.Id
            ?? throw new InvalidOperationException($"Unexpected account response: {body}");
    }

    public Task<CallResult> CreatePaymentAsync(
        Guid source,
        Guid destination,
        long amountMinorUnits,
        string currency,
        string idempotencyKey,
        bool capture,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(
            new
            {
                sourceAccountId = source,
                destinationAccountId = destination,
                amountMinorUnits,
                currency,
                method = "Card",
                reference = "loadtest",
                description = "FlowPay load harness",
                capture,
            },
            Json);

        return SendAsync(HttpMethod.Post, "/v1/payments", payload, idempotencyKey, cancellationToken);
    }

    public Task<CallResult> RefundAsync(
        Guid paymentId,
        long amountMinorUnits,
        string currency,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(
            new { amountMinorUnits, currency, reason = "loadtest" },
            Json);

        return SendAsync(
            HttpMethod.Post,
            $"/v1/payments/{paymentId}/refunds",
            payload,
            idempotencyKey,
            cancellationToken);
    }

    public Task<CallResult> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"/v1/payments/{paymentId}", null, null, cancellationToken);

    public async Task<AccountView> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        CallResult result = await SendAsync(
            HttpMethod.Get, $"/v1/accounts/{accountId}", null, null, cancellationToken);

        return JsonSerializer.Deserialize<AccountView>(result.Body, Json)
            ?? throw new InvalidOperationException($"Unexpected account response: {result.Body}");
    }

    public async Task<InvariantView> CheckInvariantsAsync(CancellationToken cancellationToken)
    {
        // The endpoint answers 500 when an invariant is broken, so the status is not
        // thrown on: the body is the report the harness needs to print.
        using HttpResponseMessage response =
            await _http.GetAsync("/v1/admin/invariants", cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        return JsonSerializer.Deserialize<InvariantView>(body, Json)
            ?? throw new InvalidOperationException($"Unexpected invariant response: {body}");
    }

    public static PaymentView? ParsePayment(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<PaymentView>(body, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CallResult> SendAsync(
        HttpMethod method,
        string path,
        string? payload,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, path);

        if (payload is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        Reporting.LatencyScope scope = Reporting.LatencyScope.Start();

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            bool replay = response.Headers.TryGetValues(
                    "Idempotent-Replay", out IEnumerable<string>? values)
                && values.Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase));

            return new CallResult(response.StatusCode, body, replay, scope.Stop());
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client-side timeout. Reported as 408 so it is counted as a failure rather
            // than silently dropped from the sample, which would flatter the percentiles.
            return new CallResult(HttpStatusCode.RequestTimeout, "client timeout", false, scope.Stop());
        }
        catch (HttpRequestException failure)
        {
            return new CallResult(
                HttpStatusCode.ServiceUnavailable, failure.Message, false, scope.Stop());
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
