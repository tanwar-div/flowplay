using FlowPay.Api.Infrastructure;
using FlowPay.Application.Outbox;
using FlowPay.Application.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FlowPay.Api.Endpoints;

/// <summary>
/// Operational endpoints: the invariant check and outbox recovery.
/// </summary>
/// <remarks>
/// Grouped under <c>/v1/admin</c> so a deployment can restrict the prefix at the ingress.
/// The invariant check in particular reads the whole ledger, which is not something to
/// leave open to arbitrary callers.
/// </remarks>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder admin = routes
            .MapGroup("/v1/admin")
            .WithTags("Admin");

        admin.MapGet("/invariants", CheckInvariantsAsync)
            .WithName("CheckInvariants")
            .WithSummary("Verifies the ledger balances and no payment is over-refunded.");

        admin.MapGet("/outbox/depth", GetOutboxDepthAsync)
            .WithName("GetOutboxDepth")
            .WithSummary("Returns the outbox backlog by state.");

        admin.MapPost("/outbox/requeue", RequeueOutboxAsync)
            .WithName("RequeueOutbox")
            .WithSummary("Returns parked outbox messages to the pending pool.");

        return routes;
    }

    private static async Task<IResult> CheckInvariantsAsync(
        PaymentQueryService queries,
        CancellationToken cancellationToken)
    {
        InvariantReportDto report = await queries.CheckInvariantsAsync(cancellationToken);

        // 500 when an invariant is broken, so an automated check fails on the status code
        // alone. A broken ledger is a system fault, not a successful report about one.
        return ApiResults.Json(
            report.Healthy ? 200 : 500,
            FlowPayJson.Serialize(report));
    }

    private static async Task<IResult> GetOutboxDepthAsync(
        IOutboxStore outbox,
        CancellationToken cancellationToken)
    {
        OutboxDepth depth = await outbox.GetDepthAsync(cancellationToken);

        return Results.Ok(new
        {
            pending = depth.Pending,
            failed = depth.Failed,
            oldestPendingAgeSeconds = depth.OldestPendingAgeSeconds,
        });
    }

    private static async Task<IResult> RequeueOutboxAsync(
        IOutboxStore outbox,
        CancellationToken cancellationToken,
        int batchSize = 500)
    {
        int requeued = await outbox.RequeueFailedAsync(
            Math.Clamp(batchSize, 1, 10_000), cancellationToken);

        return Results.Ok(new { requeued });
    }
}
