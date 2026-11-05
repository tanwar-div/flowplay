using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;

namespace FlowPay.Application.Abstractions;

/// <summary>Reads and writes accounts.</summary>
public interface IAccountRepository
{
    /// <summary>Loads an account without locking it, for read-only queries.</summary>
    Task<Account?> FindAsync(AccountId id, CancellationToken cancellationToken = default);

    /// <summary>Loads a single account with <c>SELECT ... FOR UPDATE</c>.</summary>
    Task<Account?> LockForUpdateAsync(AccountId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks every account in <paramref name="ids"/>, always in ascending id order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Locking the rows up front is what makes concurrent payments safe: the balance
    /// check and the balance update happen while no other transaction can read the row,
    /// so two authorizations cannot both see the same funds as available.
    /// </para>
    /// <para>
    /// The ordering is the part that matters under concurrent load. A payment A-&gt;B and a
    /// simultaneous payment B-&gt;A that each locked their own source account first would
    /// deadlock. Sorting by id means every transaction in the system acquires account
    /// locks in the same global order, so a wait cycle cannot form — which is a stronger
    /// guarantee than relying on PostgreSQL's deadlock detector to break the cycle after
    /// the fact, and it costs nothing.
    /// </para>
    /// </remarks>
    Task<AccountLockSet> LockForUpdateAsync(
        IReadOnlyList<AccountId> ids,
        CancellationToken cancellationToken = default);

    void Add(Account account);

    /// <summary>
    /// Recomputes every account's balance from the ledger and returns the accounts whose
    /// stored balance disagrees. Used by the reconciliation check, not by request paths.
    /// </summary>
    Task<IReadOnlyList<BalanceDiscrepancy>> FindBalanceDiscrepanciesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The accounts locked by one call, addressable by id.
/// </summary>
/// <remarks>
/// A small struct over an array rather than a dictionary: a transaction locks two or three
/// accounts, and a linear scan over three entries beats hashing on a path that runs for
/// every payment.
/// </remarks>
public readonly struct AccountLockSet
{
    private readonly Account[] _accounts;

    public AccountLockSet(Account[] accounts) => _accounts = accounts;

    public int Count => _accounts?.Length ?? 0;

    /// <summary>Returns the locked account with this id, or null if it does not exist.</summary>
    public Account? Find(AccountId id)
    {
        Account[]? accounts = _accounts;

        if (accounts is null)
        {
            return null;
        }

        for (int i = 0; i < accounts.Length; i++)
        {
            if (accounts[i].Id == id)
            {
                return accounts[i];
            }
        }

        return null;
    }
}

/// <summary>An account whose stored balance disagrees with the ledger.</summary>
public readonly record struct BalanceDiscrepancy(
    AccountId AccountId,
    long StoredMinorUnits,
    long LedgerMinorUnits)
{
    public long DeltaMinorUnits => StoredMinorUnits - LedgerMinorUnits;
}

/// <summary>Reads and writes payment intents.</summary>
public interface IPaymentRepository
{
    Task<PaymentIntent?> FindAsync(PaymentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a payment with <c>SELECT ... FOR UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// The refund path depends on this. Without the row lock, two concurrent refunds both
    /// read the same pre-refund total, both compute a permissible new total, and both
    /// write — producing a payment refunded beyond its captured amount. With it they
    /// serialize, and the second one sees the first one's total and is rejected.
    /// </remarks>
    Task<PaymentIntent?> LockForUpdateAsync(
        PaymentId id,
        CancellationToken cancellationToken = default);

    void Add(PaymentIntent payment);

    /// <summary>
    /// Finds authorizations whose window has elapsed, for the expiry sweep. Returns ids
    /// only: the sweep locks and revalidates each one individually.
    /// </summary>
    Task<IReadOnlyList<PaymentId>> FindExpiredAuthorizationsAsync(
        DateTimeOffset asOf,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns payments whose <c>refunded</c> total exceeds their captured amount. Should
    /// always be empty; the stress tests assert that it is.
    /// </summary>
    Task<IReadOnlyList<PaymentId>> FindOverRefundedAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Writes refund records.</summary>
public interface IRefundRepository
{
    void Add(Refund refund);

    Task<Refund?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Refund>> ListForPaymentAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default);
}

/// <summary>Appends to the double-entry ledger and verifies its invariants.</summary>
public interface ILedgerRepository
{
    void AddRange(ReadOnlySpan<LedgerEntry> entries);

    Task<IReadOnlyList<LedgerEntry>> ListForPaymentAsync(
        PaymentId paymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The signed sum of every ledger line. Must be exactly zero: a non-zero result means
    /// money was created or destroyed somewhere.
    /// </summary>
    Task<long> SumSignedMinorUnitsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Payments whose own ledger lines do not balance, which localizes an imbalance to
    /// the operations that caused it.
    /// </summary>
    Task<IReadOnlyList<PaymentImbalance>> FindUnbalancedPaymentsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>A payment whose ledger lines do not sum to zero.</summary>
public readonly record struct PaymentImbalance(PaymentId PaymentId, long NetMinorUnits);
