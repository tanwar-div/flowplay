namespace FlowPay.Infrastructure.Messaging;

/// <summary>Consumer-side deduplication.</summary>
public interface IInboxStore
{
    /// <summary>
    /// Records that <paramref name="consumer"/> is handling <paramref name="messageId"/>.
    /// </summary>
    /// <returns>
    /// True if this is the first time; false if the message has already been handled.
    /// </returns>
    /// <remarks>
    /// Must be called inside the same transaction as the handler's effects. That is what
    /// converts at-least-once delivery into exactly-once effects: the claim and the work
    /// commit together, so a redelivery either finds the claim and skips, or — if the
    /// original attempt rolled back — finds nothing and does the work for real.
    /// </remarks>
    Task<bool> TryBeginAsync(
        Guid messageId,
        string consumer,
        string eventType,
        int deliveryAttempt,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes inbox records older than <paramref name="retention"/>.</summary>
    Task<int> PurgeOlderThanAsync(
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken = default);
}
