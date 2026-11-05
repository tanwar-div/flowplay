using System.Text.Json.Serialization;

namespace FlowPay.Api.Contracts;

/// <summary>Request body for <c>POST /v1/payments</c>.</summary>
/// <param name="SourceAccountId">The payer.</param>
/// <param name="DestinationAccountId">The payee.</param>
/// <param name="AmountMinorUnits">
/// The amount in minor units. Minor units rather than a decimal: a JSON number parsed as
/// a double cannot represent every decimal amount exactly, and the one place that must
/// never happen is the field that says how much money to move.
/// </param>
/// <param name="Currency">ISO-4217 alphabetic code.</param>
/// <param name="Method">One of Card, BankTransfer, Wallet, DirectDebit.</param>
/// <param name="Reference">Merchant reference, up to 64 characters.</param>
/// <param name="Description">Free text, up to 512 characters.</param>
/// <param name="Capture">
/// When true, the payment is authorized and captured in the same transaction.
/// </param>
public sealed record CreatePaymentRequest(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    long AmountMinorUnits,
    string Currency,
    string Method,
    string? Reference,
    string? Description,
    bool Capture);

/// <summary>Request body for <c>POST /v1/payments/{id}/refunds</c>.</summary>
/// <param name="AmountMinorUnits">
/// The amount to refund. Omit nothing: a partial refund is the normal case, and defaulting
/// to the full amount would make a missing field silently refund everything.
/// </param>
public sealed record CreateRefundRequest(
    long AmountMinorUnits,
    string Currency,
    string? Reason);

/// <summary>Request body for <c>POST /v1/accounts</c>.</summary>
public sealed record CreateAccountRequest(
    Guid? Id,
    string Currency,
    long OpeningBalanceMinorUnits,
    string? DisplayName);

/// <summary>
/// Source-generated deserialization for the request bodies.
/// </summary>
/// <remarks>
/// Paired with the response-side context in the application layer so that neither
/// direction of the JSON path falls back to runtime reflection.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CreatePaymentRequest))]
[JsonSerializable(typeof(CreateRefundRequest))]
[JsonSerializable(typeof(CreateAccountRequest))]
public sealed partial class FlowPayRequestContext : JsonSerializerContext;
