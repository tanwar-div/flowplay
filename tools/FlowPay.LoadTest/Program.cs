using FlowPay.LoadTest;
using FlowPay.LoadTest.Reporting;
using FlowPay.LoadTest.Scenarios;
using Spectre.Console;

if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
{
    AnsiConsole.WriteLine(LoadTestOptions.Usage);
    return 0;
}

if (!LoadTestOptions.TryParse(args, out LoadTestOptions options, out string? parseError))
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(parseError ?? "Invalid arguments.")}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(LoadTestOptions.Usage);
    return 2;
}

using CancellationTokenSource cancellation = new();

// Ctrl+C cancels the run rather than killing the process, so a partial result is still
// reported and the invariant check still gets a chance to run.
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    AnsiConsole.MarkupLine("[yellow]Cancellation requested; finishing in-flight work.[/]");
    cancellation.Cancel();
};

AnsiConsole.Write(
    new FigletText("FlowPay").Color(Color.Teal));

AnsiConsole.MarkupLine(
    $"Target [blue]{Markup.Escape(options.BaseAddress.ToString())}[/] - scenario [green]{options.Scenario}[/]");
AnsiConsole.WriteLine();

using LoadTestClient client = new(
    options.BaseAddress, options.Concurrency, options.RequestTimeout);

if (!await client.WaitUntilReadyAsync(TimeSpan.FromSeconds(60), cancellation.Token))
{
    AnsiConsole.MarkupLine(
        "[red]The API did not become ready within 60 seconds. Is the stack running?[/]");
    return 3;
}

try
{
    switch (options.Scenario)
    {
        case ScenarioKind.Verify:
        {
            InvariantView invariants = await client.CheckInvariantsAsync(cancellation.Token);
            ConsoleReport.RenderInvariants(invariants);
            return invariants.Healthy ? 0 : 1;
        }

        case ScenarioKind.Throughput:
        {
            ScenarioContext context = await BuildContextAsync();
            ScenarioResult result = await ThroughputScenario.RunAsync(context, cancellation.Token);

            ConsoleReport.RenderScenario(result);
            await WriteCsvIfRequestedAsync(result);

            // The exit code reflects the objectives, so the harness is usable as a gate
            // in CI rather than something a person has to read and judge.
            bool met = result.Latency.P95Ms <= ConsoleReport.P95TargetMs
                && result.Outcomes.Failed == 0
                && result.Invariants?.Healthy == true;

            AnsiConsole.MarkupLine(met
                ? $"[bold green]PASS[/] p95 {result.Latency.P95Ms:F1} ms at {result.AchievedRps:F0} rps, ledger intact."
                : $"[bold red]FAIL[/] p95 {result.Latency.P95Ms:F1} ms (target {ConsoleReport.P95TargetMs:F0} ms), {result.Outcomes.Failed} failures.");

            return met ? 0 : 1;
        }

        case ScenarioKind.Soak:
        {
            ScenarioContext context = await BuildContextAsync();
            ScenarioResult result = await SoakScenario.RunAsync(context, cancellation.Token);

            ConsoleReport.RenderScenario(result);
            await WriteCsvIfRequestedAsync(result);

            bool healthy = result.Invariants?.Healthy == true;

            AnsiConsole.MarkupLine(healthy
                ? $"[bold green]PASS[/] {result.Outcomes.Total:N0} payments processed with the ledger intact."
                : "[bold red]FAIL[/] the ledger did not reconcile after the soak.");

            return healthy ? 0 : 1;
        }

        case ScenarioKind.Replay:
        {
            ScenarioContext context = await BuildContextAsync();
            ReplayResult replay = await ReplayScenario.RunAsync(context, cancellation.Token);

            ConsoleReport.RenderReplay(replay);
            await WriteCsvIfRequestedAsync(replay.Scenario);

            return replay.IsCorrect ? 0 : 1;
        }

        case ScenarioKind.RefundStorm:
        {
            ScenarioContext context = await BuildContextAsync();
            RefundStormResult storm = await RefundStormScenario.RunAsync(context, cancellation.Token);

            ConsoleReport.RenderRefundStorm(storm);
            await WriteCsvIfRequestedAsync(storm.Scenario);

            return storm.IsCorrect ? 0 : 1;
        }

        default:
            AnsiConsole.MarkupLine($"[red]Unsupported scenario '{options.Scenario}'.[/]");
            return 2;
    }
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]Run cancelled.[/]");
    return 130;
}
catch (Exception failure)
{
    AnsiConsole.MarkupLine($"[red]The run failed: {Markup.Escape(failure.Message)}[/]");
    AnsiConsole.WriteException(failure, ExceptionFormats.ShortenPaths);
    return 4;
}

async Task<ScenarioContext> BuildContextAsync()
{
    IReadOnlyList<Guid> accounts = await ScenarioSetup.ProvisionAccountsAsync(
        client, options, cancellation.Token);

    return new ScenarioContext(client, options, accounts);
}

async Task WriteCsvIfRequestedAsync(ScenarioResult result)
{
    if (options.CsvOutputPath is { Length: > 0 } path)
    {
        await ConsoleReport.WriteCsvAsync(path, result);
    }
}
