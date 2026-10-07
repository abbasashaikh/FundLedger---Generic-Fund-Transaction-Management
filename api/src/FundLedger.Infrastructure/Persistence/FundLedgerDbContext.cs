using FundLedger.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// EF Core context over schema <c>fl</c>. The schema itself (tables, triggers,
/// RLS policies, views, grants) is created by hand-written SQL migrations — see
/// <c>Migrations/</c> and <c>database/schema.sql</c>. Entities are mapped onto it.
/// </summary>
public sealed class FundLedgerDbContext(DbContextOptions<FundLedgerDbContext> options) : DbContext(options)
{
    public const string Schema = "fl";

    public DbSet<Organization> Organizations => Set<Organization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FundLedgerDbContext).Assembly);
    }
}
