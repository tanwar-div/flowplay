using System.Globalization;
using System.Text;
using FlowPay.Domain.Payments;

namespace FlowPay.Application.Payments;

/// <summary>Create and authorize a payment, optionally capturing it in the same call.</summary>
public sealed record AuthorizePaymentCommand(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    long AmountMinorUnits,
    string Currency,
    PaymentMethod Method,
    string Reference,
    string Description,
    bool CaptureImmediately);

/// <summary>Capture a previously authorized payment.</summary>
public sealed record CapturePaymentCommand(Guid PaymentId);

/// <summary>Refund some or all of a captured payment.</summary>
public sealed record RefundPaymentCommand(
    Guid PaymentId,
    long AmountMinorUnits,
    string Currency,
    string Reason);

/// <summary>Release an authorization before capture.</summary>
public sealed record VoidPaymentCommand(Guid PaymentId);

/// <summary>
/// Builds the canonical strings that idempotency fingerprints are computed from.
/// </summary>
/// <remarks>
/// <para>
/// The canonical form includes every field that affects what the request does, and
/// nothing else. If a field is left out, a client could reuse one key to move a different
/// amount and be handed the first request's response — believing it had transferred money
/// it had not. If an irrelevant field is included, a client whose HTTP stack reorders JSON
/// keys or reformats a timestamp gets a spurious "key reused" rejection on a legitimate
/// retry.
/// </para>
/// <para>
/// The format is fixed-order and separator-delimited rather than derived from the request
/// bytes, so it is stable across client libraries and across FlowPay versions.
/// </para>
/// </remarks>
public static class CanonicalRequest
{
    private const char Separator = '|';

    public static string ForAuthorize(AuthorizePaymentCommand command)
    {
        StringBuilder builder = new(192);

        builder.Append("authorize").Append(Separator)
            .Append(command.SourceAccountId.ToString("n")).Append(Separator)
            .Append(command.DestinationAccountId.ToString("n")).Append(Separator)
            .Append(command.AmountMinorUnits.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append(command.Currency.ToUpperInvariant()).Append(Separator)
            .Append((int)command.Method).Append(Separator)
            .Append(command.Reference).Append(Separator)
            .Append(command.Description).Append(Separator)
            .Append(command.CaptureImmediately ? '1' : '0');

        return builder.ToString();
    }

    public static string ForCapture(CapturePaymentCommand command)
        => string.Concat("capture", "|", command.PaymentId.ToString("n"));

    public static string ForRefund(RefundPaymentCommand command)
    {
        StringBuilder builder = new(128);

        builder.Append("refund").Append(Separator)
            .Append(command.PaymentId.ToString("n")).Append(Separator)
            .Append(command.AmountMinorUnits.ToString(CultureInfo.InvariantCulture)).Append(Separator)
            .Append(command.Currency.ToUpperInvariant()).Append(Separator)
            .Append(command.Reason);

        return builder.ToString();
    }

    public static string ForVoid(VoidPaymentCommand command)
        => string.Concat("void", "|", command.PaymentId.ToString("n"));
}

/// <summary>The endpoint names idempotency keys are scoped to.</summary>
public static class IdempotencyScopes
{
    public const string AuthorizePayment = "POST /v1/payments";
    public const string CapturePayment = "POST /v1/payments/{id}/capture";
    public const string RefundPayment = "POST /v1/payments/{id}/refunds";
    public const string VoidPayment = "POST /v1/payments/{id}/void";
}
