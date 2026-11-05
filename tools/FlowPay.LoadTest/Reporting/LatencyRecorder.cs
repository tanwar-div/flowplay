using System.Diagnostics;

namespace FlowPay.LoadTest.Reporting;

/// <summary>
/// Collects per-request latencies and computes percentiles from the full sample.
/// </summary>
/// <remarks>
/// <para>
/// Every measurement is kept rather than summarized into a streaming estimator. The
/// figure that matters is p95, and approximate percentiles are least accurate exactly
/// where the tail lives. At a few hundred thousand samples the memory cost is a few
/// megabytes, which is nothing next to being able to state the number exactly.
/// </para>
/// <para>
/// Each worker records into its own recorder and the results are merged at the end, so
/// there is no shared counter for the measurement loop to contend on. A lock on the hot
/// path would show up in the very latencies being measured.
/// </para>
/// </remarks>
public sealed class LatencyRecorder
{
    private readonly List<long> _ticks;

    public LatencyRecorder(int expectedSamples = 1024)
        => _ticks = new List<long>(Math.Max(expectedSamples, 16));

    public int Count => _ticks.Count;

    public void Record(long elapsedTicks) => _ticks.Add(elapsedTicks);

    public void Record(TimeSpan elapsed) => _ticks.Add(elapsed.Ticks);

    /// <summary>Combines per-worker recorders into one sample set.</summary>
    public static LatencySummary Summarize(IEnumerable<LatencyRecorder> recorders)
    {
        List<long> all = [];

        foreach (LatencyRecorder recorder in recorders)
        {
            all.AddRange(recorder._ticks);
        }

        return LatencySummary.FromTicks(all);
    }
}

/// <summary>Percentiles over a complete latency sample.</summary>
public sealed record LatencySummary(
    int Count,
    double MinMs,
    double MeanMs,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms,
    double P999Ms,
    double MaxMs)
{
    public static LatencySummary Empty { get; } =
        new(0, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d);

    public static LatencySummary FromTicks(List<long> ticks)
    {
        if (ticks.Count == 0)
        {
            return Empty;
        }

        ticks.Sort();

        double total = 0d;
        foreach (long value in ticks)
        {
            total += value;
        }

        return new LatencySummary(
            ticks.Count,
            ToMilliseconds(ticks[0]),
            ToMilliseconds(total / ticks.Count),
            Percentile(ticks, 0.50d),
            Percentile(ticks, 0.90d),
            Percentile(ticks, 0.95d),
            Percentile(ticks, 0.99d),
            Percentile(ticks, 0.999d),
            ToMilliseconds(ticks[^1]));
    }

    /// <summary>
    /// The nearest-rank percentile: the smallest value at or below which the given
    /// fraction of samples fall.
    /// </summary>
    /// <remarks>
    /// Nearest-rank rather than an interpolating definition, because an interpolated p95
    /// reports a latency no request actually experienced. For a latency objective, the
    /// honest answer is a real observation.
    /// </remarks>
    private static double Percentile(List<long> sorted, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return ToMilliseconds(sorted[Math.Clamp(rank, 0, sorted.Count - 1)]);
    }

    private static double ToMilliseconds(double ticks)
        => ticks / TimeSpan.TicksPerMillisecond;
}

/// <summary>A stopwatch scoped to one request.</summary>
public readonly struct LatencyScope
{
    private readonly long _startTimestamp;

    private LatencyScope(long startTimestamp) => _startTimestamp = startTimestamp;

    public static LatencyScope Start() => new(Stopwatch.GetTimestamp());

    public TimeSpan Stop() => Stopwatch.GetElapsedTime(_startTimestamp);
}
