namespace FlowPay.Application.Idempotency;

/// <summary>The outcome of trying to claim an idempotency key.</summary>
public enum IdempotencyClaimOutcome : byte
{
    /// <summary>This caller owns the key and must do the work.</summary>
    Claimed = 0,

    /// <summary>Another request already completed under this key.</summary>
    AlreadyCompleted = 1,

    /// <summary>Another request holds the key and has not finished.</summary>
    InProgressElsewhere = 2,
}

/// <summary>Result of a claim attempt.</summary>
public readonly record struct IdempotencyClaim(
    IdempotencyClaimOutcome Outcome,
    IdempotencyRecord? Existing)
{
    public bool IsClaimed => Outcome == IdempotencyClaimOutcome.Claimed;
}

/// <summary>
/// Storage for idempotency claims.
/// </summary>
/// <remarks>
/// The contract that matters is on <see cref="TryClaimAsync"/>: it must be atomic across
/// every API instance. The PostgreSQL implementation gets that from a unique index and
/// <c>INSERT ... ON CONFLICT DO NOTHING</c>, so the guarantee comes from the database
/// rather than from any coordination between processes — which is what makes it hold when
/// the API is running behind a load balancer on several machines.
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="key"/> for <paramref name="endpoint"/>.
    /// </summary>
    /// <remarks>
    /// Must be called inside the same transaction as the work it guards. A competing
    /// transaction's insert blocks on the unique index until this one commits or rolls
    /// back, which is exactly the serialization the protocol needs: the loser of the race
    /// either sees a completed record to replay, or — if the winner rolled back — wins
    /// the claim itself and proceeds.
    /// </remarks>
    Task<IdempotencyClaim> TryClaimAsync(
        string endpoint,
        string key,
        byte[] requestFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the response for the claimed key.</summary>
    Task CompleteAsync(
        string endpoint,
        string key,
        int statusCode,
        string responseBody,
        Guid? resourceId,
        CancellationToken cancellationToken = default);

    Task<IdempotencyRecord?> FindAsync(
        string endpoint,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes expired records. Called by the maintenance worker.</summary>
    Task<int> PurgeExpiredAsync(int batchSize, CancellationToken cancellationToken = default);
}
