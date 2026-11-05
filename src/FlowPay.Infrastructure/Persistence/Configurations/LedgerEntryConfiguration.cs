using FlowPay.Domain.Ledger;
using FlowPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasConversion(FlowPayConverters.LedgerEntryId)
            .ValueGeneratedNever();

        builder.Property(e => e.PaymentId)
            .HasColumnName("payment_id")
            .HasConversion(FlowPayConverters.PaymentId)
            .IsRequired();

        builder.Property(e => e.RefundId)
            .HasColumnName("refund_id")
            .HasConversion(FlowPayConverters.NullableRefundId);

        builder.Property(e => e.AccountId)
            .HasColumnName("account_id")
            .HasConversion(FlowPayConverters.AccountId)
            .IsRequired();

        builder.Property(e => e.Direction)
            .HasColumnName("direction")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(e => e.EntryType)
            .HasColumnName("entry_type")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(e => e.AmountMinorUnits).HasColumnName("amount_minor_units").IsRequired();

        builder.Property(e => e.Currency)
            .HasColumnName("currency")
            .HasConversion(FlowPayConverters.Currency)
            .HasColumnType("char(3)")
            .IsRequired();

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // The computed signed amount is ignored by EF: it is a generated column in
        // PostgreSQL so the invariant queries can sum it directly in the database rather
        // than materializing the entire ledger to add it up in process.
        builder.Ignore(e => e.SignedMinorUnits);
        builder.Ignore(e => e.Amount);

        // The foreign keys are declared with no navigation property on either side.
        // The aggregates deliberately do not reference each other — a payment does not
        // own a collection of ledger lines — but EF still needs to know the dependency
        // order, or it will try to insert a ledger line before the payment it belongs to
        // and the database's own foreign key will reject it.
        builder.HasOne<PaymentIntent>()
            .WithMany()
            .HasForeignKey(e => e.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(e => e.AccountId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Refund>()
            .WithMany()
            .HasForeignKey(e => e.RefundId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.PaymentId).HasDatabaseName("ix_ledger_entries_payment");
        builder.HasIndex(e => e.AccountId).HasDatabaseName("ix_ledger_entries_account");
    }
}
