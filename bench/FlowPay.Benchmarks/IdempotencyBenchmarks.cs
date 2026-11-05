using BenchmarkDotNet.Attributes;
using FlowPay.Application.Idempotency;
using FlowPay.Application.Payments;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Benchmarks;

/// <summary>
/// Idempotency key parsing and request fingerprinting.
/// </summary>
/// <remarks>
/// Both run once per mutating request, before the transaction opens. Key parsing is
/// allocation-free: the UTF-8 transcode targets a stack buffer, so a well-formed key costs
/// nothing beyond the hash. Fingerprinting does allocate, and the benchmarks show how
/// much — the canonical string and the 16-byte digest are a deliberate cost, paid so that
/// reusing a key with different parameters is detected rather than silently answered with
/// the first request's response.
/// </remarks>
[MemoryDiagnoser]
public class IdempotencyBenchmarks
{
    private string _shortKey = string.Empty;
    private string _longKey = string.Empty;
    private string _invalidKey = string.Empty;
    private AuthorizePaymentCommand _command = null!;
    private string _canonicalForm = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _shortKey = "idem-7f3a91c4";
        _longKey = new string('a', IdempotencyKey.MaxLength);
        _invalidKey = "idem key with spaces";

        _command = new AuthorizePaymentCommand(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            129_950L,
            "USD",
            PaymentMethod.Card,
            "ORDER-2026-114238",
            "Subscription renewal, annual plan",
            CaptureImmediately: true);

        _canonicalForm = CanonicalRequest.ForAuthorize(_command);
    }

    [Benchmark(Baseline = true, Description = "IdempotencyKey.TryParse, typical key")]
    public bool ParseShortKey()
        => IdempotencyKey.TryParse(_shortKey, out _, out _);

    [Benchmark(Description = "IdempotencyKey.TryParse, maximum-length key")]
    public bool ParseLongKey()
        => IdempotencyKey.TryParse(_longKey, out _, out _);

    [Benchmark(Description = "IdempotencyKey.TryParse, rejected key")]
    public bool ParseInvalidKey()
        => IdempotencyKey.TryParse(_invalidKey, out _, out _);

    [Benchmark(Description = "IdempotencyKey.GetStripe (lock bucket selection)")]
    public int GetStripe()
    {
        IdempotencyKey.TryParse(_shortKey, out IdempotencyKey key, out _);
        return key.GetStripe(64);
    }

    [Benchmark(Description = "CanonicalRequest.ForAuthorize (canonical string)")]
    public string BuildCanonicalForm() => CanonicalRequest.ForAuthorize(_command);

    [Benchmark(Description = "RequestFingerprint.Compute (128-bit digest)")]
    public byte[] ComputeFingerprint() => RequestFingerprint.Compute(_canonicalForm);

    /// <summary>
    /// The whole pre-transaction sequence an endpoint performs: parse the header, build
    /// the canonical form, hash it.
    /// </summary>
    [Benchmark(Description = "Full pre-transaction idempotency preparation")]
    public byte[] PrepareRequest()
    {
        IdempotencyKey.TryParse(_shortKey, out _, out _);
        return RequestFingerprint.Compute(CanonicalRequest.ForAuthorize(_command));
    }
}
