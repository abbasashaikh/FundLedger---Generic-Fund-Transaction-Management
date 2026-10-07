using FluentValidation;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Auth;
using FundLedger.Application.Bootstrap;
using FundLedger.Application.Funds;
using FundLedger.Application.Profile;
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
        services.AddScoped<MeService>();
        services.AddScoped<BootstrapService>();
        services.AddValidatorsFromAssemblyContaining<CreateUserRequestValidator>(ServiceLifetime.Singleton);
        return services;
    }
}
