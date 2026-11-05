using FlowPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FlowPay.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasColumnName("id")
            .HasConversion(FlowPayConverters.AccountId)
            .ValueGeneratedNever();

        builder.Property(a => a.Currency)
            .HasColumnName("currency")
            .HasConversion(FlowPayConverters.Currency)
            .HasColumnType("char(3)")
            .IsRequired();

        builder.Property(a => a.BalanceMinorUnits)
            .HasColumnName("balance_minor_units")
            .IsRequired();

        builder.Property(a => a.OpeningBalanceMinorUnits)
            .HasColumnName("opening_balance_minor_units")
            .IsRequired();

        builder.Property(a => a.ReservedMinorUnits)
            .HasColumnName("reserved_minor_units")
            .IsRequired();

        builder.Property(a => a.Status)
            .HasColumnName("status")
            .HasConversion<short>()
            .IsRequired();

        builder.Property(a => a.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // PostgreSQL's xmin system column as the optimistic concurrency token. The
        // database maintains it, so there is no application counter to forget to bump,
        // and an UPDATE built on a stale read affects zero rows instead of silently
        // clobbering a concurrent change.
        builder.Property(a => a.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.HasIndex(a => a.Status).HasDatabaseName("ix_accounts_status");
    }
}
