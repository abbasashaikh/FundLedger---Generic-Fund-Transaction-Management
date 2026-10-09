using System.Text;
using System.Text.Json;
using FundLedger.Application.Audit;
using FundLedger.Application.Ledger;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Reads the audit log with every filter passed as a SQL parameter (no string-built values). The table is
/// protected by a RESTRICTIVE RLS policy, so a non-Admin session sees no rows even if this were called.
/// </summary>
public sealed class AuditReader(FundLedgerDbContext db) : IAuditReader
{
    public async Task<IReadOnlyList<AuditItem>> QueryAsync(AuditFilter filter, int skip, int take, CancellationToken ct)
    {
        var where = new StringBuilder("TRUE");
        var args = new List<(string, object)>();

        void Add(string clause, string name, object value)
        {
            where.Append(" AND ").Append(clause);
            args.Add((name, value));
        }

        if (filter.FromUtc is { } from) Add("a.created_at >= @from", "from", from.UtcDateTime);
        if (filter.ToUtcExclusive is { } to) Add("a.created_at < @to", "to", to.UtcDateTime);
        if (filter.UserId is { } user) Add("a.user_id = @user", "user", user);
        if (filter.FundId is { } fund) Add("a.fund_id = @fund", "fund", fund);
        if (filter.Actions is { Count: > 0 } actions) Add("a.action = ANY(@actions)", "actions", actions.ToArray());
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var like = "%" + filter.Q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            Add("(a.entity_id ILIKE @q OR a.action ILIKE @q OR a.reason ILIKE @q OR u.full_name ILIKE @q)", "q", like);
        }

        args.Add(("take", take));
        args.Add(("skip", skip));

        var sql = $"""
            SELECT a.id, a.created_at, a.user_id, u.full_name, a.action, a.entity_type, a.entity_id, a.fund_id, fu.name,
                   a.old_value::text, a.new_value::text, a.reason, a.request_id, host(a.ip_address)
              FROM fl.audit_logs a
              LEFT JOIN fl.users u ON u.id = a.user_id
              LEFT JOIN fl.funds fu ON fu.id = a.fund_id
             WHERE {where}
             ORDER BY a.id DESC
             LIMIT @take OFFSET @skip
            """;

        return await RawSql.QueryAsync(db, sql, args.ToArray(), r => new AuditItem(
            r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1),
            r.IsDBNull(2) ? null : new PersonRef(r.GetGuid(2), r.GetString(3)),
            r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetString(8),
            Json(r, 9), Json(r, 10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetString(13)), ct).ConfigureAwait(false);
    }

    private static JsonElement? Json(System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(r.GetString(i));
        return doc.RootElement.Clone();
    }
}
