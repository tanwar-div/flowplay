using System.IO.Hashing;
using System.Text;

namespace FlowPay.Application.Idempotency;

/// <summary>State of an idempotency claim.</summary>
public enum IdempotencyStatus : short
{
    /// <summary>
    /// Claimed but not finished. Only observable if a previous attempt committed the
    /// claim separately from its work, which the single-transaction protocol avoids.
    /// </summary>
    InProgress = 0,

    /// <summary>Finished, with the response stored for replay.</summary>
    Completed = 1,
}

/// <summary>
/// A stored idempotency claim and the response it produced.
/// </summary>
/// <remarks>
/// The response is persisted verbatim rather than regenerated on replay. Regenerating it
/// would mean a replay could return a different body than the original call — a later
/// refund changing the payment's status, for instance — which defeats the point: a client
/// retrying after a timeout needs to learn what its original request did, not what the
/// resource looks like now.
/// </remarks>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        // Materialization constructor for the persistence layer.
    }

    /// <summary>
    /// The operation the key was used against, part of the primary key.
    /// </summary>
    /// <remarks>
    /// Scoping by endpoint means a client that reuses one key per business transaction
    /// across authorize and capture is not told its capture is a duplicate authorization.
    /// </remarks>
    public string Endpoint { get; private set; } = string.Empty;

    /// <summary>The raw client-supplied key.</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>
    /// A 128-bit hash of the canonical request parameters.
    /// </summary>
    /// <remarks>
    /// Stored so that reusing one key for a genuinely different request is detected and
    /// rejected, rather than silently answered with the first request's response. That
    /// failure mode is worse than a duplicate: the client believes it created something
    /// it did not.
    /// </remarks>
    public byte[] RequestFingerprint { get; private set; } = [];

    public IdempotencyStatus Status { get; private set; }

    public int? ResponseStatusCode { get; private set; }

    /// <summary>The response body, stored exactly as it was first returned.</summary>
    public string? ResponseBody { get; private set; }

    /// <summary>The id of the resource the request created, for logging and lookups.</summary>
    public Guid? ResourceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When this record may be pruned.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyRecord Claim(
        string endpoint,
        string key,
        byte[] requestFingerprint,
        DateTimeOffset now,
        TimeSpan retention) => new()
        {
            Endpoint = endpoint,
            Key = key,
            RequestFingerprint = requestFingerprint,
            Status = IdempotencyStatus.InProgress,
            CreatedAt = now,
            ExpiresAt = now + retention,
        };

    /// <summary>Records the response that this key's request produced.</summary>
    public void Complete(int statusCode, string responseBody, Guid? resourceId, DateTimeOffset now)
    {
        Status = IdempotencyStatus.Completed;
        ResponseStatusCode = statusCode;
        ResponseBody = responseBody;
        ResourceId = resourceId;
        CompletedAt = now;
    }

    /// <summary>
    /// Constant-time comparison of the stored fingerprint against a candidate.
    /// </summary>
    public bool MatchesFingerprint(ReadOnlySpan<byte> candidate)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            RequestFingerprint, candidate);
}

/// <summary>
/// Canonicalizes and hashes request parameters into a fingerprint.
/// </summary>
/// <remarks>
/// The fingerprint is computed from the parsed, normalized fields rather than the raw
/// request bytes. Hashing the bytes would make two semantically identical requests look
/// different because of key ordering or whitespace, and a client retrying through a
/// library that reserializes its payload would get a spurious "key reused" rejection.
/// </remarks>
public static class RequestFingerprint
{
    /// <summary>Size of a fingerprint in bytes.</summary>
    public const int SizeInBytes = 16;

    /// <summary>Hashes a canonical string form of the request's significant fields.</summary>
    public static byte[] Compute(ReadOnlySpan<char> canonicalForm)
    {
        const int StackBudget = 512;

        int maxBytes = Encoding.UTF8.GetMaxByteCount(canonicalForm.Length);

        // Canonical forms are short by construction, so the stack path is the normal
        // one; the heap fallback only exists so an unusually long reference or
        // description cannot overflow the stack.
        Span<byte> buffer = maxBytes <= StackBudget
            ? stackalloc byte[StackBudget]
            : new byte[maxBytes];

        int written = Encoding.UTF8.GetBytes(canonicalForm, buffer);

        byte[] fingerprint = new byte[SizeInBytes];
        XxHash128.Hash(buffer[..written], fingerprint);
        return fingerprint;
    }
}
