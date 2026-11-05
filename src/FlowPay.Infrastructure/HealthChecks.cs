using FlowPay.Infrastructure.Messaging;
using FlowPay.Infrastructure.Outbox;
using FlowPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlowPay.Infrastructure;

/// <summary>Verifies the database is reachable and accepting queries.</summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly FlowPayDbContext _context;

    public DatabaseHealthCheck(FlowPayDbContext context) => _context = context;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A real round trip, not just a pool lookup: CanConnectAsync executes a
            // query, so a server that has accepted the TCP connection but cannot serve
            // requests is reported as unhealthy rather than healthy.
            bool reachable = await _context.Database
                .CanConnectAsync(cancellationToken)
                .ConfigureAwait(false);

            return reachable
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL did not respond.");
        }
        catch (Exception failure)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", failure);
        }
    }
}

/// <summary>
/// Reports on the outbox backlog.
/// </summary>
/// <remarks>
/// Degraded rather than unhealthy when the backlog is large. A deep outbox means events
/// are late, not that the API is broken — payments are still being accepted and committed
/// correctly. Returning unhealthy would take the instance out of the load balancer and
/// reduce the capacity available to drain the backlog, making the problem worse.
/// </remarks>
public sealed class OutboxHealthCheck : IHealthCheck
{
    private readonly OutboxHealthState _state;

    public OutboxHealthCheck(OutboxHealthState state) => _state = state;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_state.HasSample)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy("Outbox depth has not been sampled yet."));
        }

        Dictionary<string, object> data = new(StringComparer.Ordinal)
        {
            ["pending"] = _state.Pending,
            ["failed"] = _state.Failed,
            ["oldestPendingAgeSeconds"] = _state.OldestPendingAgeSeconds,
        };

        if (_state.IsHealthy)
        {
            return Task.FromResult(
                HealthCheckResult.Healthy("Outbox is draining normally.", data));
        }

        return Task.FromResult(HealthCheckResult.Degraded(
            _state.Failed > 0L
                ? "Outbox has messages parked after exhausting their retries."
                : "Outbox backlog is above its threshold.",
            data: data));
    }
}

/// <summary>Verifies the broker connection can be established.</summary>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMqConnectionProvider _connections;

    public RabbitMqHealthCheck(RabbitMqConnectionProvider connections)
        => _connections = connections;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            RabbitMQ.Client.IConnection connection = await _connections
                .GetConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            return connection.IsOpen
                ? HealthCheckResult.Healthy("RabbitMQ connection is open.")
                : HealthCheckResult.Degraded("RabbitMQ connection is closed; recovery pending.");
        }
        catch (Exception failure)
        {
            // Degraded, not unhealthy: the outbox holds events durably while the broker
            // is away, so payments continue to be accepted correctly. Only the
            // asynchronous work downstream is delayed.
            return HealthCheckResult.Degraded("RabbitMQ is unreachable.", failure);
        }
    }
}
