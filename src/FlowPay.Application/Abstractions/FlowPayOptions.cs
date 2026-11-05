using System.ComponentModel.DataAnnotations;

namespace FlowPay.Application.Abstractions;

/// <summary>Processing rules and tuning knobs, bound from configuration.</summary>
public sealed class FlowPayOptions
{
    public const string SectionName = "FlowPay";

    /// <summary>
    /// The internal account that receives processing fees, keyed by ISO-4217 currency code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fees are credited to a real account rather than simply subtracted, so every
    /// capture's ledger lines still sum to zero. Without a fee account the fee would be
    /// money that left the payer and arrived nowhere, and the double-entry invariant —
    /// the system's main correctness oracle — would report every captured payment as
    /// unbalanced.
    /// </para>
    /// <para>
    /// One account per currency, not one overall. An account holds a single currency, so
    /// a single fee account would make a capture in any other currency fail on a currency
    /// mismatch at the moment the fee was credited. Keying by currency means adding a
    /// settlement currency is a configuration change rather than a schema change.
    /// </para>
    /// </remarks>
    [Required]
    [MinLength(1)]
    public Dictionary<string, Guid> PlatformFeeAccounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many accounts each currency's fee income is spread across.
    /// </summary>
    /// <remarks>
    /// Every capture credits platform fee income, and PostgreSQL holds a row lock until
    /// commit, so a single fee account per currency makes one row part of every capture
    /// and serializes the whole path. Sharding spreads the credits across a family of
    /// accounts; see <see cref="Domain.Payments.FeeAccountShards"/> for the measurement
    /// that motivated it.
    /// </remarks>
    [Range(1, Domain.Payments.FeeAccountShards.MaxShards)]
    public int FeeAccountShards { get; set; } = Domain.Payments.FeeAccountShards.DefaultShards;

    /// <summary>Proportional fee component in basis points. Default 2.9%.</summary>
    [Range(0, 10_000)]
    public int FeeBasisPoints { get; set; } = 290;

    /// <summary>Fixed fee component in minor units. Default 30 cents.</summary>
    [Range(0, long.MaxValue)]
    public long FeeFixedMinorUnits { get; set; } = 30L;

    /// <summary>
    /// How long a completed idempotency record is kept.
    /// </summary>
    /// <remarks>
    /// Long enough to cover any realistic client retry window, short enough that the
    /// table stays small: it is written on every single mutating request, so unbounded
    /// retention would make it the largest table in the database within days.
    /// </remarks>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    public OutboxOptions Outbox { get; set; } = new();

    public MaintenanceOptions Maintenance { get; set; } = new();
}

/// <summary>Dispatcher tuning for the transactional outbox.</summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// Rows claimed per dispatch cycle.
    /// </summary>
    /// <remarks>
    /// Sized against the sustained write rate: at 400 payments a second each producing
    /// one event, a 256-row batch on a 250 ms cycle clears roughly 1,000 events a second,
    /// which leaves headroom to drain a backlog without holding row locks long enough to
    /// interfere with the request path.
    /// </remarks>
    [Range(1, 10_000)]
    public int BatchSize { get; set; } = 256;

    /// <summary>How long to wait after an empty poll before looking again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Publish attempts before a row is parked as failed.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 8;

    /// <summary>First retry delay; doubles on each subsequent attempt.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the retry delay.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fraction of the computed delay added as random jitter.
    /// </summary>
    /// <remarks>
    /// Prevents the thundering herd when a broker recovers: without jitter, every row
    /// that failed during an outage is scheduled for the same instant.
    /// </remarks>
    [Range(0d, 1d)]
    public double RetryJitterFactor { get; set; } = 0.5d;

    /// <summary>
    /// Pending backlog above which the health check reports degraded.
    /// </summary>
    public long PendingDepthWarningThreshold { get; set; } = 10_000L;
}

/// <summary>Background maintenance schedules.</summary>
public sealed class MaintenanceOptions
{
    /// <summary>How often to look for authorizations past their window.</summary>
    public TimeSpan AuthorizationExpirySweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Authorizations expired per sweep.</summary>
    [Range(1, 10_000)]
    public int AuthorizationExpiryBatchSize { get; set; } = 200;

    /// <summary>How often to prune expired idempotency records.</summary>
    public TimeSpan IdempotencyPurgeInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Idempotency records deleted per pass.</summary>
    [Range(1, 100_000)]
    public int IdempotencyPurgeBatchSize { get; set; } = 5_000;
}
