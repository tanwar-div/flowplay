using FlowPay.Application.Abstractions;
using FlowPay.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using FlowPay.Domain.Payments;
using Testcontainers.RabbitMq;
using Xunit;

namespace FlowPay.IntegrationTests.Fixtures;

/// <summary>
/// Brings up real PostgreSQL and RabbitMQ containers and the API against them.
/// </summary>
/// <remarks>
/// <para>
/// Real containers rather than in-memory substitutes, because the guarantees under test
/// do not exist outside PostgreSQL. <c>INSERT ... ON CONFLICT DO NOTHING</c>,
/// <c>SELECT ... FOR UPDATE</c>, <c>SKIP LOCKED</c> and the <c>CHECK</c> constraints are
/// the mechanism — a fake would either reimplement them (testing the fake) or ignore them
/// (testing nothing). The same applies to the broker: staged retry queues and
/// dead-lettering are RabbitMQ behaviours, not application code.
/// </para>
/// <para>
/// One container set is shared by every test class through a collection fixture. Starting
/// PostgreSQL and RabbitMQ costs seconds, and paying that per class would make the suite
/// slow enough that it stopped being run.
/// </para>
/// </remarks>
public sealed class FlowPayFixture : IAsyncLifetime
{
    /// <summary>
    /// The base id of the USD fee account family. Fee income is sharded across
    /// <see cref="FeeAccountShardCount"/> accounts derived from this, so tests assert
    /// membership of the family rather than one fixed id.
    /// </summary>
    public static readonly Guid UsdFeeAccountBaseId =
        Guid.Parse("00000000-0000-7000-8000-000001000000");

    public static readonly Guid EurFeeAccountBaseId =
        Guid.Parse("00000000-0000-7000-8000-000002000000");

    /// <summary>
    /// Kept small for the tests. Each shard is an account the initializer provisions at
    /// startup, and the production default of sixteen would add rows to every run for no
    /// test value — the sharding logic itself is covered by unit tests.
    /// </summary>
    public const int FeeAccountShardCount = 4;

    /// <summary>The fee account ids for a currency's family.</summary>
    public static IReadOnlyList<Guid> FeeAccountFamily(Guid baseId)
        => Enumerable
            .Range(0, FeeAccountShardCount)
            .Select(shard => FeeAccountShards.Derive(baseId, shard).Value)
            .ToArray();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowpay")
        .WithUsername("flowpay")
        .WithPassword("flowpay")
        .WithCleanUp(true)
        .Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine")
        .WithUsername("flowpay")
        .WithPassword("flowpay")
        .WithCleanUp(true)
        .Build();

    private FlowPayApiFactory? _factory;

    public FlowPayApiFactory Api => _factory
        ?? throw new InvalidOperationException("The fixture has not been initialized.");

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        // Started together: neither depends on the other, and serializing them would
        // double the suite's startup cost for no benefit.
        await Task.WhenAll(_postgres.StartAsync(), _rabbitMq.StartAsync());

        // Configuration is injected through environment variables rather than
        // WebApplicationFactory's ConfigureAppConfiguration hook. The API is a top-level
        // program that migrates the database between Build() and Run(), so the container
        // addresses have to be in place before the host is constructed at all — and
        // environment variables are the only source that both arrives that early and
        // outranks appsettings.json in the default configuration order.
        ApplyEnvironment(
            _postgres.GetConnectionString(),
            _rabbitMq.Hostname,
            _rabbitMq.GetMappedPublicPort(5672));

        _factory = new FlowPayApiFactory();

        // Forces the host to build and run its startup work: schema migration, topology
        // declaration, and provisioning the platform fee accounts.
        using HttpClient warmup = _factory.CreateClient();
        await warmup.GetAsync("/health/live");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        ClearEnvironment();

        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbitMq.DisposeAsync().AsTask());
    }

    private static readonly string[] OverriddenVariables =
    [
        "ConnectionStrings__FlowPay",
        "RabbitMq__Host",
        "RabbitMq__Port",
        "RabbitMq__UserName",
        "RabbitMq__Password",
        "RabbitMq__ClientProvidedName",
        "FlowPay__Hosting__RunOutboxDispatcher",
        "FlowPay__PlatformFeeAccounts__USD",
        "FlowPay__PlatformFeeAccounts__EUR",
        "FlowPay__FeeAccountShards",
        "FlowPay__PlatformFeeAccounts__GBP",
        "FlowPay__FeeBasisPoints",
        "FlowPay__FeeFixedMinorUnits",
        "FlowPay__IdempotencyRetention",
    ];

    private static void ApplyEnvironment(string connectionString, string rabbitHost, int rabbitPort)
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__FlowPay", connectionString);
        Environment.SetEnvironmentVariable("RabbitMq__Host", rabbitHost);
        Environment.SetEnvironmentVariable("RabbitMq__Port", rabbitPort.ToString());
        Environment.SetEnvironmentVariable("RabbitMq__UserName", "flowpay");
        Environment.SetEnvironmentVariable("RabbitMq__Password", "flowpay");
        Environment.SetEnvironmentVariable(
            "RabbitMq__ClientProvidedName", "flowpay-integration-tests");

        // The dispatcher is driven explicitly by the tests that care about it. Letting a
        // background loop race the assertions would make outbox tests flaky and their
        // failures uninformative.
        Environment.SetEnvironmentVariable("FlowPay__Hosting__RunOutboxDispatcher", "false");

        Environment.SetEnvironmentVariable(
            "FlowPay__PlatformFeeAccounts__USD", UsdFeeAccountBaseId.ToString());
        Environment.SetEnvironmentVariable(
            "FlowPay__PlatformFeeAccounts__EUR", EurFeeAccountBaseId.ToString());

        Environment.SetEnvironmentVariable(
            "FlowPay__FeeAccountShards", FeeAccountShardCount.ToString());
        Environment.SetEnvironmentVariable("FlowPay__FeeBasisPoints", "290");
        Environment.SetEnvironmentVariable("FlowPay__FeeFixedMinorUnits", "30");

        // Long enough that no test's replay is purged mid-run.
        Environment.SetEnvironmentVariable("FlowPay__IdempotencyRetention", "01:00:00");
    }

    private static void ClearEnvironment()
    {
        foreach (string variable in OverriddenVariables)
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}

/// <summary>The API host, pointed at the fixture's containers.</summary>
public sealed class FlowPayApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    /// <summary>
    /// Builds an outbox dispatcher for tests to drive one cycle at a time.
    /// </summary>
    /// <remarks>
    /// Constructed rather than resolved, because the API process does not register it as
    /// a hosted service. Driving a single cycle makes the outbox assertions exact instead
    /// of dependent on a polling interval.
    /// </remarks>
    public OutboxDispatcher CreateOutboxDispatcher()
        => ActivatorUtilities.CreateInstance<OutboxDispatcher>(Services);

    /// <summary>Opens a service scope for direct repository and store access.</summary>
    public IServiceScope CreateScope() => Services.CreateScope();

    public IClock Clock => Services.GetRequiredService<IClock>();
}

/// <summary>
/// Puts every integration test in one collection, so the containers start once.
/// </summary>
[CollectionDefinition(Name)]
public sealed class FlowPayCollection : ICollectionFixture<FlowPayFixture>
{
    public const string Name = "flowpay";
}
