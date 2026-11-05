using BenchmarkDotNet.Attributes;
using FlowPay.Domain.Primitives;

namespace FlowPay.Benchmarks;

/// <summary>
/// The <see cref="Money"/> and <see cref="Currency"/> operations on the payment path.
/// </summary>
/// <remarks>
/// These run on every request, several times each: parsing the currency, checking the
/// amount, adding to a refunded total. The benchmarks assert they stay in the low
/// nanoseconds with no allocations, which is what justifies the integer-minor-unit and
/// packed-currency design over the obvious alternative of <see cref="decimal"/> plus a
/// currency <see cref="string"/>.
/// </remarks>
[MemoryDiagnoser]
public class MoneyBenchmarks
{
    private Money _left;
    private Money _right;
    private Money _refunded;
    private string _currencyCode = "USD";
    private string _amountText = "1250.75";

    [GlobalSetup]
    public void Setup()
    {
        Currency usd = Currency.Usd;
        _left = new Money(129_950L, usd);
        _right = new Money(4_799L, usd);
        _refunded = new Money(25_000L, usd);
        _currencyCode = "USD";
        _amountText = "1250.75";
    }

    [Benchmark(Description = "Currency.TryParse (3-char span to packed uint)")]
    public bool CurrencyTryParse() => Currency.TryParse(_currencyCode, out _);

    [Benchmark(Description = "Currency.IsSupported (packed integer compare)")]
    public bool CurrencyIsSupported() => Currency.Usd.IsSupported;

    [Benchmark(Description = "Money.TryAdd with overflow check")]
    public bool MoneyTryAdd() => _left.TryAdd(_right, out _);

    [Benchmark(Description = "Money.TrySubtract with overflow check")]
    public bool MoneyTrySubtract() => _left.TrySubtract(_right, out _);

    [Benchmark(Description = "Money.IsWithinOperationalLimit")]
    public bool MoneyLimitCheck() => _left.IsWithinOperationalLimit();

    [Benchmark(Description = "Money.TryParseMajorUnits (decimal text to minor units)")]
    public bool MoneyParse() => Money.TryParseMajorUnits(_amountText, Currency.Usd, out _);

    /// <summary>
    /// The refund headroom check, which is the comparison the over-refund invariant turns
    /// into a rejection.
    /// </summary>
    [Benchmark(Description = "Refund headroom check (add then compare)")]
    public bool RefundHeadroom()
    {
        if (!_refunded.TryAdd(_right, out Money total))
        {
            return false;
        }

        return total.MinorUnits <= _left.MinorUnits;
    }

    /// <summary>
    /// Splitting an amount across destinations, the one operation that writes into a
    /// caller-supplied buffer rather than returning a value.
    /// </summary>
    [Benchmark(Description = "Money.TryAllocate across 4 destinations")]
    public bool MoneyAllocate()
    {
        Span<Money> shares = stackalloc Money[4];
        return _left.TryAllocate(4, shares);
    }
}
