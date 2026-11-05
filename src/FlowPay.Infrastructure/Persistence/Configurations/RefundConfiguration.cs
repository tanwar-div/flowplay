using FlowPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> builder)
    {
        builder.ToTable("refunds");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasConversion(FlowPayConverters.RefundId)
            .ValueGeneratedNever();

        builder.Property(r => r.PaymentId)
            .HasColumnName("payment_id")
            .HasConversion(FlowPayConverters.PaymentId)
            .IsRequired();

        builder.Property(r => r.AmountMinorUnits).HasColumnName("amount_minor_units").IsRequired();

        builder.Property(r => r.Currency)
            .HasColumnName("currency")
            .HasConversion(FlowPayConverters.Currency)
            .HasColumnType("char(3)")
            .IsRequired();

        builder.Property(r => r.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(r => r.Reason).HasColumnName("reason").HasMaxLength(256).IsRequired();

        builder.Property(r => r.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.ResultingPaymentStatus)
            .HasColumnName("resulting_payment_status")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

        // Declared so EF inserts the payment's update before the refund row that
        // references it.
        builder.HasOne<PaymentIntent>()
            .WithMany()
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.PaymentId).HasDatabaseName("ix_refunds_payment");

        // A second line of defence behind the idempotency table: even a code path that
        // skipped the idempotency layer entirely could not write two refunds under one
        // key.
        builder.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ux_refunds_idempotency_key");
    }
}
