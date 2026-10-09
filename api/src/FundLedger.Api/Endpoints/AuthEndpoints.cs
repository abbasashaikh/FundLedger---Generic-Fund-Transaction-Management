using FundLedger.Api.Hosting;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FundLedger.Api.Endpoints;

public sealed record LoginRequest(string Mobile, string Pin);

public sealed record ChangePinRequest(string CurrentPin, string NewPin);

/// <summary>Login/refresh response. The refresh token is only ever in the HttpOnly cookie.</summary>
public sealed record AuthResponse(string AccessToken, int ExpiresIn, SessionProfile User);

/// <summary>
/// <c>/api/v1/auth</c> — mobile + PIN (ADR-0002). These endpoints manage their own
/// transactions (failed attempts must be committed even when the response is an error).
/// Every call must send <c>X-FundLedger-Client: pwa</c>: a header a cross-site form
/// can't set, so login/refresh/logout can't be triggered by CSRF.
/// </summary>
internal static class AuthEndpoints
{
    public const string ClientHeader = "X-FundLedger-Client";
    public const string RefreshCookie = "fl_rt";
    public const string CookiePath = "/api/v1/auth";

    public static void Map(WebApplication app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth").AddEndpointFilter(RequireClientHeader);

        auth.MapPost("/login", async Task<Ok<AuthResponse>> (LoginRequest body, AuthService service, HttpContext http) =>
            {
                var outcome = await service.LoginAsync(body.Mobile, body.Pin, http.RequestAborted).ConfigureAwait(false);
                return TypedResults.Ok(Issue(http, outcome));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Login)
            .WithName("Login")
            .WithSummary("Sign in with mobile number and 6-digit PIN.")
            .ProducesProblem(401).ProducesProblem(429);

        auth.MapPost("/refresh", async Task<Ok<AuthResponse>> (AuthService service, HttpContext http) =>
            {
                var outcome = await service.RefreshAsync(http.Request.Cookies[RefreshCookie], http.RequestAborted).ConfigureAwait(false);
                return TypedResults.Ok(Issue(http, outcome));
            })
            .AllowAnonymous()
            .WithName("Refresh")
            .WithSummary("Exchange the refresh cookie for a new access token (the cookie rotates).")
            .ProducesProblem(401);

        auth.MapPost("/logout", async Task<NoContent> (AuthService service, ICurrentUser caller, HttpContext http) =>
            {
                await service.LogoutAsync(http.Request.Cookies[RefreshCookie], caller, http.RequestAborted).ConfigureAwait(false);
                http.Response.Cookies.Delete(RefreshCookie, CookieOptions(http, DateTimeOffset.UnixEpoch));
                return TypedResults.NoContent();
            })
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("End this session chain on the server and clear the cookie.");

        auth.MapPost("/pin/change", async Task<NoContent> (ChangePinRequest body, AuthService service, ICurrentUser caller, HttpContext http) =>
            {
                await service.ChangePinAsync(caller, body.CurrentPin, body.NewPin, http.RequestAborted).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .RequireAuthorization(Policies.PinChangePending)
            .WithName("ChangePin")
            .WithSummary("Replace your PIN (required after a temporary PIN). Other devices are signed out.")
            .ProducesProblem(400).ProducesProblem(429);
    }

    private static AuthResponse Issue(HttpContext http, AuthOutcome outcome)
    {
        http.Response.Cookies.Append(RefreshCookie, outcome.RefreshToken, CookieOptions(http, outcome.RefreshExpiresAt));
        return new AuthResponse(outcome.AccessToken, outcome.ExpiresInSeconds, outcome.User);
    }

    private static CookieOptions CookieOptions(HttpContext http, DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,                       // browsers treat http://localhost as secure
        SameSite = SameSiteMode.Strict,      // PWA (app.<domain>) and API (api.<domain>) are same-site
        Path = CookiePath,
        Expires = expires,
        IsEssential = true,
    };

    private static async ValueTask<object?> RequireClientHeader(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next) =>
        ctx.HttpContext.Request.Headers[ClientHeader] == "pwa"
            ? await next(ctx).ConfigureAwait(false)
            : TypedResults.Problem(statusCode: 400, title: "Missing client header.",
                extensions: new Dictionary<string, object?> { ["code"] = "CLIENT_HEADER_REQUIRED" });
}
