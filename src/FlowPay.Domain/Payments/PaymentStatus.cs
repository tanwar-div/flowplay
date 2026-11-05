namespace FlowPay.Domain.Payments;

/// <summary>
/// The lifecycle state of a payment intent.
/// </summary>
/// <remarks>
/// The numeric values are part of the persisted contract and the transition table's
/// layout: each status is a bit index in <see cref="PaymentStateMachine"/>'s bitmask
/// table, so values must stay dense, start at zero, and never be renumbered.
/// </remarks>
public enum PaymentStatus : byte
{
    /// <summary>Created and validated, no funds reserved yet.</summary>
    Pending = 0,

    /// <summary>Funds reserved on the source account, not yet moved.</summary>
    Authorized = 1,

    /// <summary>Funds moved; the double-entry ledger lines exist.</summary>
    Captured = 2,

    /// <summary>Captured, with some but not all of the amount refunded.</summary>
    PartiallyRefunded = 3,

    /// <summary>Captured and refunded in full.</summary>
    Refunded = 4,

    /// <summary>Rejected by validation, risk, or the funding source.</summary>
    Failed = 5,

    /// <summary>An authorization released before capture.</summary>
    Voided = 6,

    /// <summary>An authorization that aged out before capture.</summary>
    Expired = 7,
}

/// <summary>Why a payment failed. Persisted alongside <see cref="PaymentStatus.Failed"/>.</summary>
public enum PaymentFailureReason : byte
{
    None = 0,
    InsufficientFunds = 1,
    AccountClosed = 2,
    AccountFrozen = 3,
    CurrencyMismatch = 4,
    RiskRejected = 5,
    ValidationFailed = 6,
    DownstreamUnavailable = 7,
    AuthorizationExpired = 8,
}

/// <summary>How the payer is funding the payment.</summary>
public enum PaymentMethod : byte
{
    Unspecified = 0,
    Card = 1,
    BankTransfer = 2,
    Wallet = 3,
    DirectDebit = 4,
}
