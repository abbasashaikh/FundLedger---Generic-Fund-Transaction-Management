using FundLedger.Application.Abstractions;
using FundLedger.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FundLedger.Api.Hosting;

/// <summary>Authorization policy names used by endpoint groups.</summary>
internal static class Policies
{
    /// <summary>Signed in, session valid, own PIN chosen.</summary>
    public const string User = "user";

    /// <summary><see cref="User"/> + Admin role (checked against the database each request).</summary>
    public const string Admin = "admin";

    /// <summary>Signed in, even if the temporary PIN still has to be changed (change-PIN, /me, logout).</summary>
    public const string PinChangePending = "pin-change-pending";
}

internal static class AuthSetup
{
    public static IServiceCollection AddFundLedgerAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.PinChangePending, p => p.RequireAuthenticatedUser().RequireAssertion(ctx => Caller(ctx).IsAuthenticated))
            .AddPolicy(Policies.User, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                Caller(ctx) is { IsAuthenticated: true, PinMustChange: false }))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
                Caller(ctx) is { IsAuthenticated: true, PinMustChange: false, IsAdmin: true }));

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        return services;
    }

    private static ICurrentUser Caller(AuthorizationHandlerContext ctx) =>
        ((HttpContext)ctx.Resource!).RequestServices.GetRequiredService<ICurrentUser>();

    private sealed class ConfigureJwtBearer(JwtKeyRing keys) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(string? name, JwtBearerOptions options) => Configure(options);

        public void Configure(JwtBearerOptions options)
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = keys.Options.Issuer,
                ValidAudience = keys.Options.Audience,
                IssuerSigningKeys = keys.ValidationKeys,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = FundLedgerClaims.Subject,
                RoleClaimType = FundLedgerClaims.Role,
            };
        }
    }

    /// <summary>
    /// Authorization failures as ProblemDetails with stable codes: 401 UNAUTHENTICATED,
    /// 403 PIN_CHANGE_REQUIRED (temporary PIN still in use) or 403 FORBIDDEN.
    /// </summary>
    private sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Succeeded)
            {
                await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
                return;
            }

            var caller = context.RequestServices.GetRequiredService<ICurrentUser>();
            var (status, code, title) = !caller.IsAuthenticated
                ? (401, "UNAUTHENTICATED", "Please sign in.")
                : caller.PinMustChange
                    ? (403, "PIN_CHANGE_REQUIRED", "Choose your own PIN to continue.")
                    : (403, "FORBIDDEN", "You don't have permission to do this.");

            context.Response.StatusCode = status;
            if (status == 401)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
            }

            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
            }).ConfigureAwait(false);
        }
    }
}
