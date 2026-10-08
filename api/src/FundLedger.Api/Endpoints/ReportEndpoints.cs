using FundLedger.Api.Hosting;
using FundLedger.Application.Errors;
using FundLedger.Application.Reports;
using FundLedger.Application.Settings;
using FundLedger.Domain.Ledger;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FundLedger.Api.Endpoints;

public sealed record ReportCatalogItem(string Code, string Title, string Description);

/// <summary>Reports, exports, Money In receipts and settings (Phase 4).</summary>
internal static class ReportEndpoints
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/v1").WithTags("Reports").RequireAuthorization().AddEndpointFilter<TenantTransactionFilter>();

        g.MapGet("/reports", () => TypedResults.Ok(ReportCodes.All.Select(c =>
            {
                var (title, description) = ReportCodes.Describe(c);
                return new ReportCatalogItem(ReportCodes.Wire(c), title, description);
            }).ToList()))
            .WithName("ListReports").WithSummary("The ten reports (TRD §10.1). Who may open them is decided per fund (can_view_reports).");

        g.MapGet("/reports/{code}", async Task<Ok<ReportResult>> (
                string code, Guid fundId, DateOnly? from, DateOnly? to, EnumQuery<TxnType>? type, Guid? categoryId, Guid? accountId, Guid? userId,
                Guid? paymentModeId, ReportService s, HttpContext h) =>
            {
                if (!ReportCodes.TryParse(code, out var c))
                {
                    throw new NotFoundException();
                }

                return TypedResults.Ok(await s.RunAsync(c, new ReportQuery(fundId, from, to, type?.Value, categoryId, accountId, userId, paymentModeId), h.RequestAborted)
                    .ConfigureAwait(false));
            })
            .WithName("RunReport").WithSummary("Run one report for a fund and period. Members without 'see all' get their own entries only.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);

        g.MapPost("/exports", async Task<Results<Accepted<ExportJobDto>, Ok<ExportJobDto>>> (
                CreateExportRequest b, [FromHeader(Name = "Idempotency-Key")] Guid? key, ExportService s, HttpContext h) =>
            {
                var (job, existing) = await s.CreateAsync(b, key, h.RequestAborted).ConfigureAwait(false);
                return existing ? TypedResults.Ok(job) : TypedResults.Accepted($"/api/v1/exports/{job.Id}", job);
            })
            .WithName("CreateExport").WithSummary("Start an export (CSV, XLSX or PDF). Needs an Idempotency-Key header; a repeat returns the same job.")
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(404);

        g.MapGet("/exports", async Task<Ok<IReadOnlyList<ExportJobDto>>> (ExportService s, HttpContext h) =>
                TypedResults.Ok(await s.ListAsync(h.RequestAborted).ConfigureAwait(false)))
            .WithName("ListExports").WithSummary("The caller's own recent exports.");

        g.MapGet("/exports/{id:guid}", async Task<Ok<ExportJobDto>> (Guid id, ExportService s, HttpContext h) =>
                TypedResults.Ok(await s.GetAsync(id, h.RequestAborted).ConfigureAwait(false)))
            .WithName("GetExport").WithSummary("Status of one of the caller's exports.").ProducesProblem(404);

        g.MapGet("/exports/{id:guid}/file", async Task<FileContentHttpResult> (Guid id, ExportService s, HttpContext h) =>
            {
                var f = await s.DownloadAsync(id, h.RequestAborted).ConfigureAwait(false);
                h.Response.Headers.CacheControl = "private, no-store";
                return TypedResults.File(f.Content, f.ContentType, f.FileName);
            })
            .WithName("DownloadExport").WithSummary("The finished file (available for 24 hours).").ProducesProblem(404).ProducesProblem(409);

        g.MapGet("/transactions/{id:guid}/receipt.pdf", async Task<FileContentHttpResult> (Guid id, ReceiptService s, HttpContext h) =>
            {
                var f = await s.GenerateAsync(id, h.RequestAborted).ConfigureAwait(false);
                h.Response.Headers.CacheControl = "private, no-store";
                return TypedResults.File(f.Content, "application/pdf", f.FileName);
            })
            .WithName("GetReceipt").WithSummary("Money In receipt (PDF). Money In entries only; same access as the entry itself.")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(429);

        var admin = app.MapGroup("/api/v1/settings").WithTags("Settings").RequireAuthorization(Policies.Admin).AddEndpointFilter<TenantTransactionFilter>();

        admin.MapGet("/", async Task<Ok<SettingsDto>> (SettingsService s, HttpContext h) =>
                TypedResults.Ok(await s.GetAsync(h.RequestAborted).ConfigureAwait(false)))
            .WithName("GetSettings").WithSummary("Organization profile and settings (Admin).");

        admin.MapPut("/", async Task<Ok<SettingsDto>> (SettingsDto b, SettingsService s, HttpContext h) =>
                TypedResults.Ok(await s.SaveAsync(b, h.RequestAborted).ConfigureAwait(false)))
            .Validate<SettingsDto>().WithName("SaveSettings").WithSummary("Save the organization profile and settings. Changes are audited.")
            .ProducesProblem(400).ProducesProblem(403);
    }
}
