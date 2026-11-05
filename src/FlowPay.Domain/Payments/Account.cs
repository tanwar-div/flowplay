using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Payments;

/// <summary>Whether an account may participate in new payments.</summary>
public enum AccountStatus : byte
{
    Active = 0,

    /// <summary>Can receive funds but not send them (compliance hold).</summary>
    Frozen = 1,

    /// <summary>Cannot participate in any new payment.</summary>
    Closed = 2,
}

/// <summary>
/// A balance-holding party, with funds split between a settled balance and the portion
/// reserved by in-flight authorizations.
/// </summary>
/// <remarks>
/// <para>
/// Tracking <see cref="ReservedMinorUnits"/> separately from
/// <see cref="BalanceMinorUnits"/> is what makes authorize-then-capture safe. An
/// authorization moves funds into the reserved bucket, so two concurrent authorizations
/// cannot both see the same money as spendable, and a capture draws from the reservation
/// it already owns rather than re-checking a balance that may have moved underneath it.
/// </para>
/// <para>
/// Every mutation is a <c>Try*</c> method that validates the invariant before touching
/// state, so a rejected operation leaves the aggregate untouched and allocates nothing.
/// These checks are the first of three layers: the aggregate here, row-level locking in
/// the repository, and <c>CHECK</c> constraints in PostgreSQL that make a negative
/// balance unrepresentable even if a future code path forgets to ask.
/// </para>
/// </remarks>
public sealed class Account
{
    private Account()
    {
        // Materialization constructor for the persistence layer.
    }

    public AccountId Id { get; private set; }

    /// <summary>The single currency this account holds. Cross-currency is out of scope.</summary>
    public Currency Currency { get; private set; }

    /// <summary>Settled funds, in minor units. Never negative.</summary>
    public long BalanceMinorUnits { get; private set; }

    /// <summary>
    /// The balance the account was opened with, in minor units. Never changes.
    /// </summary>
    /// <remarks>
    /// Stored rather than derived, because it is what makes the reconciliation check
    /// mean anything. The invariant is
    /// <c>balance == opening_balance + SUM(ledger.signed_amount)</c>: with the opening
    /// balance recorded independently, any balance change that happened without a
    /// matching ledger line shows up as a mismatch. Reconstructing the opening balance
    /// from the current balance and the ledger would make the equation true by
    /// construction and the check worthless.
    /// </remarks>
    public long OpeningBalanceMinorUnits { get; private set; }

    /// <summary>
    /// Funds committed to in-flight authorizations. Always between zero and
    /// <see cref="BalanceMinorUnits"/>.
    /// </summary>
    public long ReservedMinorUnits { get; private set; }

