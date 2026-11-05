using System.Collections.Concurrent;
using FlowPay.LoadTest.Reporting;
using Spectre.Console;

namespace FlowPay.LoadTest.Scenarios;

/// <summary>Counts of each outcome a scenario produced.</summary>
public sealed class OutcomeTally
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    private int _succeeded;
    private int _failed;
    private int _replayed;

    public int Succeeded => Volatile.Read(ref _succeeded);

    public int Failed => Volatile.Read(ref _failed);

    public int Replayed => Volatile.Read(ref _replayed);

    public int Total => Succeeded + Failed;

    public void Record(CallResult result, string? classification = null)
    {
        if (result.IsSuccess)
        {
            Interlocked.Increment(ref _succeeded);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        if (result.IsIdempotentReplay)
        {
            Interlocked.Increment(ref _replayed);
        }

        // Status codes are tallied individually so a run that "mostly worked" still says
        // what the remainder did: 409s under contention and 422s from business rules mean
        // very different things.
        string key = classification ?? $"{(int)result.Status} {result.Status}";
        _counts.AddOrUpdate(key, 1, static (_, existing) => existing + 1);
    }

    public IReadOnlyDictionary<string, int> Breakdown => _counts;
}

/// <summary>Shared state a scenario needs: the client, the accounts, the options.</summary>
public sealed class ScenarioContext
{
    public ScenarioContext(
        LoadTestClient client,
        LoadTestOptions options,
        IReadOnlyList<Guid> accounts)
    {
        Client = client;
        Options = options;
        Accounts = accounts;
    }

    public LoadTestClient Client { get; }

    public LoadTestOptions Options { get; }

    /// <summary>The accounts provisioned for this run.</summary>
    public IReadOnlyList<Guid> Accounts { get; }

    /// <summary>
    /// Picks a distinct payer and payee.
    /// </summary>
    /// <remarks>
    /// Spreading traffic across many account pairs is what makes a throughput run
    /// meaningful. Every payment sharing one payer would serialize on that account's row
    /// lock, and the result would measure lock contention rather than the service.
    /// </remarks>
    public (Guid Source, Guid Destination) PickPair(Random random)
    {
        int source = random.Next(Accounts.Count);
        int destination = random.Next(Accounts.Count - 1);

        if (destination >= source)
        {
            destination++;
        }

        return (Accounts[source], Accounts[destination]);
    }

    public static string NewIdempotencyKey() => $"lt-{Guid.CreateVersion7():n}";
}

/// <summary>Provisions the accounts a run needs.</summary>
public static class ScenarioSetup
{
    public static async Task<IReadOnlyList<Guid>> ProvisionAccountsAsync(
        LoadTestClient client,
        LoadTestOptions options,
        CancellationToken cancellationToken)
    {
        Guid[] accounts = new Guid[options.AccountCount];

        await AnsiConsole.Status()
            .StartAsync(
                $"Provisioning {options.AccountCount} {options.Currency} accounts...",
                async _ =>
                {
                    for (int i = 0; i < accounts.Length; i++)
                    {
                        accounts[i] = await client.CreateAccountAsync(
                            options.Currency,
                            options.OpeningBalanceMinorUnits,
                            $"loadtest-{i:D5}",
                            cancellationToken);
                    }
                });

        return accounts;
    }

    /// <summary>
    /// Issues unmeasured requests so the first measured one is not paying for JIT
    /// tiering, cold connection pools and an empty query plan cache.
    /// </summary>
    public static async Task WarmUpAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        if (context.Options.WarmupRequests <= 0)
        {
            return;
        }

        Random random = new(Seed: 7);

        await AnsiConsole.Status()
            .StartAsync($"Warming up ({context.Options.WarmupRequests} requests)...", async _ =>
            {
                for (int i = 0; i < context.Options.WarmupRequests; i++)
                {
                    (Guid source, Guid destination) = context.PickPair(random);

                    await context.Client.CreatePaymentAsync(
                        source,
                        destination,
                        context.Options.AmountMinorUnits,
                        context.Options.Currency,
                        ScenarioContext.NewIdempotencyKey(),
                        capture: true,
                        cancellationToken);
                }
            });
    }
}
