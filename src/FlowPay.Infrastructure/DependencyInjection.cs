using FlowPay.Application.Abstractions;
using FlowPay.Application.Idempotency;
using FlowPay.Application.Outbox;
using FlowPay.Application.Payments;
using FlowPay.Infrastructure.Idempotency;
using FlowPay.Infrastructure.Messaging;
using FlowPay.Infrastructure.Outbox;
using FlowPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowPay.Infrastructure;

/// <summary>Service registration for everything outside the domain.</summary>
public static class DependencyInjection
{
    /// <summary>The configuration key holding the PostgreSQL connection string.</summary>
    public const string ConnectionStringName = "FlowPay";

    /// <summary>
    /// Registers persistence, idempotency, messaging and the shared application services.
    /// </summary>
    /// <remarks>
    /// Background services are not registered here. The API process and the worker
    /// process need different ones — the API runs no dispatcher, the worker runs no HTTP
    /// pipeline — and keeping that choice at the composition root means neither process
    /// silently starts work the other is responsible for.
    /// </remarks>
    public static IServiceCollection AddFlowPayInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<FlowPayOptions>()
            .Bind(configuration.GetSection(FlowPayOptions.SectionName))
            .ValidateDataAnnotations()
            // Validated at startup rather than on first use. A misconfigured fee account
            // or fee rate should stop a deployment, not surface as a failed payment an
            // hour later.
            .ValidateOnStart();

        services
            .AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMetrics();
        services.AddSingleton<IClock>(SystemClock.Instance);

        string connectionString = ResolveConnectionString(configuration);

        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            NpgsqlDataSourceBuilder builder = new(connectionString);
            return builder.Build();
        });

        // A pooled context, not a new one per request. At a few hundred requests a second
        // the model-building and internal-service setup EF does per instance is a
        // measurable share of request time; pooling amortizes it to nearly nothing and
        // was worth several milliseconds off the p95.
        services.AddDbContextPool<FlowPayDbContext>((provider, options) =>
        {
            NpgsqlDataSource dataSource = provider.GetRequiredService<NpgsqlDataSource>();

            options
                .UseNpgsql(dataSource, npgsql =>
                {
                    npgsql.CommandTimeout(30);

                    // EF's execution strategy is deliberately NOT enabled. It retries a
                    // whole operation, which is unsafe for code that manages its own
                    // transaction: a retry would re-run the idempotency claim and the
                    // business work against a transaction that no longer exists. Retries
                    // belong at the HTTP layer, where the idempotency key makes them safe.
                })
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IRefundRepository, RefundRepository>();
        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IInboxStore, PostgresInboxStore>();
        services.AddScoped<Projections.SettlementProjectionStore>();
        services.AddScoped<Projections.PaymentAuditStore>();

        services.AddScoped<IdempotentExecutor>();
        services.AddScoped<SettlementEngine>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentQueryService>();
        services.AddScoped<AccountService>();

        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton<OutboxMetrics>();
        services.AddSingleton<OutboxHealthState>();

        services.AddSingleton(provider => new DatabaseMigrator(
            connectionString,
            provider.GetRequiredService<ILogger<DatabaseMigrator>>()));

        return services;
    }

    /// <summary>Registers the outbox dispatcher and its depth monitor.</summary>
    public static IServiceCollection AddFlowPayOutboxDispatcher(this IServiceCollection services)
    {
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<OutboxDepthMonitor>();
        return services;
    }

    /// <summary>Registers the scheduled maintenance sweeps.</summary>
    public static IServiceCollection AddFlowPayMaintenance(this IServiceCollection services)
    {
        services.AddHostedService<AuthorizationExpirySweep>();
        services.AddHostedService<IdempotencyPurgeService>();
        return services;
    }

    /// <summary>Registers the database, outbox and broker health checks.</summary>
    public static IServiceCollection AddFlowPayHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"])
            .AddCheck<OutboxHealthCheck>("outbox", tags: ["ready"])
            .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

        return services;
    }

    /// <summary>Brings the database schema up to date. Called once at startup.</summary>
    public static async Task MigrateFlowPayDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        DatabaseMigrator migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Declares the broker topology. Called once at startup.</summary>
    public static async Task DeclareFlowPayTopologyAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        RabbitMqConnectionProvider connections =
            services.GetRequiredService<RabbitMqConnectionProvider>();

        ILogger logger = services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RabbitMqTopology));

        await using RabbitMQ.Client.IChannel channel = await connections
            .CreateConsumerChannelAsync(cancellationToken)
            .ConfigureAwait(false);

        await RabbitMqTopology
            .DeclareAsync(channel, logger, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        throw new InvalidOperationException(
            $"Connection string '{ConnectionStringName}' is not configured. " +
            "Set ConnectionStrings__FlowPay or the ConnectionStrings:FlowPay configuration key.");
    }
}
