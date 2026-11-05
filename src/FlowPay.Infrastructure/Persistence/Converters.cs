using FlowPay.Domain.Primitives;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FlowPay.Infrastructure.Persistence;

/// <summary>
/// Converters between the domain's strongly typed primitives and their column types.
/// </summary>
internal static class FlowPayConverters
{
    public static readonly ValueConverter<AccountId, Guid> AccountId =
        new(id => id.Value, value => new AccountId(value));

    public static readonly ValueConverter<PaymentId, Guid> PaymentId =
        new(id => id.Value, value => new PaymentId(value));

    public static readonly ValueConverter<RefundId, Guid> RefundId =
        new(id => id.Value, value => new RefundId(value));

    public static readonly ValueConverter<RefundId?, Guid?> NullableRefundId =
        new(
            id => id.HasValue ? id.Value.Value : null,
            value => value.HasValue ? new RefundId(value.Value) : null);

    public static readonly ValueConverter<LedgerEntryId, Guid> LedgerEntryId =
        new(id => id.Value, value => new LedgerEntryId(value));

    /// <summary>
    /// Stores a currency as its three-letter code rather than its packed integer.
    /// </summary>
    /// <remarks>
    /// In memory a <see cref="Currency"/> is a packed <see cref="uint"/> so that equality
    /// is one integer compare on the hot path. In the database it is <c>char(3)</c>,
    /// because the rows are read by people — during an incident, a reconciliation query,
    /// or a support request — and <c>'USD'</c> is worth more there than <c>4478293</c>.
    /// The conversion happens once per row on materialization, well off the hot path.
    /// </remarks>
    public static readonly ValueConverter<Currency, string> Currency =
        new(
            currency => currency.ToString(),
            code => ParseCurrency(code));

    private static Currency ParseCurrency(string code)
        => Domain.Primitives.Currency.TryParse(code, out Currency currency)
            ? currency
            : throw new InvalidOperationException(
                $"The stored currency code '{code}' is not a valid ISO-4217 code.");
}
