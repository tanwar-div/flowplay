using FlowPay.Infrastructure;
using FlowPay.Worker.Consumers;
using FlowPay.Worker.Hosting;
using Serilog;
using Serilog.Events;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service.name", "flowpay-worker")
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"));

builder.Services.AddFlowPayInfrastructure(builder.Configuration);

// The worker owns the asynchronous half of the system. Separating it from the API means
// a backlog being drained at full speed cannot compete with request handling for
// connections or CPU, and the two can be scaled independently — more API replicas for
// request volume, more workers for event throughput.
builder.Services.AddFlowPayOutboxDispatcher();
builder.Services.AddFlowPayMaintenance();

builder.Services.AddHostedService<SettlementProjectionConsumer>();
builder.Services.AddHostedService<PaymentAuditConsumer>();
builder.Services.AddHostedService<InboxPurgeService>();

// Schema migration runs before anything else starts, so a worker brought up against a
// fresh database does not race the API for it: whichever process gets there first takes
// the advisory lock and the other waits.
builder.Services.AddHostedService<SchemaMigrationStartupService>();

IHost host = builder.Build();

await host.RunAsync();
