using FlowPay.Domain.Primitives;
using Microsoft.AspNetCore.Http;

namespace FlowPay.Api.Infrastructure;

/// <summary>The outcome of reading the idempotency key from a request.</summary>
public readonly record struct IdempotencyHeaderResult(
    bool IsValid,
    string Key,
    IdempotencyKeyError Error)
{
    public static IdempotencyHeaderResult Valid(string key)
        => new(true, key, IdempotencyKeyError.None);

    public static IdempotencyHeaderResult Invalid(IdempotencyKeyError error)
        => new(false, string.Empty, error);
}

/// <summary>
/// Reads and validates the <c>Idempotency-Key</c> request header.
/// </summary>
/// <remarks>
/// <para>
/// The header is required on every mutating endpoint, not optional. Making it optional
/// would mean the safety property only held for clients that remembered to opt in — and
/// the clients most likely to forget are exactly the ones whose retries cause duplicate
/// charges.
/// </para>
/// <para>
/// Validation runs against a span over the header value, so a well-formed key costs no
/// allocation beyond the string ASP.NET Core has already materialized.
/// </para>
/// </remarks>
public static class IdempotencyHeader
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Set on a response served from a stored result rather than fresh work.</summary>
    public const string ReplayHeaderName = "Idempotent-Replay";

    public static IdempotencyHeaderResult Read(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues values))
        {
            return IdempotencyHeaderResult.Invalid(IdempotencyKeyError.Empty);
        }

        string? raw = values.Count > 0 ? values[0] : null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return IdempotencyHeaderResult.Invalid(IdempotencyKeyError.Empty);
        }

        // Parsed for validation only. The raw string, not the fingerprint, is what the
        // database uniquely indexes: the fingerprint is for in-process bucketing, and
        // keeping the original key makes the stored record readable during an incident.
        if (!IdempotencyKey.TryParse(raw, out _, out IdempotencyKeyError error))
        {
            return IdempotencyHeaderResult.Invalid(error);
        }

        return IdempotencyHeaderResult.Valid(raw);
    }

    /// <summary>A client-facing explanation of why a key was rejected.</summary>
    public static string Describe(IdempotencyKeyError error) => error switch
    {
        IdempotencyKeyError.Empty =>
            $"The {HeaderName} header is required on this endpoint.",
        IdempotencyKeyError.TooLong =>
            $"The {HeaderName} header must be at most {IdempotencyKey.MaxLength} characters.",
        IdempotencyKeyError.InvalidCharacter =>
            $"The {HeaderName} header may contain only letters, digits, and the characters - _ . :",
        _ => $"The {HeaderName} header is not valid.",
    };
}
