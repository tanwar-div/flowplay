using FlowPay.Api.Contracts;
using FlowPay.Api.Infrastructure;
using FlowPay.Application.Payments;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FlowPay.Api.Endpoints;

/// <summary>Account provisioning and lookup.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder accounts = routes
            .MapGroup("/v1/accounts")
            .WithTags("Accounts");

        accounts.MapPost("/", CreateAccountAsync)
            .WithName("CreateAccount")
            .WithSummary("Creates an account, or returns the existing one with that id.");

        accounts.MapGet("/{id:guid}", GetAccountAsync)
            .WithName("GetAccount")
            .WithSummary("Returns an account with its balance and reserved amount.");

        return routes;
    }

    private static async Task<IResult> CreateAccountAsync(
        [FromBody] CreateAccountRequest request,
        AccountService accounts,
        CancellationToken cancellationToken)
    {
        // No idempotency key on this route. Account creation is naturally idempotent on
        // the client-supplied id: provisioning the same id twice returns the existing
        // account instead of creating a second one or failing.
        ProvisionAccountCommand command = new(
            request.Id,
            request.Currency ?? string.Empty,
            request.OpeningBalanceMinorUnits,
            request.DisplayName ?? string.Empty);

        OperationResponseBody response = await accounts
            .ProvisionAsync(command, cancellationToken);

        return ApiResults.Json(response);
    }

    private static async Task<IResult> GetAccountAsync(
        Guid id,
        PaymentQueryService queries,
        CancellationToken cancellationToken)
    {
        AccountDto? account = await queries.GetAccountAsync(id, cancellationToken);

        return account is null
            ? ApiResults.NotFound("account")
            : ApiResults.Json(200, FlowPayJson.Serialize(account));
    }
}
