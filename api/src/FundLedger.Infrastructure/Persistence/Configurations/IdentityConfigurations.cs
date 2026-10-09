using FundLedger.Domain.Funds;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FundLedger.Infrastructure.Persistence.Configurations;

// Mappings onto tables created by the SQL baseline migration. Column names come from
// the snake_case convention; types/lengths mirror database/schema.sql.

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.FullName).HasMaxLength(120);
        builder.Property(u => u.MobileE164).HasMaxLength(15);
        builder.HasIndex(u => new { u.OrganizationId, u.MobileE164 }).IsUnique();
        builder.Property(u => u.Email).HasColumnType("citext");
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.Version).IsRowVersion();                 // xmin
        builder.Ignore(u => u.IsAdmin);
        builder.Ignore(u => u.IsActive);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => s.RefreshTokenHash).IsUnique();
        builder.HasIndex(s => s.FamilyId);
        builder.Property(s => s.DeviceLabel).HasMaxLength(120);
        builder.Property(s => s.RevokeReason).HasMaxLength(40);
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        builder.Ignore(s => s.IsRevoked);
    }
}

internal sealed class FundConfiguration : IEntityTypeConfiguration<Fund>
{
    public void Configure(EntityTypeBuilder<Fund> builder)
    {
        builder.ToTable("funds");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Code).HasMaxLength(10);
        builder.Property(f => f.Name).HasMaxLength(120);
        builder.HasIndex(f => new { f.OrganizationId, f.Code }).IsUnique();
        builder.HasIndex(f => new { f.OrganizationId, f.Name }).IsUnique();
        builder.Property(f => f.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(f => f.UpdatedAt).HasDefaultValueSql("now()");
    }
}

internal sealed class UserFundAccessConfiguration : IEntityTypeConfiguration<UserFundAccess>
{
    public void Configure(EntityTypeBuilder<UserFundAccess> builder)
    {
        builder.ToTable("user_fund_access");
        builder.HasKey(a => new { a.UserId, a.FundId });
        builder.HasIndex(a => a.FundId);
        builder.Property(a => a.GrantedAt).HasDefaultValueSql("now()");
    }
}
