using FlowPay.Application.Abstractions;
using FlowPay.Application.Payments;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Api.Infrastructure;

/// <summary>
/// Ensures every configured platform fee account exists before traffic is served.
/// </summary>
/// <remarks>
/// <para>
/// A capture credits its fee to the fee account for that currency. If the account is
/// missing the capture fails — after validation has passed and the client has every
/// reason to expect success. Creating the accounts at startup turns a configuration
/// mistake into a visible startup failure rather than an intermittent payment failure.
/// </para>
/// <para>
/// Implemented as an <see cref="IHostedService"/> that runs to completion before the
/// server starts accepting requests, and written to tolerate several replicas starting
/// at once: provisioning is create-if-absent, so whichever replica loses the race finds
/// the account already there.
/// </para>
/// </remarks>
public sealed class PlatformAccountInitializer : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FlowPayOptions _options;
    private readonly ILogger<PlatformAccountInitializer> _logger;

    public PlatformAccountInitializer(
        IServiceScopeFactory scopeFactory,
        IOptions<FlowPayOptions> options,
        ILogger<PlatformAccountInitializer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int shardCount = Math.Clamp(
            _options.FeeAccountShards, 1, FeeAccountShards.MaxShards);

        foreach ((string currencyCode, Guid baseId) in _options.PlatformFeeAccounts)
        {
            if (baseId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"The platform fee account for {currencyCode} is configured as an empty GUID.");
            }

            // Every shard in the family, not just one account per currency: a capture
            // picks its shard from the payment id, so any of them can be the target of
            // the very first payment.
            for (int shard = 0; shard < shardCount; shard++)
            {
                AccountId accountId = FeeAccountShards.Derive(baseId, shard);

                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                AccountService accounts =
                    scope.ServiceProvider.GetRequiredService<AccountService>();

                ProvisionAccountCommand command = new(
                    accountId.Value,
                    currencyCode,
                    OpeningBalanceMinorUnits: 0L,
                    DisplayName:
                        $"FlowPay platform fees ({currencyCode.ToUpperInvariant()}) shard {shard:D3}");

                OperationResponseBody response = await accounts
                    .ProvisionAsync(command, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode is not (200 or 201))
                {
                    throw new InvalidOperationException(
                        $"Could not provision the {currencyCode} fee account shard {shard}: {response.Json}");
                }
            }

            _logger.LogInformation(
                "Platform fee accounts for {Currency} are ready: {ShardCount} shards from base {BaseId}.",
                currencyCode.ToUpperInvariant(),
                shardCount,
                baseId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
