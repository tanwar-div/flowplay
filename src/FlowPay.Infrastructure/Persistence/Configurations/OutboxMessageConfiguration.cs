using FlowPay.Application.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(m => m.AggregateId).HasColumnName("aggregate_id").IsRequired();

        builder.Property(m => m.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(m => m.Status).HasColumnName("status").IsRequired();
        builder.Property(m => m.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(m => m.AvailableAt).HasColumnName("available_at").IsRequired();
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(m => m.DispatchedAt).HasColumnName("dispatched_at");
        builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(1024);
        builder.Property(m => m.TraceId).HasColumnName("trace_id").HasMaxLength(64);

        // The dispatcher's only query is "pending rows that are due, oldest first". A
        // partial index over exactly that predicate stays small no matter how large the
        // dispatched history grows, so polling cost is proportional to the backlog rather
        // than to the table.
        builder.HasIndex(m => new { m.Status, m.AvailableAt })
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("status = 0");
    }
}
