using System.Diagnostics;
using System.Net;
using FlowPay.LoadTest.Reporting;
using Spectre.Console;

namespace FlowPay.LoadTest.Scenarios;

/// <summary>
/// Drives many concurrent refunds at single payments and checks none is over-refunded.
/// </summary>
/// <remarks>
/// <para>
/// Each round captures a payment and then fires refunds at it from every worker at once,
/// with the refunds summing to several times the captured amount. The correct outcome is
/// that exactly as many succeed as the payment can absorb and the rest are rejected —
/// never that the total overshoots.
/// </para>
/// <para>
/// This is the scenario that would expose a missing row lock. Without
/// <c>SELECT ... FOR UPDATE</c> on the payment, concurrent refunds all read the same
/// pre-refund total, all find room, and all commit.
/// </para>
/// </remarks>
public static class RefundStormScenario
{
    public static async Task<RefundStormResult> RunAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        LoadTestOptions options = context.Options;

        // Enough rounds that a rare interleaving has a chance to show up, and small
        // enough that the run stays quick.
        int rounds = Math.Max(1, Math.Min(options.Operations / Math.Max(options.Concurrency, 1), 200));

        // A round's payment must be divisible into refund chunks, and the payee needs
        // funds to give back.
        long paymentAmount = options.AmountMinorUnits * 10L;
        long refundChunk = options.AmountMinorUnits;

        LatencyRecorder recorder = new(rounds * options.Concurrency);
        OutcomeTally outcomes = new();

        int overRefundedRounds = 0;
        int roundsCompleted = 0;

        Stopwatch elapsed = Stopwatch.StartNew();

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async progress =>
            {
                ProgressTask task = progress.AddTask(
                    $"[green]{rounds} refund storms x {options.Concurrency} concurrent refunds[/]",
                    maxValue: rounds);

                for (int round = 0; round < rounds && !cancellationToken.IsCancellationRequested; round++)
                {
                    Guid payer = context.Accounts[round % context.Accounts.Count];
                    Guid payee = context.Accounts[(round + 1) % context.Accounts.Count];

                    CallResult created = await context.Client.CreatePaymentAsync(
                        payer,
                        payee,
                        paymentAmount,
                        options.Currency,
                        ScenarioContext.NewIdempotencyKey(),
                        capture: true,
                        cancellationToken);

                    PaymentView? payment = LoadTestClient.ParsePayment(created.Body);

                    if (payment is null || payment.Status != "Captured")
                    {
                        // The payer ran out of funds. Not a correctness problem; the
                        // round simply has nothing to refund.
                        task.Increment(1);
                        continue;
                    }

                    CallResult[] refunds = await Task.WhenAll(
                        Enumerable.Range(0, options.Concurrency).Select(_ =>
                            context.Client.RefundAsync(
                                payment.Id,
                                refundChunk,
                                options.Currency,
                                ScenarioContext.NewIdempotencyKey(),
                                cancellationToken)));

                    foreach (CallResult refund in refunds)
                    {
                        lock (recorder)
                        {
                            recorder.Record(refund.Elapsed);
                        }

                        outcomes.Record(refund, Classify(refund));
                    }

                    CallResult reloaded = await context.Client.GetPaymentAsync(
                        payment.Id, cancellationToken);

                    PaymentView? settled = LoadTestClient.ParsePayment(reloaded.Body);

                    // The invariant, checked per round so a failure is attributable to
                    // the payment that broke it rather than lost in an aggregate.
                    if (settled is not null && settled.Refunded.MinorUnits > settled.Amount.MinorUnits)
                    {
                        Interlocked.Increment(ref overRefundedRounds);
                    }

                    roundsCompleted++;
                    task.Increment(1);
                }
            });

        elapsed.Stop();

        InvariantView invariants = await context.Client.CheckInvariantsAsync(cancellationToken);

        return new RefundStormResult(
            new ScenarioResult(
                $"Refund storm ({rounds} rounds x {options.Concurrency} concurrent)",
                LatencySummaryOf(recorder),
                outcomes,
                elapsed.Elapsed,
                invariants),
            roundsCompleted,
            overRefundedRounds);
    }

    private static LatencySummary LatencySummaryOf(LatencyRecorder recorder)
        => LatencyRecorder.Summarize([recorder]);

    private static string Classify(CallResult refund) => refund.Status switch
    {
        HttpStatusCode.Created => "refund accepted",
        HttpStatusCode.UnprocessableEntity => "refund rejected (no headroom)",
        HttpStatusCode.Conflict => "refund rejected (conflict)",
        _ => $"{(int)refund.Status} {refund.Status}",
    };
}

/// <summary>The refund storm's correctness findings alongside its timings.</summary>
public sealed record RefundStormResult(
    ScenarioResult Scenario,
    int RoundsCompleted,
    int OverRefundedRounds)
{
    public bool IsCorrect =>
        OverRefundedRounds == 0 && Scenario.Invariants?.Healthy == true;
}
