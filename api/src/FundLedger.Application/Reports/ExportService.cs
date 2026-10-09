using System.Text.Json;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Funds;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Reports;

public sealed record CreateExportRequest(
    string ReportCode, string Format, Guid FundId, DateOnly? From, DateOnly? To, TxnType? Type = null, Guid? CategoryId = null,
    Guid? AccountId = null, Guid? UserId = null, Guid? PaymentModeId = null);

public sealed record ExportJobDto(
    Guid Id, string ReportCode, string Format, string Status, int? RowCount, string? ErrorCode, DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt, DateTimeOffset? ExpiresAt, string? FileName, Guid FundId);

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>Work handed to the background runner. It carries the requester's identity so the job runs with exactly their rights.</summary>
public sealed record ExportWork(Guid JobId, Guid OrganizationId, Guid UserId, bool IsAdmin);

public interface IExportDispatcher
{
    void Enqueue(ExportWork work);
}

/// <summary>
/// Asynchronous exports (TRD §10.2): <c>POST /exports</c> records a job and returns at once; a background runner builds
/// the file; the client polls and downloads it. Needs both <c>can_view_reports</c> and <c>can_export</c> on the fund, so an
/// unauthorized export is a 403 (PRD §29). A job belongs to its requester: nobody else can list or download it.
/// </summary>
public sealed class ExportService(ICurrentUser caller, FundAccessGuard guard, IExportDispatcher queue, IExportStore store, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>A job that has not finished by then is reported as failed (the process may have restarted mid-job).</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool TryParseFormat(string? value, out ExportFormat format) =>
        Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);

    public async Task<(ExportJobDto Job, bool Existing)> CreateAsync(CreateExportRequest r, Guid? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (idempotencyKey is not { } key || key == Guid.Empty)
        {
            throw ValidationFailedException.For("Idempotency-Key", "Send an Idempotency-Key header (a new GUID for each export).");
        }

        if (!ReportCodes.TryParse(r.ReportCode, out var code))
        {
            throw ValidationFailedException.For("reportCode", "Unknown report.");
        }

        if (!TryParseFormat(r.Format, out var format))
        {
            throw ValidationFailedException.For("format", "Choose CSV, XLSX or PDF.");
        }

        await guard.RequireAsync(r.FundId, FundCapability.ViewReports, ct).ConfigureAwait(false);
        await guard.RequireAsync(r.FundId, FundCapability.Export, ct).ConfigureAwait(false);
        if (r.From is { } f && r.To is { } t && f > t)
        {
            throw ValidationFailedException.For("from", "The start date must be on or before the end date.");
        }

        var existing = await store.FindByKeyAsync(caller.UserId, key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return (existing, true);
        }

        var id = Guid.CreateVersion7();
        var parameters = JsonSerializer.Serialize(new
        {
            fundId = r.FundId, from = r.From, to = r.To, type = r.Type?.ToString(), categoryId = r.CategoryId, accountId = r.AccountId,
            userId = r.UserId, paymentModeId = r.PaymentModeId,
        }, Json);
        await store.InsertAsync(id, caller.OrganizationId, caller.UserId, ReportCodes.Wire(code), format.ToString().ToUpperInvariant(), parameters, key, clock.GetUtcNow(), ct)
            .ConfigureAwait(false);

        // The API's transaction filter commits the row when this request ends; the runner re-reads it (retrying briefly) before starting.
        queue.Enqueue(new ExportWork(id, caller.OrganizationId, caller.UserId, caller.IsAdmin));
        return ((await store.ListAsync(caller.UserId, id, ct).ConfigureAwait(false)).Single(), false);
    }

    public async Task<IReadOnlyList<ExportJobDto>> ListAsync(CancellationToken ct)
    {
        await store.ExpireAsync(caller.UserId, clock.GetUtcNow(), StuckAfter, ct).ConfigureAwait(false);
        return await store.ListAsync(caller.UserId, null, ct).ConfigureAwait(false);
    }

    public async Task<ExportJobDto> GetAsync(Guid id, CancellationToken ct)
    {
        await store.ExpireAsync(caller.UserId, clock.GetUtcNow(), StuckAfter, ct).ConfigureAwait(false);
        return (await store.ListAsync(caller.UserId, id, ct).ConfigureAwait(false)).SingleOrDefault() ?? throw new NotFoundException();
    }

    public async Task<ExportFile> DownloadAsync(Guid id, CancellationToken ct)
    {
        var job = await GetAsync(id, ct).ConfigureAwait(false);
        if (job.Status != "SUCCEEDED")
        {
            throw job.Status == "EXPIRED"
                ? new ConflictException("EXPORT_EXPIRED", "This file has expired. Run the export again.")
                : new ConflictException("EXPORT_NOT_READY", "The file isn't ready yet.");
        }

        var content = await store.ReadContentAsync(caller.UserId, id, ct).ConfigureAwait(false)
            ?? throw new ConflictException("EXPORT_EXPIRED", "This file has expired. Run the export again.");
        var contentType = job.Format switch
        {
            "CSV" => "text/csv; charset=utf-8",
            "XLSX" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/pdf",
        };
        return new ExportFile(job.FileName ?? $"export.{job.Format.ToLowerInvariant()}", contentType, content);
    }
}

/// <summary>Persistence of export jobs (<c>fl.export_jobs</c>). Rows are tenant-scoped by RLS; every method also filters by requester.</summary>
public interface IExportStore
{
    Task InsertAsync(Guid id, Guid organizationId, Guid requestedBy, string reportCode, string format, string parametersJson, Guid idempotencyKey, DateTimeOffset now, CancellationToken ct);

    Task<ExportJobDto?> FindByKeyAsync(Guid requestedBy, Guid idempotencyKey, CancellationToken ct);

    /// <summary>The requester's newest jobs, or just one.</summary>
    Task<IReadOnlyList<ExportJobDto>> ListAsync(Guid requestedBy, Guid? id, CancellationToken ct);

    Task<byte[]?> ReadContentAsync(Guid requestedBy, Guid id, CancellationToken ct);

    /// <summary>Files past their life become EXPIRED (and are emptied); jobs that never finished become FAILED.</summary>
    Task ExpireAsync(Guid requestedBy, DateTimeOffset now, TimeSpan stuckAfter, CancellationToken ct);

    // ---- used by the runner (inside the requester's own tenant context) ----
    Task<ExportJobParameters?> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct);

    Task CompleteAsync(Guid id, string fileName, byte[] content, int rows, DateTimeOffset now, CancellationToken ct);

    Task FailAsync(Guid id, string errorCode, DateTimeOffset now, CancellationToken ct);
}

public sealed record ExportJobParameters(string ReportCode, ExportFormat Format, ReportQuery Query);
