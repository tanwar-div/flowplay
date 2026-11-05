using System.Text.Json;
using System.Text.Json.Serialization;
using FlowPay.Application.Outbox;

namespace FlowPay.Application.Payments;

/// <summary>A monetary amount as it appears on the wire.</summary>
/// <param name="MinorUnits">
/// The authoritative value. Clients that care about exactness use this field.
/// </param>
/// <param name="Currency">ISO-4217 alphabetic code.</param>
/// <param name="Display">
/// A formatted convenience rendering. Never parsed back; it exists so logs and consoles
/// are readable without the reader having to know the currency's exponent.
/// </param>
public sealed record MoneyDto(long MinorUnits, string Currency, string Display);

/// <summary>A payment intent as returned by the API.</summary>
public sealed record PaymentDto(
    Guid Id,
    string Status,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    MoneyDto Amount,
    MoneyDto Fee,
    MoneyDto Refunded,
    MoneyDto Refundable,
    string Method,
    string Reference,
    string Description,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? AuthorizedAt,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? AuthorizationExpiresAt,
    string[] AvailableTransitions);

/// <summary>A refund as returned by the API.</summary>
public sealed record RefundDto(
    Guid Id,
    Guid PaymentId,
    MoneyDto Amount,
    string Status,
    string Reason,
    string ResultingPaymentStatus,
    MoneyDto TotalRefunded,
    DateTimeOffset CreatedAt);

/// <summary>An account as returned by the API.</summary>
public sealed record AccountDto(
    Guid Id,
    string DisplayName,
    string Status,
    MoneyDto Balance,
    MoneyDto Reserved,
    MoneyDto Available,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One line of the double-entry ledger as returned by the API.</summary>
public sealed record LedgerEntryDto(
    Guid Id,
    Guid PaymentId,
    Guid? RefundId,
    Guid AccountId,
    string Direction,
    string EntryType,
    MoneyDto Amount,
    DateTimeOffset CreatedAt);

/// <summary>An RFC 9457 style error body.</summary>
public sealed record ProblemDto(
    string Type,
    string Title,
    int Status,
    string? Detail = null,
    string[]? Errors = null);

/// <summary>The report produced by the correctness checks.</summary>
public sealed record InvariantReportDto(
    bool Healthy,
    long LedgerNetMinorUnits,
    int UnbalancedPaymentCount,
    int OverRefundedPaymentCount,
    int BalanceDiscrepancyCount,
    string[] Details);

/// <summary>
/// Source-generated JSON contracts for every payload FlowPay serializes.
/// </summary>
/// <remarks>
/// Source generation rather than reflection-based serialization: the serializer is on the
/// request path for every response, and every idempotent write serializes its response a
/// second time to store it for replay. Generating the converters at build time removes
/// that reflection cost from the p95 and makes the API trim- and AOT-compatible.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PaymentDto))]
[JsonSerializable(typeof(RefundDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(LedgerEntryDto))]
[JsonSerializable(typeof(LedgerEntryDto[]))]
[JsonSerializable(typeof(ProblemDto))]
[JsonSerializable(typeof(MoneyDto))]
[JsonSerializable(typeof(InvariantReportDto))]
[JsonSerializable(typeof(PaymentAuthorizedEvent))]
[JsonSerializable(typeof(PaymentCapturedEvent))]
[JsonSerializable(typeof(PaymentFailedEvent))]
[JsonSerializable(typeof(PaymentVoidedEvent))]
[JsonSerializable(typeof(PaymentExpiredEvent))]
[JsonSerializable(typeof(PaymentRefundedEvent))]
public sealed partial class FlowPayJsonContext : JsonSerializerContext;

/// <summary>Shared serializer settings and typed serialization helpers.</summary>
/// <remarks>
/// Every method here goes through <see cref="FlowPayJsonContext"/>'s generated metadata
/// rather than the reflection-based overloads, so no call site can accidentally fall back
/// to runtime reflection and quietly lose the source-generation benefit.
/// </remarks>
public static class FlowPayJson
{
    /// <summary>Options backed by <see cref="FlowPayJsonContext"/>.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = FlowPayJsonContext.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PaymentDto payment)
        => JsonSerializer.Serialize(payment, FlowPayJsonContext.Default.PaymentDto);

    public static string Serialize(RefundDto refund)
        => JsonSerializer.Serialize(refund, FlowPayJsonContext.Default.RefundDto);

    public static string Serialize(AccountDto account)
        => JsonSerializer.Serialize(account, FlowPayJsonContext.Default.AccountDto);

    public static string Serialize(LedgerEntryDto[] entries)
        => JsonSerializer.Serialize(entries, FlowPayJsonContext.Default.LedgerEntryDtoArray);

    public static string Serialize(ProblemDto problem)
        => JsonSerializer.Serialize(problem, FlowPayJsonContext.Default.ProblemDto);

    public static string Serialize(InvariantReportDto report)
        => JsonSerializer.Serialize(report, FlowPayJsonContext.Default.InvariantReportDto);

    public static string Serialize(PaymentAuthorizedEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentAuthorizedEvent);

    public static string Serialize(PaymentCapturedEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentCapturedEvent);

    public static string Serialize(PaymentFailedEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentFailedEvent);

    public static string Serialize(PaymentVoidedEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentVoidedEvent);

    public static string Serialize(PaymentExpiredEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentExpiredEvent);

    public static string Serialize(PaymentRefundedEvent payload)
        => JsonSerializer.Serialize(payload, FlowPayJsonContext.Default.PaymentRefundedEvent);
}
