namespace FlowPay.Application.Abstractions;

/// <summary>
/// The transactional boundary for one request.
/// </summary>
/// <remarks>
/// <para>
/// Every write in FlowPay — the idempotency claim, the balance updates, the ledger lines
/// and the outbox rows — commits through one of these. That is the whole basis of the
/// system's safety story: because the outbox row shares a transaction with the state
/// change that produced it, there is no window in which a payment is captured but its
/// event is lost, or an event is published for a capture that rolled back.
/// </para>
/// <para>
/// Nothing here exposes a "save without a transaction" path, so a handler cannot
/// accidentally half-commit.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>True once <see cref="BeginAsync"/> has opened a transaction.</summary>
    bool HasActiveTransaction { get; }

    /// <summary>Opens a transaction at READ COMMITTED.</summary>
    /// <remarks>
    /// READ COMMITTED rather than a stricter level is deliberate. The idempotency claim
    /// relies on seeing a competing transaction's committed row immediately after
    /// unblocking on the unique index, which a snapshot taken at transaction start would
    /// hide. Serialization is provided where it is needed — by row locks — rather than
    /// globally, which keeps throughput up under concurrent load.
    /// </remarks>
    Task BeginAsync(CancellationToken cancellationToken = default);

    /// <summary>Flushes tracked changes without ending the transaction.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}
