using System.Diagnostics.Metrics;

namespace FlowPay.Infrastructure.Outbox;

/// <summary>
/// Counters and gauges for the outbox, exported through OpenTelemetry.
/// </summary>
/// <remarks>
/// Backlog depth is the one number worth alerting on. Throughput and error counters say
/// what the dispatcher is doing, but a growing pending depth is the signal that events are
/// being produced faster than they are leaving — which is the failure mode that ends with
/// consumers working from stale data while every individual component reports healthy.
/// </remarks>
public sealed class OutboxMetrics
{
    /// <summary>The meter name to register with OpenTelemetry.</summary>
    public const string MeterName = "FlowPay.Outbox";

    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _publishFailures;
    private readonly Counter<long> _cycleFailures;

    private long _pendingDepth;
    private long _failedDepth;
    private long _oldestPendingAgeSeconds;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        Meter meter = meterFactory.Create(MeterName);

        _dispatched = meter.CreateCounter<long>(
            "flowpay.outbox.dispatched",
            unit: "{message}",
            description: "Outbox messages confirmed by the broker.");

        _publishFailures = meter.CreateCounter<long>(
            "flowpay.outbox.publish_failures",
            unit: "{message}",
            description: "Failed publish attempts for individual outbox messages.");

        _cycleFailures = meter.CreateCounter<long>(
            "flowpay.outbox.cycle_failures",
            unit: "{cycle}",
            description: "Dispatch cycles that failed outright.");

        meter.CreateObservableGauge(
            "flowpay.outbox.pending_depth",
            () => Interlocked.Read(ref _pendingDepth),
            unit: "{message}",
            description: "Outbox messages waiting to be published.");

        meter.CreateObservableGauge(
            "flowpay.outbox.failed_depth",
            () => Interlocked.Read(ref _failedDepth),
            unit: "{message}",
            description: "Outbox messages parked after exhausting their retries.");

        meter.CreateObservableGauge(
            "flowpay.outbox.oldest_pending_age",
            () => Interlocked.Read(ref _oldestPendingAgeSeconds),
            unit: "s",
            description: "Age of the oldest unpublished outbox message.");
    }

    public void RecordDispatched(string eventType)
        => _dispatched.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordPublishFailure(string eventType)
        => _publishFailures.Add(1, new KeyValuePair<string, object?>("event.type", eventType));

    public void RecordCycleFailure() => _cycleFailures.Add(1);

    /// <summary>Updates the observable gauges. Called by the depth monitor.</summary>
    public void UpdateDepth(long pending, long failed, long oldestPendingAgeSeconds)
    {
        Interlocked.Exchange(ref _pendingDepth, pending);
        Interlocked.Exchange(ref _failedDepth, failed);
        Interlocked.Exchange(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
    }
}
