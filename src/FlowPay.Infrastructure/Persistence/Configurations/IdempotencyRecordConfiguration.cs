using FlowPay.Application.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        // The composite key is the whole mechanism. The unique index behind it is what
        // makes "claim this key" an atomic operation across every API instance, with no
        // distributed lock and no coordination service.
        builder.HasKey(r => new { r.Endpoint, r.Key });

        builder.Property(r => r.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(r => r.Key)
            .HasColumnName("key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.RequestFingerprint)
            .HasColumnName("request_fingerprint")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").IsRequired();

        builder.Property(r => r.ResponseStatusCode).HasColumnName("response_status_code");

        // text rather than jsonb: jsonb would normalize whitespace and key order, and a
        // replay has to return the original response byte for byte.
        builder.Property(r => r.ResponseBody)
            .HasColumnName("response_body");

        builder.Property(r => r.ResourceId).HasColumnName("resource_id");

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.CompletedAt).HasColumnName("completed_at");
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(r => r.ExpiresAt).HasDatabaseName("ix_idempotency_expires_at");
    }
}
