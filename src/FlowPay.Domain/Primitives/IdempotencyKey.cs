using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;

namespace FlowPay.Domain.Primitives;

/// <summary>Why an idempotency key was rejected.</summary>
public enum IdempotencyKeyError : byte
{
    None = 0,
    Empty = 1,
    TooLong = 2,
    InvalidCharacter = 3,
}

/// <summary>
/// A validated client-supplied idempotency key, reduced to a 128-bit fingerprint.
/// </summary>
/// <remarks>
/// <para>
/// The raw key is still what the database uniquely indexes — that is the authority for
/// deduplication and keeps the audit trail readable. The fingerprint exists so the
/// in-process layers above it (the request coalescer, per-key striped locks, and metrics
/// cardinality buckets) can key off a fixed-size blittable value instead of hashing a
/// string on every request.
/// </para>
/// <para>
/// Parsing and fingerprinting run entirely on the stack: the UTF-8 transcode targets a
/// stack buffer sized from <see cref="MaxLength"/>, so a key never reaches the heap on
/// the authorization path.
/// </para>
/// </remarks>
public readonly struct IdempotencyKey : IEquatable<IdempotencyKey>
{
    /// <summary>Maximum accepted key length, matching the database column width.</summary>
    public const int MaxLength = 128;

    // MaxLength ASCII characters can transcode to at most 4 bytes each; the accepted
    // character set is ASCII-only, but sizing for the worst case keeps the stackalloc
    // correct if the character policy is ever widened.
    private const int MaxUtf8Bytes = MaxLength * 4;

    private readonly UInt128 _fingerprint;

    private IdempotencyKey(UInt128 fingerprint) => _fingerprint = fingerprint;

    /// <summary>The 128-bit fingerprint of the key.</summary>
    public UInt128 Fingerprint
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _fingerprint;
    }

    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _fingerprint == UInt128.Zero;
    }

    /// <summary>
    /// A stable bucket index for striped locking, derived from the high bits of the
    /// fingerprint. <paramref name="stripeCount"/> must be a power of two.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetStripe(int stripeCount) => (int)((ulong)_fingerprint & (ulong)(stripeCount - 1));

    /// <summary>
    /// Validates and fingerprints a client-supplied key without allocating.
    /// </summary>
    /// <remarks>
    /// Accepted characters are ASCII alphanumerics plus <c>-</c>, <c>_</c>, <c>.</c> and
    /// <c>:</c>. Restricting the set up front means the key can be echoed into logs and
    /// error payloads without escaping, and it rules out control characters and
    /// whitespace that would otherwise produce two "different" keys for the same intent.
    /// </remarks>
    public static bool TryParse(
        ReadOnlySpan<char> raw,
        out IdempotencyKey key,
        out IdempotencyKeyError error)
    {
        key = default;

        if (raw.IsEmpty)
        {
            error = IdempotencyKeyError.Empty;
            return false;
        }

        if (raw.Length > MaxLength)
        {
            error = IdempotencyKeyError.TooLong;
            return false;
        }

        for (int i = 0; i < raw.Length; i++)
        {
            if (!IsAllowed(raw[i]))
            {
                error = IdempotencyKeyError.InvalidCharacter;
                return false;
            }
        }

        Span<byte> utf8 = stackalloc byte[MaxUtf8Bytes];
        int written = Encoding.UTF8.GetBytes(raw, utf8);

        key = new IdempotencyKey(XxHash128.HashToUInt128(utf8[..written]));
        error = IdempotencyKeyError.None;
        return true;
    }

    /// <summary>
    /// Fingerprints a key that has already been validated, skipping the character scan.
    /// Used when rehydrating keys that the database accepted previously.
    /// </summary>
    public static IdempotencyKey FromTrusted(ReadOnlySpan<char> raw)
    {
        Span<byte> utf8 = stackalloc byte[MaxUtf8Bytes];
        int written = Encoding.UTF8.GetBytes(raw, utf8);
        return new IdempotencyKey(XxHash128.HashToUInt128(utf8[..written]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllowed(char c)
        => (uint)(c - '0') <= 9
            || (uint)((c | 0x20) - 'a') <= ('z' - 'a')
            || c is '-' or '_' or '.' or ':';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(IdempotencyKey other) => _fingerprint == other._fingerprint;

    public override bool Equals(object? obj) => obj is IdempotencyKey other && Equals(other);

    public override int GetHashCode() => _fingerprint.GetHashCode();

    public static bool operator ==(IdempotencyKey left, IdempotencyKey right) => left.Equals(right);

    public static bool operator !=(IdempotencyKey left, IdempotencyKey right) => !left.Equals(right);

    public override string ToString() => _fingerprint.ToString("x32", null);
}
