using FlowPay.Api.Contracts;
using FlowPay.Api.Infrastructure;
using FlowPay.Application.Idempotency;
using FlowPay.Application.Payments;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FlowPay.Api.Endpoints;

/// <summary>
/// The payment endpoints.
/// </summary>
/// <remarks>
/// Every mutating route follows the same four steps: read and validate the idempotency
/// key, build the command, compute the request fingerprint, and hand both to
/// <see cref="IdempotentExecutor"/>. The executor owns the transaction, so an endpoint
/// cannot forget to open one — which is the point of routing all of them through it
/// rather than letting each handler manage its own.
/// </remarks>
public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder payments = routes
            .MapGroup("/v1/payments")
            .WithTags("Payments");

        payments.MapPost("/", CreatePaymentAsync)
            .WithName("CreatePayment")
            .WithSummary("Authorizes a payment, optionally capturing it in the same call.");

        payments.MapPost("/{id:guid}/capture", CapturePaymentAsync)
            .WithName("CapturePayment")
            .WithSummary("Captures a previously authorized payment.");

        payments.MapPost("/{id:guid}/void", VoidPaymentAsync)
            .WithName("VoidPayment")
            .WithSummary("Releases an authorization before capture.");

        payments.MapPost("/{id:guid}/refunds", CreateRefundAsync)
            .WithName("CreateRefund")
            .WithSummary("Refunds some or all of a captured payment.");

        payments.MapGet("/{id:guid}", GetPaymentAsync)
            .WithName("GetPayment")
            .WithSummary("Returns a payment.");

        payments.MapGet("/{id:guid}/ledger", GetPaymentLedgerAsync)
            .WithName("GetPaymentLedger")
            .WithSummary("Returns the double-entry ledger lines for a payment.");

        return routes;
    }

    private static async Task<IResult> CreatePaymentAsync(
        HttpContext context,
        [FromBody] CreatePaymentRequest request,
        IdempotentExecutor executor,
        PaymentService payments,
        CancellationToken cancellationToken)
    {
        IdempotencyHeaderResult key = IdempotencyHeader.Read(context.Request);

        if (!key.IsValid)
        {
            return ApiResults.InvalidIdempotencyKey(key.Error);
        }

        if (!Enum.TryParse(request.Method, ignoreCase: true, out PaymentMethod method))
        {
            return ApiResults.Json(PaymentProblems.Unprocessable(
                "payment_method_unknown",
                "The payment method must be one of Card, BankTransfer, Wallet, DirectDebit."));
        }

        AuthorizePaymentCommand command = new(
            request.SourceAccountId,
            request.DestinationAccountId,
            request.AmountMinorUnits,
            request.Currency ?? string.Empty,
            method,
            request.Reference ?? string.Empty,
            request.Description ?? string.Empty,
            request.Capture);

        byte[] fingerprint = RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(command));

        ExecutionResult execution = await executor.ExecuteAsync(
            IdempotencyScopes.AuthorizePayment,
            key.Key,
            fingerprint,
            token => payments.AuthorizeAsync(command, token),
            cancellationToken);

        return ApiResults.FromExecution(context, execution, "/v1/payments/{id}");
    }

    private static async Task<IResult> CapturePaymentAsync(
        HttpContext context,
        Guid id,
        IdempotentExecutor executor,
        PaymentService payments,
        CancellationToken cancellationToken)
    {
        IdempotencyHeaderResult key = IdempotencyHeader.Read(context.Request);

        if (!key.IsValid)
        {
            return ApiResults.InvalidIdempotencyKey(key.Error);
        }

        CapturePaymentCommand command = new(id);
        byte[] fingerprint = RequestFingerprint.Compute(CanonicalRequest.ForCapture(command));

        ExecutionResult execution = await executor.ExecuteAsync(
            IdempotencyScopes.CapturePayment,
            key.Key,
            fingerprint,
            token => payments.CaptureAsync(command, token),
            cancellationToken);

        return ApiResults.FromExecution(context, execution);
    }

    private static async Task<IResult> VoidPaymentAsync(
        HttpContext context,
        Guid id,
        IdempotentExecutor executor,
        PaymentService payments,
        CancellationToken cancellationToken)
    {
        IdempotencyHeaderResult key = IdempotencyHeader.Read(context.Request);

        if (!key.IsValid)
        {
            return ApiResults.InvalidIdempotencyKey(key.Error);
        }

        VoidPaymentCommand command = new(id);
        byte[] fingerprint = RequestFingerprint.Compute(CanonicalRequest.ForVoid(command));

        ExecutionResult execution = await executor.ExecuteAsync(
            IdempotencyScopes.VoidPayment,
            key.Key,
            fingerprint,
            token => payments.VoidAsync(command, token),
            cancellationToken);

        return ApiResults.FromExecution(context, execution);
    }

    private static async Task<IResult> CreateRefundAsync(
        HttpContext context,
        Guid id,
        [FromBody] CreateRefundRequest request,
        IdempotentExecutor executor,
        PaymentService payments,
        CancellationToken cancellationToken)
    {
        IdempotencyHeaderResult key = IdempotencyHeader.Read(context.Request);

        if (!key.IsValid)
        {
            return ApiResults.InvalidIdempotencyKey(key.Error);
        }

        RefundPaymentCommand command = new(
            id,
            request.AmountMinorUnits,
            request.Currency ?? string.Empty,
            request.Reason ?? string.Empty);

        byte[] fingerprint = RequestFingerprint.Compute(CanonicalRequest.ForRefund(command));

        ExecutionResult execution = await executor.ExecuteAsync(
            IdempotencyScopes.RefundPayment,
            key.Key,
            fingerprint,
            // The raw key is passed through to the refund row as well as the idempotency
            // table, so a refund can be traced back to the request that created it
            // without joining through the idempotency records.
            token => payments.RefundAsync(command, key.Key, token),
            cancellationToken);

        return ApiResults.FromExecution(context, execution);
    }

    private static async Task<IResult> GetPaymentAsync(
        Guid id,
        PaymentQueryService queries,
        CancellationToken cancellationToken)
    {
        PaymentDto? payment = await queries.GetPaymentAsync(id, cancellationToken);

        return payment is null
            ? ApiResults.NotFound("payment")
            : ApiResults.Json(200, FlowPayJson.Serialize(payment));
    }

    private static async Task<IResult> GetPaymentLedgerAsync(
        Guid id,
        PaymentQueryService queries,
        CancellationToken cancellationToken)
    {
        LedgerEntryDto[] entries = await queries.GetPaymentLedgerAsync(id, cancellationToken);

        return ApiResults.Json(200, FlowPayJson.Serialize(entries));
    }
}
