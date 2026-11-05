using System.Diagnostics;
using FlowPay.LoadTest.Reporting;
using Spectre.Console;

namespace FlowPay.LoadTest.Scenarios;

/// <summary>
/// A large batch of payments, then a full invariant check over everything it produced.
/// </summary>
/// <remarks>
/// The point is not throughput but correctness at volume. A hundred thousand payments
/// across dozens of accounts exercise lock ordering, reservation accounting and the
/// double-entry ledger under real contention, and the invariant check afterwards is what
/// turns "it did not crash" into "every cent is accounted for".
/// </remarks>
public static class SoakScenario
{
    public static async Task<ScenarioResult> RunAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        LoadTestOptions options = context.Options;

        await ScenarioSetup.WarmUpAsync(context, cancellationToken);

        LatencyRecorder[] recorders = new LatencyRecorder[options.Concurrency];
        OutcomeTally outcomes = new();

        int remaining = options.Operations;
        Stopwatch elapsed = Stopwatch.StartNew();

        Task[] workers = new Task[options.Concurrency];

        for (int worker = 0; worker < options.Concurrency; worker++)
        {
            int workerIndex = worker;
            recorders[workerIndex] = new LatencyRecorder(
                (options.Operations / options.Concurrency) + 64);

            workers[workerIndex] = Task.Run(
                async () =>
                {
                    Random random = new(Seed: 5_000 + workerIndex);
                    LatencyRecorder recorder = recorders[workerIndex];

                    // A shared countdown rather than a fixed slice each: a worker that
                    // draws slow account pairs does not leave the run waiting on it
                    // while the others sit idle.
                    while (Interlocked.Decrement(ref remaining) >= 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        (Guid source, Guid destination) = context.PickPair(random);

                        CallResult result = await context.Client.CreatePaymentAsync(
                            source,
                            destination,
                            options.AmountMinorUnits,
                            options.Currency,
                            ScenarioContext.NewIdempotencyKey(),
                            capture: true,
                            cancellationToken);

                        recorder.Record(result.Elapsed);

                        // A declined payment is a correct outcome, not a failure: the
                        // accounts are finite and the run deliberately spends them down.
                        // Counting declines as errors would make a successful soak look
                        // broken.
                        string classification = Classify(result);
                        outcomes.Record(result, classification);
                    }
                },
                cancellationToken);
        }

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async progress =>
            {
                ProgressTask task = progress.AddTask(
                    $"[green]{options.Operations:N0} payments[/]", maxValue: options.Operations);

                while (!Task.WhenAll(workers).IsCompleted)
                {
                    task.Value = Math.Min(outcomes.Total, options.Operations);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None);

                    if (workers.All(worker => worker.IsCompleted))
                    {
                        break;
                    }
                }

                task.Value = task.MaxValue;
            });

        await Task.WhenAll(workers);
        elapsed.Stop();

        InvariantView invariants = await AnsiConsole.Status()
            .StartAsync(
                "Checking ledger invariants...",
                async _ => await context.Client.CheckInvariantsAsync(cancellationToken));

        return new ScenarioResult(
            $"Soak ({options.Operations:N0} payments)",
            LatencyRecorder.Summarize(recorders),
            outcomes,
            elapsed.Elapsed,
            invariants);
    }

    private static string Classify(CallResult result)
    {
        if (!result.IsSuccess)
        {
            return $"{(int)result.Status} {result.Status}";
        }

        PaymentView? payment = LoadTestClient.ParsePayment(result.Body);

        return payment is null
            ? "200 unparsed"
            : $"payment {payment.Status}";
    }
}
