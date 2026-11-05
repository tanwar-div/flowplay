using System.Diagnostics;
using FlowPay.Api.Contracts;
using FlowPay.Api.Endpoints;
using FlowPay.Api.Infrastructure;
using FlowPay.Application.Payments;
using FlowPay.Infrastructure;
using FlowPay.Infrastructure.Outbox;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// Container health probe mode. The runtime image ships neither curl nor wget, and
// adding a package manager layer to get one would grow the image and its attack surface
// for a single HTTP GET — so the application probes itself and exits with the result.
if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    return await HealthProbe.RunAsync();
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Serilog is configured before anything else so that a failure during the rest of
// startup is still logged in the same structured format as everything after it.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service.name", "flowpay-api")
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.AddFlowPayInfrastructure(builder.Configuration);
builder.Services.AddFlowPayHealthChecks();

// The fee accounts have to exist before the first capture, so this runs to completion
// during startup rather than in the background.
builder.Services.AddHostedService<PlatformAccountInitializer>();

// The outbox dispatcher normally runs in the worker process, where its polling and its
// broker connection are isolated from request handling. Running it alongside the API is
// opt-in, for single-process development and for the integration tests.
if (builder.Configuration.GetValue("FlowPay:Hosting:RunOutboxDispatcher", defaultValue: false))
{
    builder.Services.AddFlowPayOutboxDispatcher();
}
else
{
    // The depth monitor is cheap and feeds the readiness check, so the API keeps it even
    // when it is not dispatching.
    builder.Services.AddSingleton<OutboxDepthMonitor>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<OutboxDepthMonitor>());
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Both source-generated contexts are chained in: requests deserialize through the
    // API's context, responses serialize through the application's. Neither direction
    // falls back to reflection, which keeps serialization off the p95 budget and leaves
    // the API trim-compatible.
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, FlowPayRequestContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(1, FlowPayJsonContext.Default);
});

builder.Services.AddOpenApi();

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: "flowpay-api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            // Health and metrics probes run every few seconds from every replica and
            // from the load balancer. Tracing them would bury the payment traces that
            // matter under an endless stream of 200s.
            options.Filter = httpContext =>
                !httpContext.Request.Path.StartsWithSegments("/health")
                && !httpContext.Request.Path.StartsWithSegments("/metrics");
        })
        .AddSource("FlowPay.Outbox"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(OutboxMetrics.MeterName)
        .AddView(
            // The default duration buckets are too coarse to see a p95 target of 150 ms.
            // These are clustered around it so the histogram can actually resolve whether
            // the objective is being met.
            instrumentName: "http.server.request.duration",
            new ExplicitBucketHistogramConfiguration
            {
                Boundaries =
                [
                    0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.125,
                    0.15, 0.2, 0.3, 0.5, 1.0, 2.5, 5.0,
                ],
            })
        .AddPrometheusExporter());

WebApplication app = builder.Build();

// Schema first: the fee-account initializer and every request behind it assume the
// tables exist.
await app.Services.MigrateFlowPayDatabaseAsync();

try
{
    await app.Services.DeclareFlowPayTopologyAsync();
}
catch (Exception failure)
{
    // A broker that is not up yet must not stop the API from serving. Payments still
    // commit, and their events accumulate durably in the outbox until the dispatcher can
    // reach the broker, which declares the topology itself on connection.
    app.Logger.LogWarning(
        failure,
        "Could not declare the RabbitMQ topology at startup; the dispatcher will retry.");
}

app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, exception) =>
        exception is not null || httpContext.Response.StatusCode >= 500
            ? LogEventLevel.Error
            : httpContext.Request.Path.StartsWithSegments("/health")
                ? LogEventLevel.Verbose
                : LogEventLevel.Information;

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("TraceId", Activity.Current?.TraceId.ToString());

        // Logged so a duplicate-charge investigation can be run from the logs alone:
        // every mutating request is attributable to the key it was made under.
        if (httpContext.Request.Headers.TryGetValue(
            IdempotencyHeader.HeaderName, out Microsoft.Extensions.Primitives.StringValues key))
        {
            diagnosticContext.Set("IdempotencyKey", key.ToString());
        }
    };
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPaymentEndpoints();
app.MapAccountEndpoints();
app.MapAdminEndpoints();

app.MapPrometheusScrapingEndpoint("/metrics");

// Liveness answers "is this process running", so it checks nothing: a dependency outage
// must not cause the orchestrator to kill a healthy process that would recover on its own.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
});

// Readiness answers "should this instance receive traffic", so it checks the
// dependencies. Degraded still passes: a deep outbox or an unreachable broker does not
// stop payments from being accepted correctly.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
});

app.MapGet("/", () => Results.Ok(new
{
    service = "flowpay-api",
    status = "ok",
    endpoints = new[]
    {
        "POST /v1/accounts",
        "GET  /v1/accounts/{id}",
        "POST /v1/payments",
        "GET  /v1/payments/{id}",
        "GET  /v1/payments/{id}/ledger",
        "POST /v1/payments/{id}/capture",
        "POST /v1/payments/{id}/void",
        "POST /v1/payments/{id}/refunds",
        "GET  /v1/admin/invariants",
        "GET  /metrics",
    },
})).ExcludeFromDescription();

await app.RunAsync();

return 0;

/// <summary>Probes the local readiness endpoint for the container health check.</summary>
internal static class HealthProbe
{
    internal static async Task<int> RunAsync()
    {
        // Bound well inside the HEALTHCHECK timeout so the probe reports a failure rather
        // than being killed, which would look the same to Docker but tell an operator
        // nothing.
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };

        string port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";

        try
        {
            using HttpResponseMessage response =
                await client.GetAsync($"http://localhost:{port}/health/ready");

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception failure)
        {
            await Console.Error.WriteLineAsync($"Health probe failed: {failure.Message}");
            return 1;
        }
    }
}

/// <summary>
/// Named so the integration tests can reference the API's entry point assembly through
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
