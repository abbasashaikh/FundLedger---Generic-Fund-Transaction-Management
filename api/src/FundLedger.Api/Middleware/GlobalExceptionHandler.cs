using System.Globalization;
using FundLedger.Application.Errors;
using FundLedger.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Api.Middleware;

/// <summary>
/// Maps exceptions to safe ProblemDetails (TRD §11.1, Standard §3.7): stable
/// <c>code</c>, user-safe title, request id — never stack traces, SQL or paths.
/// Business rules raised by database triggers (SQLSTATE P0001, message = code)
/// are mapped to the same codes the API layer uses.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Dictionary<string, (int Status, string Title)> DatabaseRules = new(StringComparer.Ordinal)
    {
        ["ACTIVE_USER_LIMIT_REACHED"] = (409, "You've reached the limit of active users. Deactivate a user to add another."),
        ["FUND_NOT_ACTIVE"] = (409, "This fund is not active. New entries can't be added."),
        ["TXN_CANCELLED_IMMUTABLE"] = (409, "A cancelled transaction can't be changed."),
        ["TXN_IMMUTABLE_FIELD"] = (400, "This field can't be changed after the transaction is created."),
        ["AUDIT_LOG_APPEND_ONLY"] = (409, "Audit records can't be changed."),
    };

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is DbUpdateException { InnerException: PostgresException inner })
        {
            exception = inner;   // surface DB trigger rules (SQLSTATE P0001) raised during SaveChanges
        }

        var (status, code, title) = exception switch
        {
            AppException a => (a.Status, a.Code, a.Message),
            DbUpdateConcurrencyException => (412, "REVISION_CONFLICT", "This record was changed by someone else. Reload to see the latest version."),
            DomainException d => (400, d.Code, d.Message),
            PostgresException { SqlState: "P0001" } pg when DatabaseRules.TryGetValue(pg.MessageText, out var rule)
                => (rule.Status, pg.MessageText, rule.Title),
            BadHttpRequestException => (400, "VALIDATION_FAILED", "The request is not valid."),
            _ => (500, "INTERNAL_ERROR", "Something went wrong. Please try again."),
        };

        if (status >= 500)
        {
            LogUnhandled(logger, exception, httpContext.TraceIdentifier);
        }

        httpContext.Response.StatusCode = status;
        if (exception is TooManyAttemptsException locked)
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(locked.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        if (exception is UnauthenticatedException)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Bearer";
        }

        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        };
        if (exception is ValidationFailedException v)
        {
            context.ProblemDetails.Extensions["errors"] = v.Errors;
        }

        return await problemDetails.TryWriteAsync(context).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for request {RequestId}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string requestId);
}
