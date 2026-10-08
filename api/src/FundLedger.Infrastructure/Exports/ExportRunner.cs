using System.Globalization;
using System.Threading.Channels;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Reports;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Users;
using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FundLedger.Infrastructure.Exports;

/// <summary>
/// Builds export files off the request path (TRD §10.2). Each job runs with its requester's own identity (their
/// organization, role and fund access), so an export can never contain more than the screen would show.
/// Jobs live in memory only as ids; the row in <c>fl.export_jobs</c> is the source of truth, and a job lost to a restart
/// is reported as failed by <c>ExportService</c> after <see cref="ExportService.StuckAfter"/>.
/// </summary>
public sealed partial class ExportRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ExportRunner> log) : BackgroundService, IExportDispatcher
{
    private readonly Channel<ExportWork> _queue = Channel.CreateUnbounded<ExportWork>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(ExportWork work) => _queue.Writer.TryWrite(work);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunAsync(work, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRunnerError(ex, work.JobId);
            }
        }
    }

    /// <summary>Runs one job to completion. Public so tests can drive it without waiting for the background loop.</summary>
    public async Task RunAsync(ExportWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<TenantContext>().Set(work.OrganizationId, work.UserId, work.IsAdmin);
        sp.GetRequiredService<CurrentUser>().Set(work.UserId, work.OrganizationId, Guid.Empty, Guid.Empty, work.IsAdmin ? UserRole.Admin : UserRole.Member, pinMustChange: false);
        var db = sp.GetRequiredService<FundLedgerDbContext>();
        var store = sp.GetRequiredService<IExportStore>();

        // The API commits the new row when its request ends, which can be a moment after the job was queued.
        ExportJobParameters? job = null;
        for (var attempt = 0; attempt < 25 && job is null; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            job = await store.ClaimAsync(work.JobId, clock.GetUtcNow(), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            if (job is null)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        if (job is null)
        {
            return;     // never became visible, or was already taken: nothing to do
        }

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            _ = ReportCodes.TryParse(job.ReportCode, out var reportCode);
            var report = await sp.GetRequiredService<ReportService>().RunAsync(reportCode, job.Query, ct, ReportService.ExportRowLimit).ConfigureAwait(false);
            var bytes = sp.GetRequiredService<IDocumentRenderer>().Report(report, job.Format);
            var name = $"{job.ReportCode.ToLowerInvariant().Replace('_', '-')}-{report.From:yyyyMMdd}-{report.To:yyyyMMdd}.{job.Format.ToString().ToLowerInvariant()}";
            await store.CompleteAsync(work.JobId, name, bytes, report.Rows.Count, clock.GetUtcNow(), ct).ConfigureAwait(false);
            await sp.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(work.OrganizationId, work.UserId, AuditActions.ExportPerformed, "Export",
                work.JobId.ToString(), NewValue: new
                {
                    report = job.ReportCode, format = job.Format.ToString().ToUpperInvariant(), rows = report.Rows.Count, fundId = job.Query.FundId,
                    from = report.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = report.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ownOnly = report.OwnOnly, truncated = report.Truncated,
                }, FundId: job.Query.FundId), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ex is AppException app ? app.Code : "EXPORT_FAILED";
            LogJobFailed(ex, work.JobId, code);
            await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            await store.FailAsync(work.JobId, code, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Export runner error for job {JobId}")]
    private partial void LogRunnerError(Exception ex, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export job {JobId} failed with {Code}")]
    private partial void LogJobFailed(Exception ex, Guid jobId, string code);
}
