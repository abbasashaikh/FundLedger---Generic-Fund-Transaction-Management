using System.Security.Claims;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Auth;
using FundLedger.Infrastructure.Security;

namespace FundLedger.Api.Middleware;

/// <summary>
/// Runs after JWT authentication. A valid signature is not enough: the session chain
/// must not have been ended (logout, PIN reset, deactivation, theft) and the user must
/// still be active. On success it fills <see cref="ICurrentUser"/> and the tenant
/// context with the role read from the DATABASE (not the token), so role changes and
/// deactivation take effect on the very next request (TRD TR-006, TR-016).
///
/// On failure the request continues as anonymous: protected endpoints answer 401,
/// while login/refresh/logout keep working.
/// </summary>
internal sealed class SessionValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AuthService auth, CurrentUser currentUser, TenantContext tenant)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated == true
            && Guid.TryParse(principal.FindFirstValue(FundLedgerClaims.Subject), out var userId)
            && Guid.TryParse(principal.FindFirstValue(FundLedgerClaims.Organization), out var orgId)
            && Guid.TryParse(principal.FindFirstValue(FundLedgerClaims.Session), out var sessionId)
            && Guid.TryParse(principal.FindFirstValue(FundLedgerClaims.Family), out var familyId))
        {
            var state = await auth.ValidateAccessAsync(orgId, userId, familyId, context.RequestAborted).ConfigureAwait(false);
            if (state is { } s)
            {
                currentUser.Set(userId, orgId, sessionId, familyId, s.Role, s.PinMustChange);
                tenant.Set(orgId, userId, isAdmin: s.Role == Domain.Users.UserRole.Admin);
            }
            else
            {
                tenant.Clear();
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
            }
        }
        else if (principal.Identity?.IsAuthenticated == true)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity()); // malformed claims
        }

        await next(context).ConfigureAwait(false);
    }
}
