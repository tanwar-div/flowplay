using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Infrastructure.Outbox;

/// <summary>
/// Samples the outbox backlog so the metrics and health check have something to report.
/// </summary>
/// <remarks>
/// Kept out of the dispatch loop on purpose. Counting rows by status is an aggregate over
/// the whole table, and running it on every dispatch cycle would add that cost to the
/// path events actually travel. Sampling on its own slower schedule gives the same
/// operational visibility without taxing throughput.
/// </remarks>
public sealed class OutboxDepthMonitor : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxMetrics _metrics;
    private readonly OutboxHealthState _health;
    private readonly FlowPayOptions _options;
    private readonly ILogger<OutboxDepthMonitor> _logger;

    public OutboxDepthMonitor(
        IServiceScopeFactory scopeFactory,
        OutboxMetrics metrics,
        OutboxHealthState health,
        IOptions<FlowPayOptions> options,
        ILogger<OutboxDepthMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _metrics = metrics;
        _health = health;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SampleInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

                OutboxDepth depth = await store
                    .GetDepthAsync(stoppingToken)
                    .ConfigureAwait(false);

                _metrics.UpdateDepth(depth.Pending, depth.Failed, depth.OldestPendingAgeSeconds);
                _health.Update(depth, _options.Outbox.PendingDepthWarningThreshold);

                if (depth.Pending > _options.Outbox.PendingDepthWarningThreshold)
                {
                    _logger.LogWarning(
                        "Outbox backlog is {Pending} messages (oldest {AgeSeconds}s); dispatch is falling behind.",
                        depth.Pending, depth.OldestPendingAgeSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                _logger.LogError(failure, "Failed to sample outbox depth.");
            }
        }
    }
}

/// <summary>
/// The last observed outbox depth, shared with the health check endpoint.
/// </summary>
/// <remarks>
/// A cached snapshot rather than a query per health probe. Probes run every few seconds
/// from every replica and from the load balancer; letting each one run a table-wide
/// aggregate would mean the act of checking health degraded it.
/// </remarks>
public sealed class OutboxHealthState
{
    private long _pending;
    private long _failed;
    private long _oldestPendingAgeSeconds;
    private long _threshold = long.MaxValue;
    private long _lastSampleTicks;

    public void Update(OutboxDepth depth, long pendingThreshold)
    {
        Interlocked.Exchange(ref _pending, depth.Pending);
        Interlocked.Exchange(ref _failed, depth.Failed);
        Interlocked.Exchange(ref _oldestPendingAgeSeconds, depth.OldestPendingAgeSeconds);
        Interlocked.Exchange(ref _threshold, pendingThreshold);
        Interlocked.Exchange(ref _lastSampleTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public long Pending => Interlocked.Read(ref _pending);

    public long Failed => Interlocked.Read(ref _failed);

    public long OldestPendingAgeSeconds => Interlocked.Read(ref _oldestPendingAgeSeconds);

    public bool HasSample => Interlocked.Read(ref _lastSampleTicks) != 0L;

    /// <summary>
    /// True while the backlog is within its threshold and nothing is parked as failed.
    /// </summary>
    public bool IsHealthy =>
        Interlocked.Read(ref _pending) <= Interlocked.Read(ref _threshold)
        && Interlocked.Read(ref _failed) == 0L;
}
