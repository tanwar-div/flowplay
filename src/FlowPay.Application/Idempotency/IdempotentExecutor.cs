using FlowPay.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FlowPay.Application.Idempotency;

/// <summary>A handler's response, ready to be both returned and stored for replay.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Json">Serialized response body.</param>
/// <param name="ResourceId">The affected resource, for logging and lookups.</param>
/// <param name="Persist">
/// False for responses that must not be replayed — a transient failure should let the
/// client retry and get a real attempt, not a cached error.
/// </param>
public readonly record struct OperationResponse(
    int StatusCode,
    string Json,
    Guid? ResourceId,
    bool Persist = true)
{
    public static OperationResponse Transient(int statusCode, string json)
        => new(statusCode, json, null, Persist: false);
}

/// <summary>How an idempotent request was served.</summary>
public enum ExecutionKind : byte
{
    /// <summary>The work ran for the first time.</summary>
    Executed = 0,

    /// <summary>A stored response was replayed.</summary>
    Replayed = 1,

    /// <summary>An identical request is still in flight.</summary>
    InProgress = 2,

    /// <summary>The key was reused with different parameters.</summary>
    KeyReuse = 3,
}

/// <summary>The result of an idempotent execution.</summary>
public readonly record struct ExecutionResult(
    ExecutionKind Kind,
    int StatusCode,
    string Json,
    Guid? ResourceId)
{
    public bool IsReplay => Kind == ExecutionKind.Replayed;
}

/// <summary>
/// Runs an operation at most once per idempotency key.
/// </summary>
/// <remarks>
/// <para>
/// The protocol is one database transaction:
/// </para>
/// <list type="number">
/// <item><description>claim the key with an <c>INSERT ... ON CONFLICT DO NOTHING</c>;</description></item>
/// <item><description>run the operation, which writes its state changes and its outbox rows;</description></item>
/// <item><description>store the response on the claim row;</description></item>
/// <item><description>commit.</description></item>
/// </list>
/// <para>
/// Collapsing all four steps into one transaction is what makes duplicate financial
/// effects impossible rather than merely unlikely. There is no window in which the key is
/// claimed but the work is unfinished, and no window in which the work is committed but
/// the key is not: a crash at any point rolls back everything, so the client's retry gets
/// a genuine first attempt. A competing request with the same key blocks on the unique
/// index and, once this transaction commits, finds a completed record to replay.
/// </para>
/// <para>
/// The alternative — claim in one transaction, work in another — needs leases, crash
/// recovery and a story for what a half-finished claim means. This design needs none of
/// that, at the cost of holding the claim row's lock for the duration of the work.
/// </para>
/// </remarks>
public sealed class IdempotentExecutor
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IIdempotencyStore _store;
    private readonly ILogger<IdempotentExecutor> _logger;

    public IdempotentExecutor(
        IUnitOfWork unitOfWork,
        IIdempotencyStore store,
        ILogger<IdempotentExecutor> logger)
    {
        _unitOfWork = unitOfWork;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Executes <paramref name="operation"/> unless <paramref name="key"/> has already
    /// been used for <paramref name="endpoint"/>.
    /// </summary>
    public async Task<ExecutionResult> ExecuteAsync(
        string endpoint,
        string key,
        byte[] requestFingerprint,
        Func<CancellationToken, Task<OperationResponse>> operation,
        CancellationToken cancellationToken = default)
    {
        await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);

        bool committed = false;

        try
        {
            IdempotencyClaim claim = await _store
                .TryClaimAsync(endpoint, key, requestFingerprint, cancellationToken)
                .ConfigureAwait(false);

            if (!claim.IsClaimed)
            {
                ExecutionResult replay = BuildReplay(endpoint, key, requestFingerprint, claim);
                await _unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                committed = true;
                return replay;
            }

            OperationResponse response = await operation(cancellationToken).ConfigureAwait(false);

            if (!response.Persist)
            {
                // A transient failure must not be cached: rolling back releases the key
                // so the client's retry is a real attempt rather than a replayed error.
                await _unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                committed = true;

                return new ExecutionResult(
                    ExecutionKind.Executed, response.StatusCode, response.Json, response.ResourceId);
            }

            await _store
                .CompleteAsync(
                    endpoint, key, response.StatusCode, response.Json,
                    response.ResourceId, cancellationToken)
                .ConfigureAwait(false);

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;

            return new ExecutionResult(
                ExecutionKind.Executed, response.StatusCode, response.Json, response.ResourceId);
        }
        finally
        {
            if (!committed)
            {
                // Best-effort unwind. The transaction would be rolled back by the
                // connection being returned to the pool anyway, but doing it explicitly
                // keeps the failure out of the next request's way.
                try
                {
                    await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackFailure)
                {
                    _logger.LogError(
                        rollbackFailure,
                        "Rollback failed for idempotency key {Endpoint}/{Key}.",
                        endpoint,
                        key);
                }
            }
        }
    }

    private ExecutionResult BuildReplay(
        string endpoint,
        string key,
        ReadOnlySpan<byte> requestFingerprint,
        IdempotencyClaim claim)
    {
        IdempotencyRecord? existing = claim.Existing;

        if (existing is null)
        {
            _logger.LogWarning(
                "Idempotency key {Endpoint}/{Key} was claimed elsewhere but no record was readable.",
                endpoint,
                key);

            return new ExecutionResult(
                ExecutionKind.InProgress, StatusCodes.Conflict, InProgressBody, null);
        }

        if (!existing.MatchesFingerprint(requestFingerprint))
        {
            _logger.LogWarning(
                "Idempotency key {Endpoint}/{Key} was reused with different parameters.",
                endpoint,
                key);

            return new ExecutionResult(
                ExecutionKind.KeyReuse, StatusCodes.UnprocessableEntity, KeyReuseBody, null);
        }

        if (existing.Status != IdempotencyStatus.Completed || existing.ResponseBody is null)
        {
            return new ExecutionResult(
                ExecutionKind.InProgress, StatusCodes.Conflict, InProgressBody, existing.ResourceId);
        }

        return new ExecutionResult(
            ExecutionKind.Replayed,
            existing.ResponseStatusCode ?? StatusCodes.Ok,
            existing.ResponseBody,
            existing.ResourceId);
    }

    private const string InProgressBody =
        """{"type":"request_in_progress","title":"An identical request is still being processed.","status":409}""";

    private const string KeyReuseBody =
        """{"type":"idempotency_key_reuse","title":"This idempotency key was already used with different request parameters.","status":422}""";

    /// <summary>
    /// The status codes this type needs, declared locally so the application layer does
    /// not take a dependency on ASP.NET Core.
    /// </summary>
    private static class StatusCodes
    {
        internal const int Ok = 200;
        internal const int Conflict = 409;
        internal const int UnprocessableEntity = 422;
    }
}
