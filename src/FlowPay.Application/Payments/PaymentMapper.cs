using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Application.Payments;

/// <summary>Projects domain objects onto the wire contracts.</summary>
public static class PaymentMapper
{
    public static MoneyDto ToDto(in Money money)
        => new(money.MinorUnits, money.Currency.ToString(), money.ToString());

    public static PaymentDto ToDto(PaymentIntent payment)
    {
        Span<PaymentStatus> transitions = stackalloc PaymentStatus[PaymentStateMachine.StatusCount];
        int count = PaymentStateMachine.GetAllowedTransitions(payment.Status, transitions);

        string[] available = count == 0 ? [] : new string[count];
        for (int i = 0; i < count; i++)
        {
            available[i] = transitions[i].ToString();
        }

        return new PaymentDto(
            payment.Id.Value,
            payment.Status.ToString(),
            payment.SourceAccountId.Value,
            payment.DestinationAccountId.Value,
            ToDto(payment.Amount),
            ToDto(payment.Fee),
            ToDto(payment.RefundedAmount),
            ToDto(payment.RefundableAmount),
            payment.Method.ToString(),
            payment.Reference,
            payment.Description,
            payment.FailureReason == PaymentFailureReason.None
                ? null
                : payment.FailureReason.ToString(),
            payment.CreatedAt,
            payment.UpdatedAt,
            payment.AuthorizedAt,
            payment.CapturedAt,
            payment.AuthorizationExpiresAt,
            available);
    }

    public static RefundDto ToDto(Refund refund, in Money totalRefunded)
        => new(
            refund.Id.Value,
            refund.PaymentId.Value,
            ToDto(refund.Amount),
            refund.Status.ToString(),
            refund.Reason,
            refund.ResultingPaymentStatus.ToString(),
            ToDto(totalRefunded),
            refund.CreatedAt);

    public static AccountDto ToDto(Account account)
        => new(
            account.Id.Value,
            account.DisplayName,
            account.Status.ToString(),
            ToDto(account.Balance),
            ToDto(account.Reserved),
            ToDto(account.Available),
            account.CreatedAt,
            account.UpdatedAt);

    public static LedgerEntryDto ToDto(LedgerEntry entry)
        => new(
            entry.Id.Value,
            entry.PaymentId.Value,
            entry.RefundId?.Value,
            entry.AccountId.Value,
            entry.Direction.ToString(),
            entry.EntryType.ToString(),
            ToDto(entry.Amount),
            entry.CreatedAt);
}
