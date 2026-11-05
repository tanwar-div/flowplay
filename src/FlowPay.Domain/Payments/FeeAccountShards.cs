using FlowPay.Domain.Primitives;

namespace FlowPay.Domain.Payments;

/// <summary>
/// Spreads platform fee income across a family of accounts to remove a hot row.
/// </summary>
/// <remarks>
/// <para>
/// Every capture credits the platform's fee income, so a single fee account per currency
/// makes one row part of every payment transaction. PostgreSQL holds a row lock until
/// commit, so that one row serializes the entire capture path: measured throughput
/// equals one divided by the mean transaction duration, no matter how many API replicas
/// or distinct payer accounts are involved. Profiling confirmed exactly that — sixty of
/// sixty-four connections waiting on a single <c>accounts</c> tuple, and eight times more
/// payer accounts changing nothing.
/// </para>
/// <para>
/// The fix is to shard. A capture picks one of <c>N</c> fee accounts from the payment's
/// id, so concurrent captures contend only when they land on the same shard — reducing
/// contention by roughly <c>N</c>. Total fee income is the sum across the family, which
/// a reporting query handles trivially and which the double-entry invariant does not care
/// about at all: every shard is a real account, so the ledger still balances and the
/// balance reconciliation still applies unchanged.
/// </para>
/// <para>
/// Shard ids are derived from the configured base id by overwriting its last two bytes
/// with the shard index, so the family is contiguous and readable: a base of
/// <c>...-000001000000</c> yields <c>...-000001000000</c> through <c>...-00000100000f</c>
/// for sixteen shards. Keeping them legible matters because these are the rows an
/// operator reads when reconciling fee income.
/// </para>
/// </remarks>
public static class FeeAccountShards
{
    /// <summary>The most shards a fee account family may have.</summary>
    /// <remarks>
    /// Bounded by the two bytes the derivation overwrites. It is also well past the point
    /// of diminishing returns: contention falls as 1/N, so the step from 64 to 256 buys
    /// far less than the step from 1 to 16, while adding rows to reconcile.
    /// </remarks>
    public const int MaxShards = 256;

    /// <summary>The default shard count, which is ample for a few thousand captures a second.</summary>
    public const int DefaultShards = 16;

    /// <summary>
    /// The id of shard <paramref name="shardIndex"/> in the family rooted at
    /// <paramref name="baseId"/>.
    /// </summary>
    public static AccountId Derive(Guid baseId, int shardIndex)
    {
        if ((uint)shardIndex >= MaxShards)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shardIndex), shardIndex, $"The shard index must be below {MaxShards}.");
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!baseId.TryWriteBytes(bytes))
        {
            throw new ArgumentException("Could not read the base account id.", nameof(baseId));
        }

        // The final two bytes of the byte array are the last four hex digits of the
        // string form, so the shard index is visible in the id as written.
        bytes[14] = (byte)(shardIndex >> 8);
        bytes[15] = (byte)shardIndex;

        return new AccountId(new Guid(bytes));
    }

    /// <summary>
    /// Picks the shard a payment's fee belongs to.
    /// </summary>
    /// <remarks>
    /// Derived from the payment id rather than chosen at random, so a capture that is
    /// retried — by the idempotency layer, or by an operator replaying it — resolves to
    /// the same shard and therefore the same account. A random choice would spread one
    /// logical payment's fee across shards depending on which attempt committed.
    /// </remarks>
    public static int SelectShard(PaymentId paymentId, int shardCount)
    {
        if (shardCount <= 1)
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[16];

        if (!paymentId.Value.TryWriteBytes(bytes))
        {
            return 0;
        }

        // UUIDv7 puts a timestamp in the leading bytes and randomness in the trailing
        // ones, so the tail is what distributes evenly. Using the leading bytes would
        // make consecutive payments cluster on the same shard, which is precisely the
        // contention this is meant to avoid.
        uint tail = (uint)bytes[15] | ((uint)bytes[14] << 8) | ((uint)bytes[13] << 16);

        return (int)(tail % (uint)shardCount);
    }

    /// <summary>Writes every shard id in a family into <paramref name="destination"/>.</summary>
    public static int DeriveAll(Guid baseId, int shardCount, Span<AccountId> destination)
    {
        int count = Math.Min(shardCount, destination.Length);

        for (int i = 0; i < count; i++)
        {
            destination[i] = Derive(baseId, i);
        }

        return count;
    }
}
