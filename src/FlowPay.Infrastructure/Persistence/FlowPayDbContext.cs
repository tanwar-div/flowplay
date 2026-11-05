using FlowPay.Application.Idempotency;
using FlowPay.Application.Outbox;
using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using FlowPay.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FlowPay.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context over FlowPay's schema.
/// </summary>
/// <remarks>
/// <para>
/// One context rather than one per bounded concern, because the system's central
/// guarantee depends on it: the idempotency claim, the balance updates, the ledger lines
/// and the outbox rows all have to commit or roll back together, and that requires them
/// to share a transaction, which requires them to share a connection and a context.
/// </para>
/// <para>
/// Query tracking is disabled by default — configured at registration rather than in
/// <c>OnConfiguring</c>, which pooling forbids. Read endpoints greatly outnumber writes,
/// and tracking them would make every lookup pay for change detection and keep
/// materialized graphs alive for the life of the request. The write paths opt back in
/// explicitly, through the repositories' <c>FOR UPDATE</c> queries.
/// </para>
/// </remarks>
public sealed class FlowPayDbContext : DbContext
{
    public FlowPayDbContext(DbContextOptions<FlowPayDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<PaymentIntent> Payments => Set<PaymentIntent>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(FlowPayDbContext).Assembly);
}
