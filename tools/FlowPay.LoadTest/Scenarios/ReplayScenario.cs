using System.Diagnostics;
using FlowPay.LoadTest.Reporting;
using Spectre.Console;

namespace FlowPay.LoadTest.Scenarios;

/// <summary>
/// Hammers one idempotency key and proves only one financial effect results.
/// </summary>
/// <remarks>
/// <para>
/// A real client retries after a timeout, a dropped connection, or a deploy that
/// restarted the process mid-request. This scenario is that situation taken to its limit:
/// a single key submitted tens of thousands of times, concurrently, long after the
/// original has committed.
/// </para>
/// <para>
/// The assertions are what make it a correctness test rather than a load test. Every
/// response must name the same payment, and the payer's balance afterwards must differ
/// from its opening balance by exactly one payment — which is checked directly rather
/// than inferred from the responses.
/// </para>
/// </remarks>
public static class ReplayScenario
{
    public static async Task<ReplayResult> RunAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        LoadTestOptions options = context.Options;

        Guid payer = context.Accounts[0];
        Guid payee = context.Accounts[1];

        AccountView before = await context.Client.GetAccountAsync(payer, cancellationToken);

        string sharedKey = ScenarioContext.NewIdempotencyKey();

        // The first attempt runs alone, so the comparison afterwards is against a known
        // original rather than whichever concurrent attempt happened to win.
        CallResult original = await context.Client.CreatePaymentAsync(
            payer,
            payee,
            options.AmountMinorUnits,
            options.Currency,
            sharedKey,
            capture: true,
            cancellationToken);

        PaymentView? originalPayment = LoadTestClient.ParsePayment(original.Body);

        if (originalPayment is null)
        {
            throw new InvalidOperationException(
                $"The first attempt did not return a payment ({original.Status}): {original.Body}");
        }

        LatencyRecorder[] recorders = new LatencyRecorder[options.Concurrency];
        OutcomeTally outcomes = new();

        int remaining = options.Operations;
        int divergentPayments = 0;
        int missingPayments = 0;

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
                    LatencyRecorder recorder = recorders[workerIndex];

                    while (Interlocked.Decrement(ref remaining) >= 0)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        CallResult result = await context.Client.CreatePaymentAsync(
                            payer,
                            payee,
                            options.AmountMinorUnits,
                            options.Currency,
                            sharedKey,
                            capture: true,
                            cancellationToken);

                        recorder.Record(result.Elapsed);
                        outcomes.Record(result);

                        PaymentView? replayed = LoadTestClient.ParsePayment(result.Body);

                        if (replayed is null)
                        {
                            // A 409 "still in flight" carries no payment and is a correct
                            // answer, so only a successful response without one counts as
                            // a problem.
                            if (result.IsSuccess)
                            {
                                Interlocked.Increment(ref missingPayments);
                            }
                        }
                        else if (replayed.Id != originalPayment.Id)
                        {
                            // The failure this scenario exists to detect: a second
                            // payment created under a key that had already been used.
                            Interlocked.Increment(ref divergentPayments);
                        }
                    }
                },
                cancellationToken);
        }

        await AnsiConsole.Status().StartAsync(
            $"Replaying one idempotency key {options.Operations:N0} times across {options.Concurrency} workers...",
            async _ => await Task.WhenAll(workers));

        elapsed.Stop();

        AccountView after = await context.Client.GetAccountAsync(payer, cancellationToken);
        InvariantView invariants = await context.Client.CheckInvariantsAsync(cancellationToken);

        long actualDebit = before.Balance.MinorUnits - after.Balance.MinorUnits;

        return new ReplayResult(
            new ScenarioResult(
                $"Replay ({options.Operations:N0} attempts)",
                LatencyRecorder.Summarize(recorders),
                outcomes,
                elapsed.Elapsed,
                invariants),
            originalPayment.Id,
            ExpectedDebitMinorUnits: options.AmountMinorUnits,
            ActualDebitMinorUnits: actualDebit,
            DivergentPayments: divergentPayments,
            MissingPayments: missingPayments);
    }
}

/// <summary>The replay scenario's correctness findings alongside its timings.</summary>
public sealed record ReplayResult(
    ScenarioResult Scenario,
    Guid PaymentId,
    long ExpectedDebitMinorUnits,
    long ActualDebitMinorUnits,
    int DivergentPayments,
    int MissingPayments)
{
    /// <summary>
    /// True when every replay named the original payment and exactly one payment's worth
    /// of money moved.
    /// </summary>
    public bool IsCorrect =>
        DivergentPayments == 0
        && MissingPayments == 0
        && ActualDebitMinorUnits == ExpectedDebitMinorUnits
        && Scenario.Invariants?.Healthy == true;
}
