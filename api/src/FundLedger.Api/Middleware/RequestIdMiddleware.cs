using System.Text.RegularExpressions;
using Serilog.Context;

namespace FundLedger.Api.Middleware;

/// <summary>
/// Correlation id for every request (TRD TR-095, Standard §8.3). Accepts a
/// well-formed incoming <c>X-Request-Id</c> (the PWA sends one), otherwise
/// generates one; echoes it on the response and pushes it into every log line.
/// </summary>
internal sealed partial class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var requestId = IsSafe(incoming) ? incoming : Guid.CreateVersion7().ToString("N");

        context.TraceIdentifier = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("RequestId", requestId))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    // Prevents log injection: only short ids made of safe characters are accepted.
    private static bool IsSafe(string value) => value.Length is > 0 and <= 64 && SafeId().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeId();
}
