using System.Globalization;
using System.Text;
using FlowPay.LoadTest.Scenarios;
using Spectre.Console;

namespace FlowPay.LoadTest.Reporting;

/// <summary>Renders scenario results to the console and, optionally, to CSV.</summary>
public static class ConsoleReport
{
    /// <summary>The latency objective the throughput run is measured against.</summary>
    public const double P95TargetMs = 150d;

    /// <summary>The throughput objective, in requests per second.</summary>
    public const double RpsTarget = 400d;

    public static void RenderScenario(ScenarioResult result)
    {
        AnsiConsole.Write(new Rule($"[bold]{result.Name}[/]").LeftJustified());

        Table summary = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Metric")
            .AddColumn(new TableColumn("Value").RightAligned());

        summary.AddRow("Duration", $"{result.Elapsed.TotalSeconds:F2} s");
        summary.AddRow("Requests", $"{result.Outcomes.Total:N0}");
        summary.AddRow("Succeeded", $"{result.Outcomes.Succeeded:N0}");
        summary.AddRow("Failed", Highlight(result.Outcomes.Failed, good: result.Outcomes.Failed == 0));
        summary.AddRow("Idempotent replays", $"{result.Outcomes.Replayed:N0}");
        summary.AddRow(
            "Throughput",
            Highlight($"{result.AchievedRps:N1} rps", good: result.AchievedRps >= RpsTarget * 0.95d));

        AnsiConsole.Write(summary);

        if (result.Latency.Count > 0)
        {
            Table latency = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold]Latency[/]")
                .AddColumn("Percentile")
                .AddColumn(new TableColumn("Milliseconds").RightAligned());

            latency.AddRow("min", $"{result.Latency.MinMs:F2}");
            latency.AddRow("mean", $"{result.Latency.MeanMs:F2}");
            latency.AddRow("p50", $"{result.Latency.P50Ms:F2}");
            latency.AddRow("p90", $"{result.Latency.P90Ms:F2}");
            latency.AddRow(
                "[bold]p95[/]",
                Highlight($"{result.Latency.P95Ms:F2}", good: result.Latency.P95Ms <= P95TargetMs));
            latency.AddRow("p99", $"{result.Latency.P99Ms:F2}");
            latency.AddRow("p99.9", $"{result.Latency.P999Ms:F2}");
            latency.AddRow("max", $"{result.Latency.MaxMs:F2}");

            AnsiConsole.Write(latency);
        }

        if (result.Outcomes.Breakdown.Count > 0)
        {
            Table breakdown = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold]Outcomes[/]")
                .AddColumn("Outcome")
                .AddColumn(new TableColumn("Count").RightAligned());

            foreach ((string outcome, int count) in result.Outcomes.Breakdown
                .OrderByDescending(entry => entry.Value))
            {
                breakdown.AddRow(Markup.Escape(outcome), $"{count:N0}");
            }

            AnsiConsole.Write(breakdown);
        }

        if (result.Invariants is { } invariants)
        {
            RenderInvariants(invariants);
        }
    }

    public static void RenderInvariants(InvariantView invariants)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Ledger invariants[/]")
            .AddColumn("Check")
            .AddColumn(new TableColumn("Result").RightAligned());

        table.AddRow(
            "Ledger nets to zero",
            Highlight(
                $"{invariants.LedgerNetMinorUnits}",
                good: invariants.LedgerNetMinorUnits == 0L));

        table.AddRow(
            "Unbalanced payments",
            Highlight(invariants.UnbalancedPaymentCount, good: invariants.UnbalancedPaymentCount == 0));

        table.AddRow(
            "Over-refunded payments",
            Highlight(
                invariants.OverRefundedPaymentCount,
                good: invariants.OverRefundedPaymentCount == 0));

        table.AddRow(
            "Balance discrepancies",
            Highlight(
                invariants.BalanceDiscrepancyCount,
                good: invariants.BalanceDiscrepancyCount == 0));

        AnsiConsole.Write(table);

        if (!invariants.Healthy)
        {
            AnsiConsole.MarkupLine("[bold red]Invariants are broken:[/]");

            foreach (string detail in invariants.Details.Take(20))
            {
                AnsiConsole.MarkupLine($"  [red]-[/] {Markup.Escape(detail)}");
            }
        }
    }

    public static void RenderReplay(ReplayResult replay)
    {
        RenderScenario(replay.Scenario);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Idempotency[/]")
            .AddColumn("Check")
            .AddColumn(new TableColumn("Result").RightAligned());

        table.AddRow("Payment id", replay.PaymentId.ToString("d"));
        table.AddRow(
            "Replays returning a different payment",
            Highlight(replay.DivergentPayments, good: replay.DivergentPayments == 0));
        table.AddRow(
            "Successful replays without a payment",
            Highlight(replay.MissingPayments, good: replay.MissingPayments == 0));
        table.AddRow("Expected payer debit", $"{replay.ExpectedDebitMinorUnits:N0}");
        table.AddRow(
            "Actual payer debit",
            Highlight(
                $"{replay.ActualDebitMinorUnits:N0}",
                good: replay.ActualDebitMinorUnits == replay.ExpectedDebitMinorUnits));

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(replay.IsCorrect
            ? "[bold green]PASS[/] every replay resolved to one payment and one financial effect."
            : "[bold red]FAIL[/] duplicate financial effects were detected.");
    }

    public static void RenderRefundStorm(RefundStormResult storm)
    {
        RenderScenario(storm.Scenario);

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Refund concurrency[/]")
            .AddColumn("Check")
            .AddColumn(new TableColumn("Result").RightAligned());

        table.AddRow("Rounds completed", $"{storm.RoundsCompleted:N0}");
        table.AddRow(
            "Over-refunded payments",
            Highlight(storm.OverRefundedRounds, good: storm.OverRefundedRounds == 0));

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(storm.IsCorrect
            ? "[bold green]PASS[/] no payment was refunded beyond its captured amount."
            : "[bold red]FAIL[/] at least one payment was over-refunded.");
    }

    /// <summary>Writes the percentile summary to CSV for charting or tracking over time.</summary>
    public static async Task WriteCsvAsync(string path, ScenarioResult result)
    {
        StringBuilder builder = new();

        builder.AppendLine(
            "scenario,requests,succeeded,failed,duration_s,rps,min_ms,mean_ms,p50_ms,p90_ms,p95_ms,p99_ms,p999_ms,max_ms");

        builder.Append(CultureInfo.InvariantCulture, $"{Escape(result.Name)},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Outcomes.Total},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Outcomes.Succeeded},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Outcomes.Failed},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Elapsed.TotalSeconds:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.AchievedRps:F2},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.MinMs:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.MeanMs:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.P50Ms:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.P90Ms:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.P95Ms:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.P99Ms:F3},");
        builder.Append(CultureInfo.InvariantCulture, $"{result.Latency.P999Ms:F3},");
        builder.AppendLine(result.Latency.MaxMs.ToString("F3", CultureInfo.InvariantCulture));

        await File.WriteAllTextAsync(path, builder.ToString());

        AnsiConsole.MarkupLine($"Latency summary written to [blue]{Markup.Escape(path)}[/].");
    }

    private static string Escape(string value) => value.Replace(",", ";", StringComparison.Ordinal);

    private static string Highlight(int value, bool good)
        => Highlight(value.ToString("N0", CultureInfo.InvariantCulture), good);

    private static string Highlight(string value, bool good)
        => good ? $"[green]{value}[/]" : $"[red]{value}[/]";
}
