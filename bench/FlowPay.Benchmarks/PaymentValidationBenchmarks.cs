using BenchmarkDotNet.Attributes;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Benchmarks;

/// <summary>
/// Request validation: the first thing every payment touches.
/// </summary>
/// <remarks>
/// Validation runs before any lock is taken or any row is read, so its cost is paid by
/// every request including the ones that are about to be rejected. The benchmarks cover
/// the valid case, the rejected case, and a request with several simultaneous problems —
/// the last one matters because the flags-based result reports every failure in one pass,
/// and that must not cost more than reporting the first.
/// </remarks>
[MemoryDiagnoser]
public class PaymentValidationBenchmarks
{
    private AccountId _source;
    private AccountId _destination;
    private Money _validAmount;
    private Money _excessiveAmount;
    private string _reference = string.Empty;
    private string _description = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _source = AccountId.New();
        _destination = AccountId.New();
        _validAmount = new Money(129_950L, Currency.Usd);
        _excessiveAmount = new Money(Money.MaxMinorUnits + 1L, Currency.Usd);
        _reference = "ORDER-2026-114238";
        _description = "Subscription renewal, annual plan";
    }

    [Benchmark(Baseline = true, Description = "Validate a well-formed request")]
    public PaymentValidationFailures ValidateValid()
    {
        PaymentValidationRequest request = new(
            _source, _destination, _validAmount, PaymentMethod.Card,
            _reference, _description, hasIdempotencyKey: true);

        return PaymentValidator.Validate(request).Failures;
    }

    [Benchmark(Description = "Validate a request over the amount limit")]
    public PaymentValidationFailures ValidateOverLimit()
    {
        PaymentValidationRequest request = new(
            _source, _destination, _excessiveAmount, PaymentMethod.Card,
            _reference, _description, hasIdempotencyKey: true);

        return PaymentValidator.Validate(request).Failures;
    }

    /// <summary>
    /// Four simultaneous problems: no accounts, no currency, a zero amount and no
    /// idempotency key. The flags result collects them all without allocating a list.
    /// </summary>
    [Benchmark(Description = "Validate a request with four simultaneous failures")]
    public PaymentValidationFailures ValidateMultipleFailures()
    {
        PaymentValidationRequest request = new(
            AccountId.Empty, AccountId.Empty, default, PaymentMethod.Unspecified,
            _reference, _description, hasIdempotencyKey: false);

        return PaymentValidator.Validate(request).Failures;
    }

    [Benchmark(Description = "Validate including the account currency check")]
    public PaymentValidationFailures ValidateAgainstAccounts()
    {
        PaymentValidationRequest request = new(
            _source, _destination, _validAmount, PaymentMethod.Card,
            _reference, _description, hasIdempotencyKey: true);

        return PaymentValidator
            .ValidateAgainstAccounts(request, Currency.Usd, Currency.Usd)
            .Failures;
    }

    /// <summary>
    /// Projecting the failure bitmask into the error codes an API response carries. The
    /// one validation step that is allowed to allocate, because it only runs on rejection.
    /// </summary>
    [Benchmark(Description = "Project failure codes for an API response")]
    public int ProjectErrorCodes()
    {
        PaymentValidationRequest request = new(
            AccountId.Empty, AccountId.Empty, default, PaymentMethod.Unspecified,
            _reference, _description, hasIdempotencyKey: false);

        PaymentValidationResult result = PaymentValidator.Validate(request);

        string[] codes = new string[result.FailureCount];
        return result.GetErrorCodes(codes);
    }
}
