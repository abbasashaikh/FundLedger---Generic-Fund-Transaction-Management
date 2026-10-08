using System.Text.Json;
using FundLedger.Application.Reports;
using FundLedger.Domain.Ledger;
using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Infrastructure.Exports;

/// <summary>
/// <c>fl.export_jobs</c> access. The finished file is kept in the row's <c>content</c> column until it expires (24 h),
/// then emptied, so the API and the runner share it without a bucket (see docs: exports deviation). All values are
/// SQL parameters.
/// </summary>
public sealed class ExportStore(FundLedgerDbContext db) : IExportStore
{
    private const string Columns =
        "id, report_code, format, status::text, row_count, error_code, created_at, finished_at, expires_at, file_name, (parameters->>'fundId')::uuid";

    public async Task InsertAsync(
        Guid id, Guid organizationId, Guid requestedBy, string reportCode, string format, string parametersJson, Guid idempotencyKey, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fl.export_jobs (id, organization_id, requested_by, report_code, format, parameters, idempotency_key, status, created_at)
            VALUES ({id}, {organizationId}, {requestedBy}, {reportCode}, {format}, {parametersJson}::jsonb, {idempotencyKey}, 'QUEUED'::fl.export_status, {now})
            """, ct).ConfigureAwait(false);

    public async Task<ExportJobDto?> FindByKeyAsync(Guid requestedBy, Guid idempotencyKey, CancellationToken ct) =>
        (await RawSql.QueryAsync(db, $"SELECT {Columns} FROM fl.export_jobs WHERE requested_by = @me AND idempotency_key = @key",
            [("me", requestedBy), ("key", idempotencyKey)], Map, ct).ConfigureAwait(false)).SingleOrDefault();

    public async Task<IReadOnlyList<ExportJobDto>> ListAsync(Guid requestedBy, Guid? id, CancellationToken ct) =>
        id is { } one
            ? await RawSql.QueryAsync(db, $"SELECT {Columns} FROM fl.export_jobs WHERE requested_by = @me AND id = @id",
                [("me", requestedBy), ("id", one)], Map, ct).ConfigureAwait(false)
            : await RawSql.QueryAsync(db, $"SELECT {Columns} FROM fl.export_jobs WHERE requested_by = @me ORDER BY created_at DESC LIMIT 50",
                [("me", requestedBy)], Map, ct).ConfigureAwait(false);

    public async Task<byte[]?> ReadContentAsync(Guid requestedBy, Guid id, CancellationToken ct) =>
        (await RawSql.QueryAsync(db, "SELECT content FROM fl.export_jobs WHERE requested_by = @me AND id = @id AND status = 'SUCCEEDED'::fl.export_status",
            [("me", requestedBy), ("id", id)], r => r.IsDBNull(0) ? null : r.GetFieldValue<byte[]>(0), ct).ConfigureAwait(false)).SingleOrDefault();

    public async Task ExpireAsync(Guid requestedBy, DateTimeOffset now, TimeSpan stuckAfter, CancellationToken ct)
    {
        var stuck = now - stuckAfter;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE fl.export_jobs SET status = 'EXPIRED'::fl.export_status, content = NULL
             WHERE requested_by = {requestedBy} AND status = 'SUCCEEDED'::fl.export_status AND expires_at < {now}
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE fl.export_jobs SET status = 'FAILED'::fl.export_status, error_code = 'EXPORT_TIMEOUT', finished_at = {now}
             WHERE requested_by = {requestedBy} AND status IN ('QUEUED'::fl.export_status, 'RUNNING'::fl.export_status) AND created_at < {stuck}
            """, ct).ConfigureAwait(false);
    }

    public async Task<ExportJobParameters?> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        const string sql = """
            UPDATE fl.export_jobs SET status = 'RUNNING'::fl.export_status, started_at = @now
             WHERE id = @id AND status = 'QUEUED'::fl.export_status
            RETURNING report_code, format, parameters::text
            """;
        var rows = await RawSql.QueryAsync(db, sql, [("now", now), ("id", id)], r => (Code: r.GetString(0), Format: r.GetString(1), Json: r.GetString(2)), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var (code, format, json) = rows[0];
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;
        DateOnly? Date(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? DateOnly.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        Guid? Id(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Guid.Parse(v.GetString()!) : null;
        TxnType? type = p.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && Enum.TryParse<TxnType>(t.GetString(), out var parsed) ? parsed : null;

        _ = ReportCodes.TryParse(code, out var reportCode);
        _ = Enum.TryParse<ExportFormat>(format, ignoreCase: true, out var fmt);
        return new ExportJobParameters(ReportCodes.Wire(reportCode), fmt,
            new ReportQuery(Id("fundId")!.Value, Date("from"), Date("to"), type, Id("categoryId"), Id("accountId"), Id("userId"), Id("paymentModeId")));
    }

    public async Task CompleteAsync(Guid id, string fileName, byte[] content, int rows, DateTimeOffset now, CancellationToken ct)
    {
        var expires = now + ExportService.Lifetime;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE fl.export_jobs
               SET status = 'SUCCEEDED'::fl.export_status, content = {content}, file_name = {fileName}, row_count = {rows}, finished_at = {now}, expires_at = {expires}
             WHERE id = {id}
            """, ct).ConfigureAwait(false);
    }

    public async Task FailAsync(Guid id, string errorCode, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE fl.export_jobs SET status = 'FAILED'::fl.export_status, error_code = {errorCode}, finished_at = {now} WHERE id = {id}
            """, ct).ConfigureAwait(false);

    private static ExportJobDto Map(System.Data.Common.DbDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetGuid(10));
}
