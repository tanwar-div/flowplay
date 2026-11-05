using FlowPay.Application.Idempotency;
using FlowPay.Application.Payments;
using Microsoft.AspNetCore.Http;

namespace FlowPay.Api.Infrastructure;

/// <summary>
/// Turns handler output into HTTP responses.
/// </summary>
/// <remarks>
/// The handlers return JSON that has already been serialized, because the idempotency
/// layer needs the exact bytes it will store for replay. So the endpoints write that text
/// through rather than re-serializing a DTO: a response and its stored copy must be
/// identical, and serializing twice would let them diverge.
/// </remarks>
public static class ApiResults
{
    private const string JsonContentType = "application/json";

    /// <summary>Writes a pre-serialized body with its status code.</summary>
    public static IResult Json(int statusCode, string json)
        => Results.Text(json, JsonContentType, statusCode: statusCode);

    public static IResult Json(OperationResponseBody body)
        => Json(body.StatusCode, body.Json);

    /// <summary>
    /// Writes the result of an idempotent execution, marking replays and adding a
    /// <c>Location</c> header for newly created resources.
    /// </summary>
    public static IResult FromExecution(
        HttpContext context,
        ExecutionResult execution,
        string? locationTemplate = null)
    {
        if (execution.IsReplay)
        {
            // Lets a client tell "my retry was accepted" from "my retry did the work",
            // which is the difference between a successful retry and a double charge it
            // needs to investigate.
            context.Response.Headers[IdempotencyHeader.ReplayHeaderName] = "true";
        }

        if (execution.Kind == ExecutionKind.InProgress)
        {
            // The duplicate is still in flight. A Retry-After tells the client to wait
            // rather than hammer the endpoint, which would only lengthen the queue behind
            // the row lock it is waiting on.
            context.Response.Headers.RetryAfter = "1";
        }

        if (locationTemplate is not null &&
            execution.StatusCode == StatusCodes.Status201Created &&
            execution.ResourceId is { } resourceId)
        {
            context.Response.Headers.Location =
                locationTemplate.Replace("{id}", resourceId.ToString("d"), StringComparison.Ordinal);
        }

        return Json(execution.StatusCode, execution.Json);
    }

    /// <summary>A problem response for a rejected idempotency header.</summary>
    public static IResult InvalidIdempotencyKey(Domain.Primitives.IdempotencyKeyError error)
        => Json(400, FlowPayJson.Serialize(new ProblemDto(
            "idempotency_key_invalid",
            IdempotencyHeader.Describe(error),
            400)));

    public static IResult NotFound(string resource)
        => Json(PaymentProblems.NotFound(resource));

    public static IResult MalformedBody(string detail)
        => Json(400, FlowPayJson.Serialize(new ProblemDto(
            "malformed_request", "The request body could not be read.", 400, detail)));
}
