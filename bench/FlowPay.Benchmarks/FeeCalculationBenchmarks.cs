using BenchmarkDotNet.Attributes;
using FlowPay.Domain.Primitives;

namespace FlowPay.Benchmarks;

/// <summary>
/// Fee calculation, and the comparison that justifies doing it in integers.
/// </summary>
/// <remarks>
/// The <c>Decimal</c> benchmarks are not dead code: they are the alternative this design
/// rejected, measured so the rejection is based on a number rather than an assumption.
/// They compute the same 2.9% + 30c fee the integer path does, with the same banker's
/// rounding, and the gap between them is the justification for
/// <see cref="Money"/> holding integer minor units.
/// </remarks>
[MemoryDiagnoser]
public class FeeCalculationBenchmarks
{
    private FeeSchedule _schedule;
    private Money _small;
    private Money _typical;
    private Money _large;

    private decimal _typicalDecimal;
    private const decimal DecimalRate = 0.029m;
    private const decimal DecimalFixed = 0.30m;

    [GlobalSetup]
    public void Setup()
    {
        _schedule = FeeSchedule.Standard(Currency.Usd);
        _small = new Money(199L, Currency.Usd);
        _typical = new Money(129_950L, Currency.Usd);
        _large = new Money(99_999_999L, Currency.Usd);
        _typicalDecimal = 1299.50m;
    }

    [Benchmark(Baseline = true, Description = "Integer fee, typical amount")]
    public bool IntegerFeeTypical() => _schedule.TryComputeFee(_typical, out _);

    [Benchmark(Description = "Integer fee, small amount (fixed component dominates)")]
    public bool IntegerFeeSmall() => _schedule.TryComputeFee(_small, out _);

    [Benchmark(Description = "Integer fee, large amount")]
    public bool IntegerFeeLarge() => _schedule.TryComputeFee(_large, out _);

    [Benchmark(Description = "Integer fee plus net settlement in one pass")]
    public bool IntegerSettlement()
        => _schedule.TryComputeSettlement(_typical, out _, out _);

    /// <summary>
    /// The decimal equivalent, for comparison. Same inputs, same rounding mode.
    /// </summary>
    [Benchmark(Description = "Decimal fee, typical amount (rejected alternative)")]
    public decimal DecimalFeeTypical()
    {
        decimal proportional = Math.Round(
            _typicalDecimal * DecimalRate, 2, MidpointRounding.ToEven);

        return proportional + DecimalFixed;
    }

    [Benchmark(Description = "Decimal fee plus net settlement (rejected alternative)")]
    public decimal DecimalSettlement()
    {
        decimal fee = Math.Round(_typicalDecimal * DecimalRate, 2, MidpointRounding.ToEven)
            + DecimalFixed;

        return _typicalDecimal - fee;
    }
}
