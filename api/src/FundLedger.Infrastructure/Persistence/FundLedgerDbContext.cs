using FundLedger.Application.Abstractions;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Organizations;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// EF Core context over schema <c>fl</c>. The schema itself (tables, triggers,
/// RLS policies, views, grants) is created by hand-written SQL migrations — see
/// <c>Migrations/</c> and <c>database/schema.sql</c>. Entities are mapped onto it.
/// </summary>
public sealed class FundLedgerDbContext(DbContextOptions<FundLedgerDbContext> options) : DbContext(options), IFundLedgerDb
{
    public const string Schema = "fl";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<Fund> Funds => Set<Fund>();

    public DbSet<UserFundAccess> UserFundAccess => Set<UserFundAccess>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FundLedgerDbContext).Assembly);
    }
}
