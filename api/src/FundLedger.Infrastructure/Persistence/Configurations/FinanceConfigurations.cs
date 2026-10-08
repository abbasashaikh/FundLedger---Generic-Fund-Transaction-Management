using FundLedger.Domain.Accounts;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundLedger.Infrastructure.Persistence.Configurations;

internal sealed class FundTypeConfiguration : IEntityTypeConfiguration<FundType>
{
    public void Configure(EntityTypeBuilder<FundType> b)
    {
        b.ToTable("fund_types");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(60);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
    }
}

internal sealed class PaymentModeConfiguration : IEntityTypeConfiguration<PaymentMode>
{
    public void Configure(EntityTypeBuilder<PaymentMode> b)
    {
        b.ToTable("payment_modes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(40);
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
    }
}

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(80);
        b.Property(x => x.BankName).HasMaxLength(80);
        b.Property(x => x.AccountNumberLast4).HasColumnType("char(4)");
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
    }
}

internal sealed class OpeningBalanceConfiguration : IEntityTypeConfiguration<OpeningBalance>
{
    public void Configure(EntityTypeBuilder<OpeningBalance> b)
    {
        b.ToTable("opening_balances");
        b.HasKey(x => new { x.FundId, x.AccountId });
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.SetAt).HasDefaultValueSql("now()");
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("categories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(60);
        b.Property(x => x.Icon).HasMaxLength(40);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        // The unique index uses an expression (lower(name), coalesce(fund_id)), so SQL owns it.
    }
}

internal sealed class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.ToTable("transactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TxnNumber).HasMaxLength(30);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.TxnTime).HasColumnType("time(0)");
        b.Property(x => x.ReceivedFrom).HasMaxLength(120);
        b.Property(x => x.PaidTo).HasMaxLength(120);
        b.Property(x => x.Purpose).HasMaxLength(300);
        b.Property(x => x.ReferenceNumber).HasMaxLength(80);
        b.Property(x => x.Source).HasMaxLength(10);
        b.Property(x => x.CancellationReason).HasMaxLength(300);
        b.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        b.Property(x => x.Revision).HasDefaultValue(1);
        b.HasIndex(x => new { x.OrganizationId, x.TxnNumber }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.ClientTxnId }).IsUnique();
    }
}
