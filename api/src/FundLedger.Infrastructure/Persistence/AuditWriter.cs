using System.Text.Json;
using FundLedger.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Appends to <c>fl.audit_logs</c> with plain INSERT (no RETURNING): only Admins may
/// read the audit log under RLS, so EF's insert-and-read-back would fail for events
/// caused by Members (e.g. their own login). Runs in the caller's transaction.
/// </summary>
public sealed class AuditWriter(FundLedgerDbContext db, IRequestContext request, TimeProvider clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var oldValue = entry.OldValue is null ? null : JsonSerializer.Serialize(entry.OldValue, Json);
        var newValue = entry.NewValue is null ? null : JsonSerializer.Serialize(entry.NewValue, Json);
        var device = request.UserAgent is { Length: > 200 } ua ? ua[..200] : request.UserAgent;
        var requestId = request.RequestId is { Length: > 64 } rid ? rid[..64] : request.RequestId;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fl.audit_logs
              (organization_id, user_id, action, entity_type, entity_id, fund_id, old_value, new_value,
               reason, request_id, ip_address, device_info, created_at)
            VALUES
              ({entry.OrganizationId}, {entry.UserId}, {entry.Action}, {entry.EntityType}, {entry.EntityId}, {entry.FundId},
               {oldValue}::jsonb, {newValue}::jsonb, {entry.Reason}, {requestId}, {request.IpAddress}::inet, {device},
               {clock.GetUtcNow()})
            """, ct).ConfigureAwait(false);
    }
}
