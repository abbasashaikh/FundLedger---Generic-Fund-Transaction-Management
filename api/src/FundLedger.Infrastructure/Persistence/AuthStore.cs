using System.Data.Common;
using FundLedger.Application.Abstractions;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>
/// Pre-authentication queries. User and session lookups go through the narrowly
/// scoped SECURITY DEFINER functions (TR-003) because no tenant is known yet;
/// <c>login_attempts</c> has no RLS by design. Runs in the caller's transaction if any.
/// </summary>
public sealed class AuthStore(FundLedgerDbContext db, IRequestContext request, TimeProvider clock) : IAuthStore
{
    public async Task<AuthUserRecord?> FindUserByMobileAsync(string mobileE164, CancellationToken ct)
    {
        var rows = await QueryAsync(
            "SELECT user_id, organization_id, role::text, status::text, pin_hash, pin_set_at, pin_must_change " +
            "FROM fl.auth_find_user_by_mobile(@mobile)",
            [("mobile", mobileE164)],
            r => new AuthUserRecord(
                r.GetGuid(0),
                r.GetGuid(1),
                r.GetString(2) == "ADMIN" ? UserRole.Admin : UserRole.Member,
                r.GetString(3) == "ACTIVE" ? UserStatus.Active : UserStatus.Inactive,
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
                r.GetBoolean(6)),
            ct).ConfigureAwait(false);

        // V1 is single-organization; if a number ever exists in several, prefer the active account.
        return rows.OrderBy(r => r.Status == UserStatus.Active ? 0 : 1).FirstOrDefault();
    }

    public async Task<AuthSessionRecord?> FindSessionByHashAsync(byte[] refreshTokenHash, CancellationToken ct)
    {
        var rows = await QueryAsync(
            "SELECT id, organization_id, user_id, family_id FROM fl.auth_find_session(@hash)",
            [("hash", refreshTokenHash)],
            r => new AuthSessionRecord(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3)),
            ct).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public async Task<FailureWindow> GetRecentFailuresAsync(string mobileE164, DateTimeOffset since, int take, CancellationToken ct)
    {
        var rows = await QueryAsync(
            """
            WITH last_success AS (
              SELECT max(created_at) AS t FROM fl.login_attempts WHERE mobile_e164 = @mobile AND succeeded
            ), counted AS (
              SELECT a.created_at
                FROM fl.login_attempts a, last_success s
               WHERE a.mobile_e164 = @mobile AND NOT a.succeeded
                 AND coalesce(a.failure_code, '') <> 'LOCKED'
                 AND a.created_at > @since
                 AND (s.t IS NULL OR a.created_at > s.t)
               ORDER BY a.created_at DESC
               LIMIT @take
            )
            SELECT count(*)::int, min(created_at) FROM counted
            """,
            [("mobile", mobileE164), ("since", since), ("take", take)],
            r => new FailureWindow(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1)),
            ct).ConfigureAwait(false);
        return rows.Single();
    }

    public Task RecordAttemptAsync(string mobileE164, Guid? userId, bool succeeded, string? failureCode, CancellationToken ct)
    {
        var userAgent = request.UserAgent is { Length: > 300 } ua ? ua[..300] : request.UserAgent;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO fl.login_attempts (mobile_e164, user_id, succeeded, failure_code, ip_address, user_agent, created_at)
            VALUES ({mobileE164}, {userId}, {succeeded}, {failureCode}, {request.IpAddress}::inet, {userAgent}, {clock.GetUtcNow()})
            """, ct);
    }

    private Task<List<T>> QueryAsync<T>(
        string sql, (string Name, object Value)[] parameters, Func<DbDataReader, T> map, CancellationToken ct) =>
        RawSql.QueryAsync(db, sql, parameters, map, ct);
}
