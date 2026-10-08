using FundLedger.Application.Abstractions;
using FundLedger.Application.Settings;
using FundLedger.Domain.Accounts;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using FundLedger.Domain.Users;
using FundLedger.Infrastructure.Persistence;
using FundLedger.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace FundLedger.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence for the runtime role (<c>fundledger_app</c>, RLS enforced) and
    /// security services. Connection string: <c>ConnectionStrings:FundLedger</c>.
    /// </summary>
    /// <param name="allowEphemeralSigningKey">
    /// Decides, when the key ring is first needed, whether a missing signing key may be replaced by
    /// a throwaway one (Development/Testing only). Evaluated lazily so the final environment is used.
    /// </param>
    public static IServiceCollection AddFundLedgerInfrastructure(
        this IServiceCollection services, IConfiguration configuration, Func<IServiceProvider, bool>? allowEphemeralSigningKey = null)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantTransactionInterceptor>();

        services.AddDbContext<FundLedgerDbContext>((sp, options) =>
        {
            var raw = configuration.GetConnectionString("FundLedger");
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("ConnectionStrings:FundLedger is not configured.");
            }

            options.UseNpgsql(ConnectionStrings.Normalize(raw), ConfigureNpgsql)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(sp.GetRequiredService<TenantTransactionInterceptor>());
        });
        services.AddScoped<IFundLedgerDb>(sp => sp.GetRequiredService<FundLedgerDbContext>());

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuthStore, AuthStore>();
        services.AddScoped<ISettingsProvider, SettingsProvider>();
        services.AddScoped<IBalanceReader, BalanceReader>();
        services.AddSingleton<IPinHasher, PinHasher>();

        services.AddSingleton(sp =>
        {
            var jwt = new JwtOptions();
            sp.GetRequiredService<IConfiguration>().GetSection(JwtOptions.Section).Bind(jwt);
            return new JwtKeyRing(jwt, allowEphemeralSigningKey?.Invoke(sp) ?? false);
        });
        services.AddSingleton<ITokenService, TokenService>();

        return services;
    }

    /// <summary>
    /// A context for applying migrations as the schema owner (<c>fundledger_owner</c>).
    /// Used only by the <c>migrate</c> command and CI — never by request handling.
    /// </summary>
    public static FundLedgerDbContext CreateMigrationContext(string ownerConnectionString) =>
        new(new DbContextOptionsBuilder<FundLedgerDbContext>()
            .UseNpgsql(ConnectionStrings.Normalize(ownerConnectionString), npgsql =>
            {
                ConfigureNpgsql(npgsql);
                npgsql.CommandTimeout(300);
            })
            .UseSnakeCaseNamingConvention()
            .Options);

    /// <summary>
    /// Shared Npgsql options. Runtime and migration contexts MUST configure the model
    /// identically, or EF reports pending model changes and refuses to migrate.
    /// </summary>
    internal static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql)
    {
        var labels = UpperSnakeNameTranslator.Instance;   // shared: a new instance per call would defeat EF's provider cache
        npgsql.MigrationsHistoryTable("__ef_migrations_history", FundLedgerDbContext.Schema);
        npgsql.MapEnum<UserRole>("user_role", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<UserStatus>("user_status", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<AuthMethod>("auth_method", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<FundStatus>("fund_status", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<AccountKind>("account_kind", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<CategoryDirection>("category_direction", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<TxnType>("txn_type", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<TxnStatus>("txn_status", FundLedgerDbContext.Schema, labels);
        npgsql.MapEnum<AdjustmentDirection>("adj_direction", FundLedgerDbContext.Schema, labels);
    }
}
