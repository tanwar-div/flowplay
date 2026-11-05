using System.Globalization;

namespace FlowPay.LoadTest;

/// <summary>The scenarios the harness can run.</summary>
public enum ScenarioKind
{
    /// <summary>Sustained request rate with a latency distribution.</summary>
    Throughput,

    /// <summary>A large batch of payments followed by a full invariant check.</summary>
    Soak,

    /// <summary>Repeated replays of one idempotency key.</summary>
    Replay,

    /// <summary>Concurrent refunds against a single payment.</summary>
    RefundStorm,

    /// <summary>The invariant check on its own.</summary>
    Verify,
}

/// <summary>Parsed command-line options.</summary>
public sealed class LoadTestOptions
{
    public ScenarioKind Scenario { get; private set; } = ScenarioKind.Throughput;

    public Uri BaseAddress { get; private set; } = new("http://localhost:5080");

    /// <summary>Concurrent in-flight requests.</summary>
    public int Concurrency { get; private set; } = 64;

    /// <summary>
    /// Target requests per second, or zero for unthrottled.
    /// </summary>
    /// <remarks>
    /// A throughput run should be paced rather than open-throttle. Firing as fast as the
    /// client can means the measured latency is dominated by the queue the harness itself
    /// built, which says nothing about how the service behaves at its actual load.
    /// </remarks>
    public int TargetRps { get; private set; } = 400;

    /// <summary>How long a throughput run lasts.</summary>
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(60);

    /// <summary>Total operations for the soak and replay scenarios.</summary>
    public int Operations { get; private set; } = 100_000;

    /// <summary>Payer and payee accounts created for the run.</summary>
    public int AccountCount { get; private set; } = 64;

    /// <summary>Opening balance for each generated account, in minor units.</summary>
    public long OpeningBalanceMinorUnits { get; private set; } = 500_000_000L;

    /// <summary>Payment amount in minor units.</summary>
    public long AmountMinorUnits { get; private set; } = 2_500L;

    public string Currency { get; private set; } = "USD";

    /// <summary>Per-request timeout.</summary>
    public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Requests issued before measurement starts.
    /// </summary>
    /// <remarks>
    /// The first requests a .NET service handles are served by tier-0 JIT output, with
    /// cold connection pools and an empty query plan cache. Including them would put the
    /// cost of starting up into a steady-state latency figure.
    /// </remarks>
    public int WarmupRequests { get; private set; } = 500;

    /// <summary>Write the latency distribution to this CSV path.</summary>
    public string? CsvOutputPath { get; private set; }

    public static bool TryParse(string[] args, out LoadTestOptions options, out string? error)
    {
        options = new LoadTestOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Unexpected argument '{argument}'.";
                return false;
            }

            string name = argument[2..];

            string? Next()
            {
                if (i + 1 >= args.Length)
                {
                    return null;
                }

                return args[++i];
            }

            switch (name)
            {
                case "scenario":
                    if (!Enum.TryParse(Next(), ignoreCase: true, out ScenarioKind scenario))
                    {
                        error = "Expected --scenario throughput|soak|replay|refundstorm|verify.";
                        return false;
                    }

                    options.Scenario = scenario;
                    break;

                case "url":
                    if (!Uri.TryCreate(Next(), UriKind.Absolute, out Uri? url))
                    {
                        error = "Expected --url with an absolute URI.";
                        return false;
                    }

                    options.BaseAddress = url;
                    break;

                case "concurrency":
                    if (!TryReadInt(Next(), 1, 10_000, out int concurrency, ref error, name))
                    {
                        return false;
                    }

                    options.Concurrency = concurrency;
                    break;

                case "rps":
                    if (!TryReadInt(Next(), 0, 1_000_000, out int rps, ref error, name))
                    {
                        return false;
                    }

                    options.TargetRps = rps;
                    break;

                case "duration":
                    if (!TryReadInt(Next(), 1, 86_400, out int seconds, ref error, name))
                    {
                        return false;
                    }

                    options.Duration = TimeSpan.FromSeconds(seconds);
                    break;

                case "operations":
                    if (!TryReadInt(Next(), 1, 10_000_000, out int operations, ref error, name))
                    {
                        return false;
                    }

                    options.Operations = operations;
                    break;

                case "accounts":
                    if (!TryReadInt(Next(), 2, 100_000, out int accounts, ref error, name))
                    {
                        return false;
                    }

                    options.AccountCount = accounts;
                    break;

                case "amount":
                    if (!TryReadLong(Next(), 1L, 1_000_000_000L, out long amount, ref error, name))
                    {
                        return false;
                    }

                    options.AmountMinorUnits = amount;
                    break;

                case "balance":
                    if (!TryReadLong(Next(), 0L, 1_000_000_000_000L, out long balance, ref error, name))
                    {
                        return false;
                    }

                    options.OpeningBalanceMinorUnits = balance;
                    break;

                case "currency":
                    options.Currency = Next()?.ToUpperInvariant() ?? "USD";
                    break;

                case "warmup":
                    if (!TryReadInt(Next(), 0, 100_000, out int warmup, ref error, name))
                    {
                        return false;
                    }

                    options.WarmupRequests = warmup;
                    break;

                case "timeout":
                    if (!TryReadInt(Next(), 1, 600, out int timeout, ref error, name))
                    {
                        return false;
                    }

                    options.RequestTimeout = TimeSpan.FromSeconds(timeout);
                    break;

                case "csv":
                    options.CsvOutputPath = Next();
                    break;

                default:
                    error = $"Unknown option '--{name}'.";
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadInt(
        string? raw, int min, int max, out int value, ref string? error, string name)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || value < min
            || value > max)
        {
            error = $"Expected --{name} to be an integer between {min} and {max}.";
            return false;
        }

        return true;
    }

    private static bool TryReadLong(
        string? raw, long min, long max, out long value, ref string? error, string name)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || value < min
            || value > max)
        {
            error = $"Expected --{name} to be an integer between {min} and {max}.";
            return false;
        }

        return true;
    }

    public const string Usage =
        """
        FlowPay load and correctness harness.

          --scenario   throughput | soak | replay | refundstorm | verify   (default: throughput)
          --url        API base address                                     (default: http://localhost:5080)
          --concurrency  in-flight requests                                 (default: 64)
          --rps        target requests per second, 0 for unthrottled        (default: 400)
          --duration   throughput run length in seconds                     (default: 60)
          --operations total operations for soak and replay                 (default: 100000)
          --accounts   accounts to provision                                (default: 64)
          --amount     payment amount in minor units                        (default: 2500)
          --balance    opening balance per account in minor units           (default: 500000000)
          --currency   ISO-4217 code                                        (default: USD)
          --warmup     unmeasured requests before the run                   (default: 500)
          --timeout    per-request timeout in seconds                       (default: 30)
          --csv        write the latency distribution to this path

        Examples:
          flowpay-loadtest --scenario throughput --rps 400 --duration 120
          flowpay-loadtest --scenario soak --operations 100000
          flowpay-loadtest --scenario replay --operations 10000
          flowpay-loadtest --scenario refundstorm --concurrency 64
          flowpay-loadtest --scenario verify
        """;
}
