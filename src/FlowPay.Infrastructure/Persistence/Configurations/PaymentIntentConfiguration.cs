using FlowPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class PaymentIntentConfiguration : IEntityTypeConfiguration<PaymentIntent>
{
    public void Configure(EntityTypeBuilder<PaymentIntent> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasConversion(FlowPayConverters.PaymentId)
            .ValueGeneratedNever();

        builder.Property(p => p.SourceAccountId)
            .HasColumnName("source_account_id")
            .HasConversion(FlowPayConverters.AccountId)
            .IsRequired();

        builder.Property(p => p.DestinationAccountId)
            .HasColumnName("destination_account_id")
            .HasConversion(FlowPayConverters.AccountId)
            .IsRequired();

        builder.Property(p => p.AmountMinorUnits).HasColumnName("amount_minor_units").IsRequired();
        builder.Property(p => p.FeeMinorUnits).HasColumnName("fee_minor_units").IsRequired();
        builder.Property(p => p.RefundedMinorUnits)
            .HasColumnName("refunded_minor_units")
            .IsRequired();

        builder.Property(p => p.Currency)
            .HasColumnName("currency")
            .HasConversion(FlowPayConverters.Currency)
            .HasColumnType("char(3)")
            .IsRequired();

        builder.Property(p => p.Status).HasColumnName("status").HasConversion<short>().IsRequired();
        builder.Property(p => p.Method).HasColumnName("method").HasConversion<short>().IsRequired();
        builder.Property(p => p.FailureReason)
            .HasColumnName("failure_reason")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(p => p.Reference).HasColumnName("reference").HasMaxLength(64).IsRequired();
        builder.Property(p => p.Description)
            .HasColumnName("description")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.AuthorizedAt).HasColumnName("authorized_at");
        builder.Property(p => p.CapturedAt).HasColumnName("captured_at");
        builder.Property(p => p.AuthorizationExpiresAt).HasColumnName("authorization_expires_at");

        builder.Property(p => p.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        // Both account foreign keys, so a payment is never inserted before an account
        // created in the same transaction.
        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(p => p.SourceAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(p => p.DestinationAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.SourceAccountId).HasDatabaseName("ix_payments_source_account");
        builder.HasIndex(p => p.DestinationAccountId)
            .HasDatabaseName("ix_payments_destination_account");
        builder.HasIndex(p => new { p.Status, p.AuthorizationExpiresAt })
            .HasDatabaseName("ix_payments_status_expiry");
    }
}
