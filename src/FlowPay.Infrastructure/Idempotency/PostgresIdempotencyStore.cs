using FlowPay.Application.Abstractions;
using FlowPay.Application.Idempotency;
using FlowPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace FlowPay.Infrastructure.Idempotency;

/// <summary>
/// Idempotency claims backed by a PostgreSQL unique index.
/// </summary>
/// <remarks>
/// <para>
/// The claim is a single <c>INSERT ... ON CONFLICT DO NOTHING</c>. Its behaviour under
/// contention is the entire point:
/// </para>
/// <list type="bullet">
/// <item><description>If no row exists, the insert succeeds and this caller owns the key.</description></item>
/// <item><description>If a <em>committed</em> row exists, the insert affects no rows and this
/// caller replays the stored response.</description></item>
/// <item><description>If another <em>open</em> transaction has inserted the same key, PostgreSQL
/// blocks this statement on the unique index until that transaction ends. If it commits,
/// this caller sees no rows inserted and replays; if it rolls back, this caller's insert
/// succeeds and it does the work instead.</description></item>
/// </list>
/// <para>
/// That third case is what makes the guarantee hold without any application-level
/// coordination. Two API instances on different machines racing on the same key are
/// serialized by the index itself — no distributed lock, no leader election, no cache
/// with its own consistency problem.
/// </para>
/// </remarks>
public sealed class PostgresIdempotencyStore : IIdempotencyStore
{
    private readonly FlowPayDbContext _context;
    private readonly IClock _clock;
    private readonly FlowPayOptions _options;

    public PostgresIdempotencyStore(
        FlowPayDbContext context,
        IClock clock,
        IOptions<FlowPayOptions> options)
    {
        _context = context;
        _clock = clock;
        _options = options.Value;
    }

    /// <inheritdoc />
    public async Task<IdempotencyClaim> TryClaimAsync(
        string endpoint,
        string key,
        byte[] requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.UtcNow;

        const string ClaimSql =
            """
            INSERT INTO idempotency_keys
                (endpoint, key, request_fingerprint, status, created_at, expires_at)
            VALUES
                (@endpoint, @key, @fingerprint, @status, @created_at, @expires_at)
            ON CONFLICT (endpoint, key) DO NOTHING
            """;

        await using (NpgsqlCommand claim = await _context
            .CreateCommandAsync(ClaimSql, cancellationToken)
            .ConfigureAwait(false))
        {
            claim.Parameters.AddWithValue("endpoint", endpoint);
            claim.Parameters.AddWithValue("key", key);
            claim.Parameters.Add("fingerprint", NpgsqlDbType.Bytea).Value = requestFingerprint;
            claim.Parameters.AddWithValue("status", (short)IdempotencyStatus.InProgress);
            claim.Parameters.AddWithValue("created_at", now);
            claim.Parameters.AddWithValue("expires_at", now + _options.IdempotencyRetention);

            int inserted = await claim
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (inserted == 1)
            {
                return new IdempotencyClaim(IdempotencyClaimOutcome.Claimed, Existing: null);
            }
        }

        // The insert found a committed row. Read it so the caller can compare fingerprints
        // and replay the stored response.
        IdempotencyRecord? existing = await FindAsync(endpoint, key, cancellationToken)
            .ConfigureAwait(false);

        IdempotencyClaimOutcome outcome = existing?.Status == IdempotencyStatus.Completed
            ? IdempotencyClaimOutcome.AlreadyCompleted
            : IdempotencyClaimOutcome.InProgressElsewhere;

        return new IdempotencyClaim(outcome, existing);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        string endpoint,
        string key,
        int statusCode,
        string responseBody,
        Guid? resourceId,
        CancellationToken cancellationToken = default)
    {
        const string Sql =
            """
            UPDATE idempotency_keys
            SET status               = @status,
                response_status_code = @status_code,
                response_body        = @body,
                resource_id          = @resource_id,
                completed_at         = @completed_at
            WHERE endpoint = @endpoint AND key = @key
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("status", (short)IdempotencyStatus.Completed);
        command.Parameters.AddWithValue("status_code", statusCode);
        command.Parameters.Add("body", NpgsqlDbType.Text).Value = responseBody;
        command.Parameters.AddWithValue(
            "resource_id", resourceId.HasValue ? resourceId.Value : DBNull.Value);
        command.Parameters.AddWithValue("completed_at", _clock.UtcNow);
        command.Parameters.AddWithValue("endpoint", endpoint);
        command.Parameters.AddWithValue("key", key);

        int updated = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (updated != 1)
        {
            // The claim row vanished between the claim and the completion, which should
            // be impossible inside one transaction. Failing loudly is the right move:
            // committing the work without its idempotency record would let a retry run
            // the same operation a second time.
            throw new InvalidOperationException(
                $"Idempotency record {endpoint}/{key} was not found when storing its response.");
        }
    }

    public Task<IdempotencyRecord?> FindAsync(
        string endpoint,
        string key,
        CancellationToken cancellationToken = default)
        => _context.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Endpoint == endpoint && r.Key == key,
                cancellationToken);

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        // Deleted in bounded batches rather than one large statement. The table is
        // written on every mutating request, so an unbounded DELETE would hold locks
        // across a hot index long enough to show up in request latency.
        const string Sql =
            """
            DELETE FROM idempotency_keys
            WHERE (endpoint, key) IN (
                SELECT endpoint, key
                FROM idempotency_keys
                WHERE expires_at < @now
                ORDER BY expires_at
                LIMIT @batch_size
            )
            """;

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        command.Parameters.AddWithValue("now", _clock.UtcNow);
        command.Parameters.AddWithValue("batch_size", batchSize);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
