using Microsoft.EntityFrameworkCore.Design;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without starting the API.
/// No database connection is opened when generating migrations.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FundLedgerDbContext>
{
    public FundLedgerDbContext CreateDbContext(string[] args) =>
        DependencyInjection.CreateMigrationContext(
            Environment.GetEnvironmentVariable("FUNDLEDGER_MIGRATIONS_DB")
            ?? "Host=localhost;Database=fundledger");   // never opened at design time
}
