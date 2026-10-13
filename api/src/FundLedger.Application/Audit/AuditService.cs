using System.Globalization;
using System.Text;
using System.Text.Json;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Ledger;
using FundLedger.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Audit;

public sealed record AuditItem(
    long Id, DateTimeOffset CreatedAt, PersonRef? User, string Action, string EntityType, string? EntityId, Guid? FundId, string? FundName,
    JsonElement? OldValue, JsonElement? NewValue, string? Reason, string? RequestId, string? IpAddress);

public sealed record AuditPage(IReadOnlyList<AuditItem> Items, string? NextCursor);

public sealed record AuditQuery(
    DateOnly? From, DateOnly? To, Guid? UserId, string? Group, string? Action, Guid? FundId, string? Q, int? Limit, string? Cursor);

/// <summary>Resolved filter handed to the reader: instants instead of org-local dates, actions instead of a group.</summary>
public sealed record AuditFilter(
    DateTimeOffset? FromUtc, DateTimeOffset? ToUtcExclusive, Guid? UserId, IReadOnlyList<string>? Actions, Guid? FundId, string? Q);

/// <summary>Reads <c>fl.audit_logs</c>. RLS makes the table readable by Admins only (PRD §14, TR-070).</summary>
public interface IAuditReader
{
    Task<IReadOnlyList<AuditItem>> QueryAsync(AuditFilter filter, int skip, int take, CancellationToken ct);
}

/// <summary>
/// The audit log screen and its CSV export (App Flow §5.4). Admin-only; the export is itself audited
/// (PRD §14.2 "Export performed") and capped, because it is produced synchronously. Larger or scheduled
/// exports use the async export pipeline (TRD §10.2).
/// </summary>
public sealed class AuditService(IFundLedgerDb db, ICurrentUser caller, IAuditReader reader, IAuditWriter audit)
{
    public const int ExportCap = 5000;
    private const int DefaultPage = 50;
    private const int MaxPage = 200;

    /// <summary>Action groups shown as a filter (App Flow §8).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["AUTH"] = [AuditActions.Login, AuditActions.LoginFailed, AuditActions.Logout, AuditActions.SessionRevoked, AuditActions.AccountLocked, AuditActions.PinChanged, AuditActions.PinReset],
        ["USERS"] = [AuditActions.UserCreated, AuditActions.UserUpdated, AuditActions.UserActivated, AuditActions.UserDeactivated, AuditActions.FundAccessChanged],
        ["FUNDS"] = [AuditActions.FundCreated, AuditActions.FundUpdated, AuditActions.FundActivated, AuditActions.FundClosed, AuditActions.FundReopened, AuditActions.FundArchived, AuditActions.OpeningBalanceChanged],
        ["MASTER"] = [AuditActions.AccountCreated, AuditActions.AccountUpdated, AuditActions.CategoryCreated, AuditActions.CategoryUpdated, AuditActions.LookupChanged],
        ["TRANSACTIONS"] = [AuditActions.TxnCreated, AuditActions.TxnUpdated, AuditActions.TxnCancelled, AuditActions.ReceiptGenerated, AuditActions.OfflineEntryStale, AuditActions.OfflineEntryDiscarded],
        ["SYSTEM"] = [AuditActions.OrganizationCreated, AuditActions.AuditExported, AuditActions.SettingsChanged, AuditActions.ExportPerformed],
    };

    public async Task<AuditPage> QueryAsync(AuditQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        RequireAdmin();
        var filter = await ResolveAsync(q, ct).ConfigureAwait(false);
        var limit = Math.Clamp(q.Limit ?? DefaultPage, 1, MaxPage);
        var offset = Cursor.Decode(q.Cursor);
        var rows = await reader.QueryAsync(filter, offset, limit + 1, ct).ConfigureAwait(false);
        return new AuditPage(rows.Take(limit).ToList(), rows.Count > limit ? Cursor.Encode(offset + limit) : null);
    }

    /// <summary>Returns the CSV bytes and a file name. Cells that start with a formula character are neutralised (CSV injection).</summary>
    public async Task<(string FileName, byte[] Content, int Rows)> ExportCsvAsync(AuditQuery q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(q);
        RequireAdmin();
        var filter = await ResolveAsync(q, ct).ConfigureAwait(false);
        var rows = await reader.QueryAsync(filter, 0, ExportCap, ct).ConfigureAwait(false);

        var tz = await ZoneAsync(ct).ConfigureAwait(false);
        var sb = new StringBuilder("Time,User,Action,Entity,Entity id,Fund,Reason,Old value,New value,Request id\r\n");
        foreach (var r in rows)
        {
            var local = TimeZoneInfo.ConvertTime(r.CreatedAt, tz).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            sb.AppendJoin(',', Csv(local), Csv(r.User?.Name), Csv(r.Action), Csv(r.EntityType), Csv(r.EntityId), Csv(r.FundName), Csv(r.Reason),
                Csv(r.OldValue?.GetRawText()), Csv(r.NewValue?.GetRawText()), Csv(r.RequestId)).Append("\r\n");
        }

        // Written after the read, so the export event is not part of its own output.
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.AuditExported, AuditEntities.Organization,
            caller.OrganizationId.ToString(), NewValue: new { format = "CSV", rows = rows.Count, capped = rows.Count >= ExportCap,
                from = q.From, to = q.To, group = q.Group, action = q.Action, userId = q.UserId, fundId = q.FundId }), ct).ConfigureAwait(false);

        var stamp = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return ($"audit-log-{stamp}.csv", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())], rows.Count);
    }

    private void RequireAdmin()
    {
        if (!caller.IsAdmin)
        {
            throw new ForbiddenException();
        }
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        var id = await db.Organizations.AsNoTracking().Select(o => o.Timezone).SingleAsync(ct).ConfigureAwait(false);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        }
    }

    private async Task<AuditFilter> ResolveAsync(AuditQuery q, CancellationToken ct)
    {
        var tz = await ZoneAsync(ct).ConfigureAwait(false);
        DateTimeOffset StartOf(DateOnly d) => new(d.ToDateTime(TimeOnly.MinValue), tz.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue)));

        IReadOnlyList<string>? actions = null;
        if (!string.IsNullOrWhiteSpace(q.Action))
        {
            actions = [q.Action.Trim().ToUpperInvariant()];
        }
        else if (!string.IsNullOrWhiteSpace(q.Group))
        {
            actions = Groups.TryGetValue(q.Group.Trim(), out var g) ? g : throw ValidationFailedException.For("group", "Unknown action group.");
        }

        return new AuditFilter(q.From is { } f ? StartOf(f) : null, q.To is { } t ? StartOf(t.AddDays(1)) : null, q.UserId, actions, q.FundId,
            string.IsNullOrWhiteSpace(q.Q) ? null : q.Q.Trim());
    }

    /// <summary>
    /// Spreadsheet formula injection: a cell starting with = + - @ (or tab/CR) can run as a formula in Excel,
    /// so it is prefixed with an apostrophe. Values are also quoted and embedded quotes doubled.
    /// </summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>Opaque offset cursor shared by list endpoints.</summary>
internal static class Cursor
{
    public static string Encode(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"o:{offset}")));

    public static int Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }
}
