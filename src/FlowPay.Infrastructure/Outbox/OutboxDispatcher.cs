using System.Diagnostics;
using FlowPay.Application.Abstractions;
using FlowPay.Application.Outbox;
using FlowPay.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowPay.Infrastructure.Outbox;

/// <summary>
/// Moves committed outbox rows to the broker.
/// </summary>
/// <remarks>
/// <para>
/// One cycle is: open a transaction, claim a batch with <c>FOR UPDATE SKIP LOCKED</c>,
/// publish each row and wait for the broker's confirmation, mark each row's outcome, and
/// commit. Because the claim holds row locks for the duration, several dispatcher
/// instances can run this loop against the same table concurrently and never contend for
/// the same row.
/// </para>
/// <para>
/// A row whose publish fails is not lost and does not block the batch: it keeps its
/// pending status, its attempt count goes up, and <c>available_at</c> moves into the
/// future by an exponentially growing, jittered delay. After the attempt budget is spent
/// the row is parked as failed, where a health check can see it and an operator can
/// requeue it.
/// </para>
/// <para>
/// A crash between a successful publish and the commit re-delivers the batch on the next
/// cycle, which is why delivery is at-least-once and why consumers deduplicate through
/// the inbox table. The alternative — marking rows dispatched before publishing — would
/// turn a crash into permanently lost events, which is strictly worse than a duplicate a
/// consumer can detect.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly ActivitySource Activity = new("FlowPay.Outbox");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventPublisher _publisher;
    private readonly OutboxMetrics _metrics;
    private readonly FlowPayOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IEventPublisher publisher,
        OutboxMetrics metrics,
        IOptions<FlowPayOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _metrics = metrics;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox dispatcher started: batch size {BatchSize}, poll interval {PollInterval}.",
            _options.Outbox.BatchSize,
            _options.Outbox.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            int dispatched;

            try
            {
                dispatched = await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                // A cycle-level failure means the database or the broker is unreachable.
                // The loop must survive it: the rows are still durable, and the next
                // cycle will pick them up. Backing off keeps a hard-down dependency from
                // becoming a hot error loop.
                _logger.LogError(failure, "Outbox dispatch cycle failed; backing off.");
                _metrics.RecordCycleFailure();

                await DelayAsync(_options.Outbox.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // A full batch means there is probably more waiting, so the next cycle starts
            // immediately. Only an under-full batch means the queue is drained and it is
            // worth sleeping.
            if (dispatched < _options.Outbox.BatchSize)
            {
                await DelayAsync(_options.Outbox.PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Outbox dispatcher stopped.");
    }

    /// <summary>
    /// Runs a single dispatch cycle and returns how many rows were claimed.
    /// </summary>
    /// <remarks>
    /// Public so tests and operational tooling can drain the outbox deterministically.
    /// Asserting on a background loop means sleeping and hoping; calling one cycle and
    /// checking its result makes the outbox tests exact instead of timing-dependent.
    /// </remarks>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IUnitOfWork unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        IClock clock = scope.ServiceProvider.GetRequiredService<IClock>();

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IReadOnlyList<OutboxMessage> batch = await store
                .ClaimPendingAsync(_options.Outbox.BatchSize, cancellationToken)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }

            int succeeded = 0;

            foreach (OutboxMessage message in batch)
            {
                using Activity? span = Activity.StartActivity("outbox.publish");
                span?.SetTag("messaging.message.id", message.Id);
                span?.SetTag("messaging.destination.name", message.EventType);

                try
                {
                    await _publisher.PublishAsync(message, cancellationToken).ConfigureAwait(false);

                    message.MarkDispatched(clock.UtcNow);
                    succeeded++;
                    _metrics.RecordDispatched(message.EventType);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // One bad message must not stall the ones behind it. Recording the
                    // failure on the row and continuing means a single unroutable event
                    // cannot block an entire payment stream.
                    message.MarkAttemptFailed(
                        failure.Message,
                        _options.Outbox.MaxAttempts,
                        clock.UtcNow,
                        _options.Outbox.RetryBaseDelay,
                        _options.Outbox.RetryMaxDelay,
                        _options.Outbox.RetryJitterFactor);

                    _metrics.RecordPublishFailure(message.EventType);

                    if (message.Status == OutboxStatus.Failed)
                    {
                        _logger.LogError(
                            failure,
                            "Outbox message {MessageId} ({EventType}) parked after {Attempts} attempts.",
                            message.Id, message.EventType, message.Attempts);
                    }
                    else
                    {
                        _logger.LogWarning(
                            failure,
                            "Outbox message {MessageId} ({EventType}) failed attempt {Attempts}; retrying at {AvailableAt}.",
                            message.Id, message.EventType, message.Attempts, message.AvailableAt);
                    }
                }
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (succeeded > 0)
            {
                _logger.LogDebug(
                    "Dispatched {Succeeded}/{Total} outbox messages.", succeeded, batch.Count);
            }

            return batch.Count;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The caller's loop condition handles it.
        }
    }
}