    public AccountStatus Status { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// PostgreSQL's <c>xmin</c> system column, mapped as an optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// Using the row's transaction id means the database itself supplies the version;
    /// there is no application-maintained counter to forget to increment, and any update
    /// built on a stale read fails with zero rows affected rather than silently
    /// overwriting a concurrent change.
    /// </remarks>
    public uint Version { get; private set; }

    /// <summary>Funds that can back a new authorization.</summary>
    public long AvailableMinorUnits => BalanceMinorUnits - ReservedMinorUnits;

    public Money Balance => new(BalanceMinorUnits, Currency);

    public Money Reserved => new(ReservedMinorUnits, Currency);

    public Money Available => new(AvailableMinorUnits, Currency);

    public bool CanSend => Status == AccountStatus.Active;

    public bool CanReceive => Status is AccountStatus.Active or AccountStatus.Frozen;

    /// <summary>Opens a new account with an initial settled balance.</summary>
    public static DomainResult<Account> Open(
        AccountId id,
        Currency currency,
        Money openingBalance,
        string displayName,
        DateTimeOffset now)
    {
        if (currency.IsNone || !currency.IsSupported)
        {
            return DomainResult<Account>.Failure(DomainError.CurrencyMismatch);
        }

        if (openingBalance.Currency != currency)
        {
            return DomainResult<Account>.Failure(DomainError.CurrencyMismatch);
        }

        if (openingBalance.IsNegative)
        {
            return DomainResult<Account>.Failure(DomainError.AmountNotPositive);
        }

        return DomainResult<Account>.Success(new Account
        {
            Id = id,
            Currency = currency,
            BalanceMinorUnits = openingBalance.MinorUnits,
            OpeningBalanceMinorUnits = openingBalance.MinorUnits,
            ReservedMinorUnits = 0L,
            Status = AccountStatus.Active,
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    /// <summary>
    /// Moves <paramref name="amount"/> from available into reserved for an authorization.
    /// </summary>
    public DomainResult TryReserve(in Money amount, DateTimeOffset now)
    {
        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (!CanSend)
        {
            return DomainResult.Failure(DomainError.AccountNotActive);
        }

        if (amount.MinorUnits > AvailableMinorUnits)
        {
            return DomainResult.Failure(DomainError.InsufficientFunds);
        }

        ReservedMinorUnits += amount.MinorUnits;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Releases a reservation without moving funds, for a voided or expired authorization.
    /// </summary>
    public DomainResult TryReleaseReservation(in Money amount, DateTimeOffset now)
    {
        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (amount.MinorUnits > ReservedMinorUnits)
        {
            return DomainResult.Failure(DomainError.ReservationExceeded);
        }

        ReservedMinorUnits -= amount.MinorUnits;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Settles a reservation: the funds leave both the reservation and the balance.
    /// </summary>
    public DomainResult TryCaptureReserved(in Money amount, DateTimeOffset now)
    {
        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (amount.MinorUnits > ReservedMinorUnits)
        {
            return DomainResult.Failure(DomainError.ReservationExceeded);
        }

        // The reservation already guaranteed the balance covers this, but the balance is
        // re-checked rather than assumed: this is the last gate before money moves.
        if (amount.MinorUnits > BalanceMinorUnits)
        {
            return DomainResult.Failure(DomainError.InsufficientFunds);
        }

        ReservedMinorUnits -= amount.MinorUnits;
        BalanceMinorUnits -= amount.MinorUnits;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>Credits settled funds, for a capture's destination or a refund's payer.</summary>
    public DomainResult TryCredit(in Money amount, DateTimeOffset now)
    {
        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (!CanReceive)
        {
            return DomainResult.Failure(DomainError.AccountNotActive);
        }

        if (!Balance.TryAdd(amount, out Money credited))
        {
            return DomainResult.Failure(DomainError.ArithmeticOverflow);
        }

        BalanceMinorUnits = credited.MinorUnits;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    /// <summary>
    /// Debits settled funds directly, with no prior reservation. Used by refunds, which
    /// draw from the merchant's balance without an authorization step.
    /// </summary>
    public DomainResult TryDebit(in Money amount, DateTimeOffset now)
    {
        if (amount.Currency != Currency)
        {
            return DomainResult.Failure(DomainError.CurrencyMismatch);
        }

        if (!amount.IsPositive)
        {
            return DomainResult.Failure(DomainError.AmountNotPositive);
        }

        if (Status == AccountStatus.Closed)
        {
            return DomainResult.Failure(DomainError.AccountNotActive);
        }

        if (amount.MinorUnits > AvailableMinorUnits)
        {
            return DomainResult.Failure(DomainError.InsufficientFunds);
        }

        BalanceMinorUnits -= amount.MinorUnits;
        UpdatedAt = now;
        return DomainResult.Success;
    }

    public void Freeze(DateTimeOffset now)
    {
        Status = AccountStatus.Frozen;
        UpdatedAt = now;
    }

    public void Reactivate(DateTimeOffset now)
    {
        if (Status == AccountStatus.Frozen)
        {
            Status = AccountStatus.Active;
            UpdatedAt = now;
        }
    }

    /// <summary>
    /// Closes the account. Rejected while funds are still reserved, because closing would
    /// strand in-flight authorizations with nowhere to settle.
    /// </summary>
    public DomainResult TryClose(DateTimeOffset now)
    {
        if (ReservedMinorUnits != 0L)
        {
            return DomainResult.Failure(DomainError.ReservationExceeded);
        }

        Status = AccountStatus.Closed;
        UpdatedAt = now;
        return DomainResult.Success;
    }
}
