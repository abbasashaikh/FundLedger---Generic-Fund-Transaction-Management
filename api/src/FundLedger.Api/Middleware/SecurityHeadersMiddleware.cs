namespace FundLedger.Api.Middleware;

/// <summary>
/// Security headers for API responses (TRD TR-072). The API serves JSON/PDF only,
/// so its CSP forbids everything; the PWA's own CSP is set by its static host.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Resource-Policy"] = "same-site";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers.CacheControl = context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
                ? "no-store"                                  // financial data is never cached
                : headers.CacheControl;
            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            return Task.CompletedTask;
        });
        return next(context);
    }
}
