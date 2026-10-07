using FundLedger.Application.Abstractions;
using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FundLedger.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence for the runtime role (<c>fundledger_app</c>, RLS enforced).
    /// Connection string: <c>ConnectionStrings:FundLedger</c> (env <c>ConnectionStrings__FundLedger</c>).
    /// </summary>
    public static IServiceCollection AddFundLedgerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantTransactionInterceptor>();

        services.AddDbContext<FundLedgerDbContext>((sp, options) =>
        {
            var raw = configuration.GetConnectionString("FundLedger")
                ?? throw new InvalidOperationException("ConnectionStrings:FundLedger is not configured.");
            options.UseNpgsql(ConnectionStrings.Normalize(raw), npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", FundLedgerDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<TenantTransactionInterceptor>());
        });

        return services;
    }

    /// <summary>
    /// A context for applying migrations as the schema owner (<c>fundledger_owner</c>).
    /// Used only by the <c>migrate</c> command and CI — never by request handling.
    /// </summary>
    public static FundLedgerDbContext CreateMigrationContext(string ownerConnectionString) =>
        new(new DbContextOptionsBuilder<FundLedgerDbContext>()
            .UseNpgsql(ConnectionStrings.Normalize(ownerConnectionString), npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", FundLedgerDbContext.Schema)
                      .CommandTimeout(300))
            .UseSnakeCaseNamingConvention()
            .Options);
}
