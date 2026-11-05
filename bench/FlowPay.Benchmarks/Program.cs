using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace FlowPay.Benchmarks;

/// <summary>
/// Entry point for the microbenchmark suite.
/// </summary>
/// <remarks>
/// Run everything with <c>dotnet run -c Release --project bench/FlowPay.Benchmarks</c>,
/// or one class with <c>--filter "*PaymentValidationBenchmarks*"</c>. Release is not
/// optional: a Debug build disables inlining and most of the JIT optimizations these
/// numbers depend on, and BenchmarkDotNet refuses to run against one.
/// </remarks>
public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));
}
