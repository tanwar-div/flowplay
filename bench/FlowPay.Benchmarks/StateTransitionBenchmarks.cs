using BenchmarkDotNet.Attributes;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Benchmarks;

/// <summary>
/// The payment lifecycle transitions, on the state machine and on the aggregate.
/// </summary>
/// <remarks>
/// <para>
/// A lifecycle transition is destructive: a captured payment cannot be captured again, so
/// a naive benchmark measures the first call and then measures the rejection path
/// forever. The usual fix — recreating the aggregate in an
/// <see cref="IterationSetupAttribute"/> — forces BenchmarkDotNet down to one invocation
/// per iteration, which for an operation measured in nanoseconds produces a standard
/// deviation larger than the mean. The numbers it yields are not worth reporting.
/// </para>
/// <para>
/// So the destructive transitions work through a pool: the setup builds
/// <see cref="PoolSize"/> fresh aggregates, the benchmark body transitions each one
/// exactly once, and <c>OperationsPerInvoke</c> divides the iteration's cost back down to
/// a per-transition figure. The setup is excluded from the measurement, and the body
/// allocates nothing, so the allocation column still reports the transition alone.
/// </para>
/// <para>
/// The rejection paths need none of this. A rejected transition leaves the aggregate
/// unchanged by design, so it can be invoked repeatedly against one instance and measured
/// at full precision — which is worth doing, because rejection has to be as cheap as
/// success. If it were not, a client hammering an endpoint with invalid requests would
/// cost more to turn away than to serve.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StateTransitionBenchmarks
{
    /// <summary>Lookups per invocation for the non-destructive table benchmarks.</summary>
    private const int LookupBatchSize = 1_000;

    /// <summary>Aggregates per iteration for the destructive transition benchmarks.</summary>
    private const int PoolSize = 4_096;

    private readonly PaymentIntent[] _pool = new PaymentIntent[PoolSize];

    private PaymentIntent _stableCaptured = null!;
    private PaymentIntent _stableRefunded = null!;
    private PaymentIntent _stablePending = null!;

    private Money _fee;
    private Money _partialRefund;
    private Money _excessiveRefund;
    private DateTimeOffset _now;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _now = DateTimeOffset.UtcNow;
        _fee = new Money(3_800L, Currency.Usd);
        _partialRefund = new Money(10_000L, Currency.Usd);
        _excessiveRefund = new Money(Money.MaxMinorUnits, Currency.Usd);

        _stablePending = NewPayment();

        _stableCaptured = NewPayment();
        _stableCaptured.TryAuthorize(_now).EnsureSuccess();
        _stableCaptured.TryCapture(_fee, _now).EnsureSuccess();

        _stableRefunded = NewPayment();
        _stableRefunded.TryAuthorize(_now).EnsureSuccess();
        _stableRefunded.TryCapture(_fee, _now).EnsureSuccess();
        _stableRefunded
            .TryRefund(new Money(129_950L, Currency.Usd), _now, out _)
            .EnsureSuccess();
    }

    // ---------------------------------------------------------------------------------
    // Transition table. Non-destructive, so measured at full precision in a tight batch.
    // ---------------------------------------------------------------------------------

    [Benchmark(
        Baseline = true,
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentStateMachine.CanTransition (bitmask lookup)")]
    public bool CanTransition()
    {
        bool result = false;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            result ^= PaymentStateMachine.CanTransition(
                PaymentStatus.Authorized, PaymentStatus.Captured);
        }

        return result;
    }

    [Benchmark(
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentStateMachine.TryTransition with error reporting")]
    public bool TryTransition()
    {
        bool result = false;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            result ^= PaymentStateMachine.TryTransition(
                PaymentStatus.Captured, PaymentStatus.Refunded, out _);
        }

        return result;
    }

    [Benchmark(
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentStateMachine.IsTerminal")]
    public bool IsTerminal()
    {
        bool result = false;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            result ^= PaymentStateMachine.IsTerminal(PaymentStatus.Refunded);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------
    // Successful transitions. Destructive, so each one runs against a pooled aggregate.
    // ---------------------------------------------------------------------------------

    [IterationSetup(Target = nameof(Authorize))]
    public void FillPendingPool() => FillPool(PoolState.Pending);

    [IterationSetup(Target = nameof(Capture))]
    public void FillAuthorizedPoolForCapture() => FillPool(PoolState.Authorized);

    [IterationSetup(Target = nameof(Void))]
    public void FillAuthorizedPoolForVoid() => FillPool(PoolState.Authorized);

    [IterationSetup(Target = nameof(RefundPartial))]
    public void FillCapturedPool() => FillPool(PoolState.Captured);

    [Benchmark(
        OperationsPerInvoke = PoolSize,
        Description = "PaymentIntent.TryAuthorize (pending -> authorized)")]
    public void Authorize()
    {
        PaymentIntent[] pool = _pool;

        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].TryAuthorize(_now);
        }
    }

    [Benchmark(
        OperationsPerInvoke = PoolSize,
        Description = "PaymentIntent.TryCapture (authorized -> captured)")]
    public void Capture()
    {
        PaymentIntent[] pool = _pool;

        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].TryCapture(_fee, _now);
        }
    }

    [Benchmark(
        OperationsPerInvoke = PoolSize,
        Description = "PaymentIntent.TryVoid (authorized -> voided)")]
    public void Void()
    {
        PaymentIntent[] pool = _pool;

        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].TryVoid(_now);
        }
    }

    [Benchmark(
        OperationsPerInvoke = PoolSize,
        Description = "PaymentIntent.TryRefund, partial (captured -> partially refunded)")]
    public void RefundPartial()
    {
        PaymentIntent[] pool = _pool;

        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].TryRefund(_partialRefund, _now, out _);
        }
    }

    // ---------------------------------------------------------------------------------
    // Rejections. Non-destructive by design, so measured at full precision.
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// The over-refund rejection: the branch the concurrent refund stress test drives
    /// hardest, and the one the invariant depends on.
    /// </summary>
    [Benchmark(
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentIntent.TryRefund, rejected as over-refund")]
    public DomainError RejectOverRefund()
    {
        DomainError error = DomainError.None;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            error = _stableCaptured.TryRefund(_excessiveRefund, _now, out _).Error;
        }

        return error;
    }

    [Benchmark(
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentIntent.TryCapture, rejected (not authorized)")]
    public DomainError RejectUnauthorizedCapture()
    {
        DomainError error = DomainError.None;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            error = _stablePending.TryCapture(_fee, _now).Error;
        }

        return error;
    }

    [Benchmark(
        OperationsPerInvoke = LookupBatchSize,
        Description = "PaymentIntent.TryVoid, rejected (terminal state)")]
    public DomainError RejectTerminalVoid()
    {
        DomainError error = DomainError.None;

        for (int i = 0; i < LookupBatchSize; i++)
        {
            error = _stableRefunded.TryVoid(_now).Error;
        }

        return error;
    }

    private enum PoolState
    {
        Pending,
        Authorized,
        Captured,
    }

    private void FillPool(PoolState state)
    {
        for (int i = 0; i < _pool.Length; i++)
        {
            PaymentIntent payment = NewPayment();

            if (state >= PoolState.Authorized)
            {
                payment.TryAuthorize(_now).EnsureSuccess();
            }

            if (state >= PoolState.Captured)
            {
                payment.TryCapture(_fee, _now).EnsureSuccess();
            }

            _pool[i] = payment;
        }
    }

    private PaymentIntent NewPayment() => PaymentIntent.Create(
        PaymentId.New(),
        AccountId.New(),
        AccountId.New(),
        new Money(129_950L, Currency.Usd),
        PaymentMethod.Card,
        "ORDER-2026-114238",
        "Subscription renewal, annual plan",
        _now).Value;
}
