using System.Diagnostics;
using FlowPay.LoadTest.Reporting;
using Spectre.Console;

namespace FlowPay.LoadTest.Scenarios;

/// <summary>The result of a measured run.</summary>
public sealed record ScenarioResult(
    string Name,
    LatencySummary Latency,
    OutcomeTally Outcomes,
    TimeSpan Elapsed,
    InvariantView? Invariants)
{
    public double AchievedRps => Elapsed.TotalSeconds <= 0d
        ? 0d
        : Outcomes.Total / Elapsed.TotalSeconds;
}

/// <summary>
/// A sustained, paced request rate with the resulting latency distribution.
/// </summary>
/// <remarks>
/// <para>
/// The run is rate-paced rather than open-throttle. Firing requests as fast as the client
/// can means each one queues behind the harness's own backlog, and the measured latency
/// becomes a property of the load generator instead of the service. Pacing to a target
/// rate measures what a client at that rate would actually experience.
/// </para>
/// <para>
/// Each worker holds its own schedule and its own recorder, so the measurement loop has
/// no shared mutable state to contend on.
/// </para>
/// </remarks>
public static class ThroughputScenario
{
    public static async Task<ScenarioResult> RunAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        LoadTestOptions options = context.Options;

        await ScenarioSetup.WarmUpAsync(context, cancellationToken);

        LatencyRecorder[] recorders = new LatencyRecorder[options.Concurrency];
        OutcomeTally outcomes = new();

        // The interval each worker waits between its own requests, so the workers
        // together approximate the target rate.
        TimeSpan? perWorkerInterval = options.TargetRps > 0
            ? TimeSpan.FromSeconds((double)options.Concurrency / options.TargetRps)
            : null;

        using CancellationTokenSource runCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        runCancellation.CancelAfter(options.Duration);

        Stopwatch elapsed = Stopwatch.StartNew();

        Task[] workers = new Task[options.Concurrency];

        for (int worker = 0; worker < options.Concurrency; worker++)
        {
            int workerIndex = worker;
            int expectedSamples = options.TargetRps > 0
                ? (int)(options.Duration.TotalSeconds * options.TargetRps / options.Concurrency) + 64
                : 4_096;

            recorders[workerIndex] = new LatencyRecorder(expectedSamples);

            workers[workerIndex] = Task.Run(
                () => RunWorkerAsync(
                    context,
                    recorders[workerIndex],
                    outcomes,
                    perWorkerInterval,
                    workerIndex,
                    runCancellation.Token),
                cancellationToken);
        }

        await AnsiConsole.Status().StartAsync(
            $"Running {options.Duration.TotalSeconds:F0}s at ~{options.TargetRps} rps across {options.Concurrency} workers...",
            async _ => await Task.WhenAll(workers));

        elapsed.Stop();

        InvariantView invariants = await context.Client.CheckInvariantsAsync(cancellationToken);

        return new ScenarioResult(
            "Throughput",
            LatencyRecorder.Summarize(recorders),
            outcomes,
            elapsed.Elapsed,
            invariants);
    }

    private static async Task RunWorkerAsync(
        ScenarioContext context,
        LatencyRecorder recorder,
        OutcomeTally outcomes,
        TimeSpan? interval,
        int workerIndex,
        CancellationToken cancellationToken)
    {
        // Seeded per worker so a run is reproducible while the workers still choose
        // different account pairs from one another.
        Random random = new(Seed: 1_000 + workerIndex);

        long nextTicks = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (interval is { } pace)
            {
                // The schedule advances by a fixed step rather than sleeping for the
                // interval after each request. Sleeping would let a slow response push
                // the whole schedule back, quietly lowering the rate exactly when the
                // service is struggling — which is when the measurement matters most.
                nextTicks += (long)(pace.TotalSeconds * Stopwatch.Frequency);

                TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextTicks);

                if (wait > TimeSpan.Zero && !await DelayAsync(wait, cancellationToken))
                {
                    return;
                }
            }

            (Guid source, Guid destination) = context.PickPair(random);

            CallResult result;

            try
            {
                result = await context.Client.CreatePaymentAsync(
                    source,
                    destination,
                    context.Options.AmountMinorUnits,
                    context.Options.Currency,
                    ScenarioContext.NewIdempotencyKey(),
                    capture: true,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The measurement window closed while this request was in flight. Its
                // latency is real but falls outside the window, so it is dropped rather
                // than recorded — and the window ending is the normal way this loop
                // finishes, not an error to propagate.
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            recorder.Record(result.Elapsed);
            outcomes.Record(result);
        }
    }

    /// <summary>Waits for the pacing interval. Returns false once the run is over.</summary>
    private static async Task<bool> DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(wait, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
