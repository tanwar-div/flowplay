using FlowPay.Domain.Payments;
using Xunit;

namespace FlowPay.UnitTests.Domain;

public class PaymentStateMachineTests
{
    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Expired)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.PartiallyRefunded, PaymentStatus.PartiallyRefunded)]
    [InlineData(PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded)]
    public void Legal_transitions_are_allowed(PaymentStatus from, PaymentStatus to)
        => Assert.True(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Pending, PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Authorized, PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Captured, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Refunded, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Failed, PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Voided, PaymentStatus.Captured)]
    [InlineData(PaymentStatus.Expired, PaymentStatus.Authorized)]
    public void Illegal_transitions_are_rejected(PaymentStatus from, PaymentStatus to)
        => Assert.False(PaymentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(PaymentStatus.Refunded)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Voided)]
    [InlineData(PaymentStatus.Expired)]
    public void Terminal_states_have_no_outgoing_transitions(PaymentStatus status)
    {
        Assert.True(PaymentStateMachine.IsTerminal(status));

        for (int target = 0; target < PaymentStateMachine.StatusCount; target++)
        {
            Assert.False(PaymentStateMachine.CanTransition(status, (PaymentStatus)target));
        }
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.Authorized)]
    [InlineData(PaymentStatus.Captured)]
    [InlineData(PaymentStatus.PartiallyRefunded)]
    public void Non_terminal_states_have_at_least_one_transition(PaymentStatus status)
    {
        Assert.False(PaymentStateMachine.IsTerminal(status));

        Span<PaymentStatus> targets = stackalloc PaymentStatus[PaymentStateMachine.StatusCount];
        Assert.True(PaymentStateMachine.GetAllowedTransitions(status, targets) > 0);
    }

    /// <summary>
    /// Corrupt or future data must fail closed. An undefined status reports terminal, so
    /// a row FlowPay cannot interpret becomes immutable rather than freely mutable.
    /// </summary>
    [Fact]
    public void An_undefined_status_is_treated_as_terminal()
    {
        PaymentStatus undefined = (PaymentStatus)99;

        Assert.False(PaymentStateMachine.IsDefined(undefined));
        Assert.True(PaymentStateMachine.IsTerminal(undefined));
        Assert.False(PaymentStateMachine.CanTransition(undefined, PaymentStatus.Captured));
        Assert.False(PaymentStateMachine.CanTransition(PaymentStatus.Pending, undefined));
    }

    [Fact]
    public void TryTransition_distinguishes_a_finished_payment_from_an_impossible_move()
    {
        Assert.False(PaymentStateMachine.TryTransition(
            PaymentStatus.Refunded, PaymentStatus.Captured, out TransitionError terminal));
        Assert.Equal(TransitionError.TerminalState, terminal);

        Assert.False(PaymentStateMachine.TryTransition(
            PaymentStatus.Pending, PaymentStatus.Captured, out TransitionError notPermitted));
        Assert.Equal(TransitionError.NotPermitted, notPermitted);

        Assert.False(PaymentStateMachine.TryTransition(
            (PaymentStatus)99, PaymentStatus.Captured, out TransitionError unknown));
        Assert.Equal(TransitionError.UnknownStatus, unknown);
    }

    [Theory]
    [InlineData(PaymentStatus.Captured, true)]
    [InlineData(PaymentStatus.PartiallyRefunded, true)]
    [InlineData(PaymentStatus.Refunded, true)]
    [InlineData(PaymentStatus.Pending, false)]
    [InlineData(PaymentStatus.Authorized, false)]
    [InlineData(PaymentStatus.Failed, false)]
    [InlineData(PaymentStatus.Voided, false)]
    [InlineData(PaymentStatus.Expired, false)]
    public void IsSettled_identifies_the_states_that_have_ledger_entries(
        PaymentStatus status,
        bool expected)
        => Assert.Equal(expected, PaymentStateMachine.IsSettled(status));

    [Fact]
    public void GetAllowedTransitions_lists_exactly_the_legal_targets()
    {
        Span<PaymentStatus> targets = stackalloc PaymentStatus[PaymentStateMachine.StatusCount];
        int count = PaymentStateMachine.GetAllowedTransitions(PaymentStatus.Captured, targets);

        Assert.Equal(2, count);
        Assert.Equal(PaymentStatus.PartiallyRefunded, targets[0]);
        Assert.Equal(PaymentStatus.Refunded, targets[1]);
    }

    /// <summary>
    /// The listing must not write past the caller's buffer, since callers size it from
    /// the stack.
    /// </summary>
    [Fact]
    public void GetAllowedTransitions_respects_a_buffer_smaller_than_the_result()
    {
        Span<PaymentStatus> tiny = stackalloc PaymentStatus[1];
        int count = PaymentStateMachine.GetAllowedTransitions(PaymentStatus.Pending, tiny);

        Assert.Equal(1, count);
    }
}
