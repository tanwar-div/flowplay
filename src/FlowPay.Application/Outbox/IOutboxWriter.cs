namespace FlowPay.Application.Outbox;

/// <summary>
/// Appends integration events to the outbox within the caller's transaction.
/// </summary>
/// <remarks>
/// Deliberately has no flush or publish method. A handler cannot cause an event to reach
/// the broker directly; it can only enqueue one, and the enqueue is only durable if the
/// surrounding transaction commits. The coupling is structural rather than a convention
/// someone has to remember.
/// </remarks>
public interface IOutboxWriter
{
    void Enqueue(string eventType, Guid aggregateId, string payload);
}

/// <summary>Reads and updates outbox rows. Used only by the dispatcher.</summary>
public interface IOutboxStore
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> pending rows that are due.
    /// </summary>
    /// <remarks>
    /// The implementation uses <c>FOR UPDATE SKIP LOCKED</c>. That single clause is what
    /// makes the dispatcher horizontally scalable: several worker instances can poll the
    /// same table concurrently and each one claims a disjoint batch, with no leader
    /// election, no partition assignment and no row published twice.
    /// </remarks>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Counts rows in each state, for the dispatcher's metrics and health check.</summary>
    Task<OutboxDepth> GetDepthAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns parked rows to the pending pool.</summary>
    Task<int> RequeueFailedAsync(int batchSize, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot of outbox backlog by state.</summary>
public readonly record struct OutboxDepth(long Pending, long Failed, long OldestPendingAgeSeconds);
