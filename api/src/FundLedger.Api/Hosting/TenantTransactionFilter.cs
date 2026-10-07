using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Api.Hosting;

/// <summary>
/// Wraps an endpoint in one DB transaction so every query sees the caller's
/// tenant context (see <see cref="TenantTransactionInterceptor"/>). Commits on
/// success, rolls back on exceptions or error results. Applied to every
/// authenticated route group from Phase 1 onward:
/// <code>app.MapGroup("/api/v1/...").RequireAuthorization().AddEndpointFilter&lt;TenantTransactionFilter&gt;()</code>
/// </summary>
internal sealed class TenantTransactionFilter(FundLedgerDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var result = await next(context).ConfigureAwait(false);

        if (result is IStatusCodeHttpResult { StatusCode: >= 400 })
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        return result;
    }
}
