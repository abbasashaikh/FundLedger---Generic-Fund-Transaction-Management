using FluentValidation;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Audit;
using FundLedger.Application.Auth;
using FundLedger.Application.Bootstrap;
using FundLedger.Application.Funds;
using FundLedger.Application.Ledger;
using FundLedger.Application.Lookups;
using FundLedger.Application.Profile;
using FundLedger.Application.Reports;
using FundLedger.Application.Settings;
using FundLedger.Application.Sync;
using FundLedger.Application.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FundLedger.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddFundLedgerApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<AuthService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<FundAccessGuard>();
        services.AddScoped<FundService>();
        services.AddScoped<LookupService>();
        services.AddScoped<LedgerService>();
        services.AddScoped<AuditService>();
        services.AddScoped<MeService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<ExportService>();
        services.AddScoped<SettingsService>();
        services.AddScoped<SyncService>();
        services.AddSingleton<ReceiptRateLimiter>();
        services.AddScoped<BootstrapService>();
        services.AddValidatorsFromAssemblyContaining<CreateUserRequestValidator>(ServiceLifetime.Singleton);
        return services;
    }
}
