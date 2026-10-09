using FundLedger.Api.Hosting;
using FundLedger.Application.Audit;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FundLedger.Api.Endpoints;

/// <summary>The audit log (PRD §14): who did what. Admin only; also enforced by row-level security.</summary>
internal static class AuditEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1/audit-logs").WithTags("Audit").RequireAuthorization(Policies.Admin).AddEndpointFilter<TenantTransactionFilter>();

        g.MapGet("/", async Task<Ok<AuditPage>> (
                DateOnly? from, DateOnly? to, Guid? userId, string? group, string? action, Guid? fundId, string? q, int? limit, string? cursor,
                AuditService s, HttpContext h) =>
                TypedResults.Ok(await s.QueryAsync(new AuditQuery(from, to, userId, group, action, fundId, q, limit, cursor), h.RequestAborted).ConfigureAwait(false)))
            .WithName("ListAuditLogs").WithSummary("Search the audit log (newest first).").ProducesProblem(400).ProducesProblem(403);

        g.MapGet("/export", async Task<FileContentHttpResult> (
                DateOnly? from, DateOnly? to, Guid? userId, string? group, string? action, Guid? fundId, string? q, AuditService s, HttpContext h) =>
            {
                var (name, bytes, _) = await s.ExportCsvAsync(new AuditQuery(from, to, userId, group, action, fundId, q, null, null), h.RequestAborted).ConfigureAwait(false);
                return TypedResults.File(bytes, "text/csv; charset=utf-8", name);
            })
            .WithName("ExportAuditLogs").WithSummary("CSV of the filtered log (max 5,000 rows). The export is itself audited.").ProducesProblem(403);
    }
}
