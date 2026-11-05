using FlowPay.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlowPay.Worker.Hosting;

/// <summary>
/// Deletes inbox records once they are older than any possible redelivery.
/// </summary>
/// <remarks>
/// <para>
/// The retention window is the part that matters. It has to exceed the longest route a
/// redelivery can take — the full staged-retry schedule, plus however long a message can
/// sit in a dead-letter queue before an operator requeues it — because a record deleted
/// too early means a late redelivery finds no claim and applies its effects a second time.
/// </para>
/// <para>
/// Seven days is generous against a retry schedule measured in minutes, and the table is
/// narrow enough that a week of records costs little.
/// </para>
/// </remarks>
public sealed class InboxPurgeService : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int BatchSize = 10_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InboxPurgeService> _logger;

    public InboxPurgeService(
        IServiceScopeFactory scopeFactory,
        ILogger<InboxPurgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                IInboxStore inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();

                int deleted = await inbox
                    .PurgeOlderThanAsync(Retention, BatchSize, stoppingToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    _logger.LogInformation("Purged {Count} inbox records.", deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Inbox purge failed.");
            }
        }
    }
}
