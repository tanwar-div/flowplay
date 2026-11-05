using FlowPay.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        builder.HasKey(m => new { m.MessageId, m.Consumer });

        builder.Property(m => m.MessageId).HasColumnName("message_id").IsRequired();

        builder.Property(m => m.Consumer)
            .HasColumnName("consumer")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(m => m.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(m => m.ProcessedAt).HasColumnName("processed_at").IsRequired();
        builder.Property(m => m.DeliveryAttempt).HasColumnName("delivery_attempt").IsRequired();

        builder.HasIndex(m => m.ProcessedAt).HasDatabaseName("ix_inbox_processed_at");
    }
}
