using FlowPay.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowPay.Worker.Hosting;

/// <summary>
/// Applies the schema before the worker's background services start.
/// </summary>
/// <remarks>
/// Registered as the last hosted service so it runs after the others are constructed but
/// before any of them does work — the host starts hosted services in registration order
/// and <see cref="BackgroundService"/> instances only begin after their
/// <c>StartAsync</c> returns, so the dispatcher never polls a table that does not exist
/// yet.
/// </remarks>
public sealed class SchemaMigrationStartupService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SchemaMigrationStartupService> _logger;

    public SchemaMigrationStartupService(
        IServiceProvider services,
        ILogger<SchemaMigrationStartupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _services.MigrateFlowPayDatabaseAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _services
                .DeclareFlowPayTopologyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The consumers declare the topology themselves on connect, so a broker that
            // is not up yet is not a startup failure — the worker will keep retrying.
            _logger.LogWarning(
                failure,
                "Could not declare the RabbitMQ topology at startup; consumers will retry.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
