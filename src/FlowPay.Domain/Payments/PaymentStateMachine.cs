using System.Runtime.CompilerServices;

namespace FlowPay.Domain.Payments;

/// <summary>Why a requested state transition was rejected.</summary>
public enum TransitionError : byte
{
    None = 0,

    /// <summary>The source or target status was outside the defined range.</summary>
    UnknownStatus = 1,

    /// <summary>The payment is already in a terminal state.</summary>
    TerminalState = 2,

    /// <summary>The transition is not part of the payment lifecycle.</summary>
    NotPermitted = 3,
}

/// <summary>
/// The payment lifecycle, encoded as a bitmask lookup table.
/// </summary>
/// <remarks>
/// <para>
/// There are eight statuses, so the set of legal targets for any status fits in a single
/// byte: bit <c>n</c> of <c>Transitions[from]</c> is set when <c>from -> (PaymentStatus)n</c>
/// is legal. A transition check is therefore one table load, one shift and one mask —
/// no branch chain over an <c>if</c>/<c>switch</c> ladder whose cost depends on which
/// state the payment happens to be in, and no dictionary lookup.
/// </para>
/// <para>
/// Declaring the table as a <see cref="ReadOnlySpan{T}"/> of byte constants is what makes
/// it free: the C# compiler emits the bytes into the assembly's read-only data section
/// and the span points straight at them, so there is no static array to allocate, no
/// type initializer to run, and nothing for the GC to track.
/// </para>
/// </remarks>
public static class PaymentStateMachine
{
    /// <summary>Number of defined <see cref="PaymentStatus"/> values.</summary>
    public const int StatusCount = 8;

    private const byte PendingTargets =
        (1 << (int)PaymentStatus.Authorized) |
        (1 << (int)PaymentStatus.Failed) |
        (1 << (int)PaymentStatus.Voided) |
        (1 << (int)PaymentStatus.Expired);

    private const byte AuthorizedTargets =
        (1 << (int)PaymentStatus.Captured) |
        (1 << (int)PaymentStatus.Failed) |
        (1 << (int)PaymentStatus.Voided) |
        (1 << (int)PaymentStatus.Expired);

    private const byte CapturedTargets =
        (1 << (int)PaymentStatus.PartiallyRefunded) |
        (1 << (int)PaymentStatus.Refunded);

    // A partially refunded payment can absorb further partial refunds, so it is both a
    // source and a target of itself.
    private const byte PartiallyRefundedTargets =
        (1 << (int)PaymentStatus.PartiallyRefunded) |
        (1 << (int)PaymentStatus.Refunded);

    private const byte Terminal = 0;

    /// <summary>
    /// One mask per status, indexed by the status's numeric value.
    /// </summary>
    private static ReadOnlySpan<byte> Transitions =>
    [
        PendingTargets,             // Pending
        AuthorizedTargets,          // Authorized
        CapturedTargets,            // Captured
        PartiallyRefundedTargets,   // PartiallyRefunded
        Terminal,                   // Refunded
        Terminal,                   // Failed
        Terminal,                   // Voided
        Terminal,                   // Expired
    ];

    /// <summary>True when <paramref name="status"/> is a defined value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsDefined(PaymentStatus status) => (uint)status < StatusCount;

    /// <summary>
    /// True when no transition out of <paramref name="status"/> exists. Undefined values
    /// report terminal so that corrupt data fails closed rather than becoming mutable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsTerminal(PaymentStatus status)
        => (uint)status >= StatusCount || Transitions[(int)status] == Terminal;

    /// <summary>
    /// True when the payment has successfully moved funds and so has ledger entries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSettled(PaymentStatus status)
        => status is PaymentStatus.Captured
            or PaymentStatus.PartiallyRefunded
            or PaymentStatus.Refunded;

    /// <summary>
    /// Whether <paramref name="from"/> may move to <paramref name="to"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanTransition(PaymentStatus from, PaymentStatus to)
    {
        if ((uint)from >= StatusCount || (uint)to >= StatusCount)
        {
            return false;
        }

        return (Transitions[(int)from] & (1 << (int)to)) != 0;
    }

    /// <summary>
    /// Validates a transition and reports a specific reason when it is rejected, so the
    /// caller can distinguish "already finished" from "never legal".
    /// </summary>
    public static bool TryTransition(PaymentStatus from, PaymentStatus to, out TransitionError error)
    {
        if ((uint)from >= StatusCount || (uint)to >= StatusCount)
        {
            error = TransitionError.UnknownStatus;
            return false;
        }

        byte mask = Transitions[(int)from];

        if (mask == Terminal)
        {
            error = TransitionError.TerminalState;
            return false;
        }

        if ((mask & (1 << (int)to)) == 0)
        {
            error = TransitionError.NotPermitted;
            return false;
        }

        error = TransitionError.None;
        return true;
    }

    /// <summary>
    /// Writes the legal targets for <paramref name="from"/> into
    /// <paramref name="destination"/>. Used by the API to advertise the operations
    /// currently available on a payment.
    /// </summary>
    public static int GetAllowedTransitions(PaymentStatus from, Span<PaymentStatus> destination)
    {
        if ((uint)from >= StatusCount)
        {
            return 0;
        }

        byte mask = Transitions[(int)from];
        int count = 0;

        for (int target = 0; target < StatusCount && count < destination.Length; target++)
        {
            if ((mask & (1 << target)) != 0)
            {
                destination[count++] = (PaymentStatus)target;
            }
        }

        return count;
    }
}
