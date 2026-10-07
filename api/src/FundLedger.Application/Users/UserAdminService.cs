using FundLedger.Application.Abstractions;
using FundLedger.Application.Auth;
using FundLedger.Application.Errors;
using FundLedger.Application.Settings;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Application.Users;

/// <summary>
/// Admin user management (PRD §21.1, App Flow §5.1). Runs inside the request's tenant
/// transaction; RLS confines every query to the Admin's organization, so a user id
/// from another tenant simply isn't found (404).
///
/// Guard rails: no self-deactivation/demotion/PIN-reset, never remove the last
/// active Admin, 50-active-user limit (API check + DB trigger), and every change is audited.
/// </summary>
public sealed class UserAdminService(
    IFundLedgerDb db,
    ICurrentUser caller,
    IPinHasher hasher,
    IAuditWriter audit,
    ISettingsProvider settings,
    AuthService auth,
    TimeProvider clock)
{
    public async Task<UserList> ListAsync(UserStatus? status, UserRole? role, string? search, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();
        if (status is { } st)
        {
            query = query.Where(u => u.Status == st);
        }

        if (role is { } r)
        {
            query = query.Where(u => u.Role == r);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            query = query.Where(u => EF.Functions.ILike(u.FullName, $"%{term}%")
                                     || (digits.Length >= 3 && u.MobileE164.Contains(digits)));
        }

        var users = await query.OrderBy(u => u.Status).ThenBy(u => u.FullName).Take(200).ToListAsync(ct).ConfigureAwait(false);
        var ids = users.Select(u => u.Id).ToList();
        var fundCounts = await db.UserFundAccess.AsNoTracking()
            .Where(a => ids.Contains(a.UserId))
            .GroupBy(a => a.UserId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);

        var activeCount = await db.Users.CountAsync(u => u.Status == UserStatus.Active, ct).ConfigureAwait(false);
        var max = await db.Organizations.Select(o => o.MaxActiveUsers).SingleAsync(ct).ConfigureAwait(false);

        return new UserList(
            users.Select(u => new UserListItem(u.Id, u.FullName, u.MobileE164, u.Email, u.Role, u.Status, u.LastLoginAt,
                u.IsAdmin ? -1 : fundCounts.GetValueOrDefault(u.Id), u.PinMustChange)).ToList(),
            activeCount, max);
    }

    public async Task<UserDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException();
        return await ToDetailAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<CreatedUser> CreateAsync(CreateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mobile = MobileNumber.Normalize(request.Mobile);
        await EnsureActiveSlotAsync(ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var temporaryPin = PinPolicy.GenerateTemporary(mobile);
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = caller.OrganizationId,
            FullName = request.FullName.Trim(),
            MobileE164 = mobile,
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Role = request.Role,
            Status = UserStatus.Active,
            PinHash = hasher.Hash(temporaryPin),
            PinSetAt = now,
            PinMustChange = true,
            CreatedBy = caller.UserId,
            UpdatedBy = caller.UserId,
        };
        db.Users.Add(user);
        await SaveAsync(ct).ConfigureAwait(false);

        if (request.Role == UserRole.Member && request.FundAccess is { Count: > 0 } grants)
        {
            await ReplaceFundAccessAsync(user, grants, now, ct).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.UserCreated, AuditEntities.User,
            user.Id.ToString(), NewValue: Snapshot(user)), ct).ConfigureAwait(false);

        return new CreatedUser(await ToDetailAsync(user, ct).ConfigureAwait(false), temporaryPin);
    }

    public async Task<UserDetail> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await FindTrackedAsync(id, ct).ConfigureAwait(false);
        if (user.Version != request.Version)
        {
            throw new ConflictException("REVISION_CONFLICT", "This user was changed by someone else. Reload and try again.");
        }

        if (id == caller.UserId && request.Role != user.Role)
        {
            throw new ConflictException("CANNOT_CHANGE_SELF", "You can't change your own role.");
        }

        if (user.IsAdmin && user.IsActive && request.Role != UserRole.Admin)
        {
            await EnsureAnotherActiveAdminAsync(user.Id, ct).ConfigureAwait(false);
        }

        var before = Snapshot(user);
        user.FullName = request.FullName.Trim();
        user.MobileE164 = MobileNumber.Normalize(request.Mobile);
        user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        user.Role = request.Role;
        user.UpdatedBy = caller.UserId;

        if (user.Role == UserRole.Admin)
        {
            // Admins see every fund; explicit grants would be misleading.
            await db.UserFundAccess.Where(a => a.UserId == user.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await SaveAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.UserUpdated, AuditEntities.User,
            user.Id.ToString(), before, Snapshot(user)), ct).ConfigureAwait(false);
        return await ToDetailAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<UserDetail> SetStatusAsync(Guid id, SetUserStatusRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await FindTrackedAsync(id, ct).ConfigureAwait(false);
        if (user.Status == request.Status)
        {
            return await ToDetailAsync(user, ct).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        if (request.Status == UserStatus.Inactive)
        {
            if (id == caller.UserId)
            {
                throw new ConflictException("CANNOT_CHANGE_SELF", "You can't deactivate yourself.");
            }

            if (user.IsAdmin)
            {
                await EnsureAnotherActiveAdminAsync(user.Id, ct).ConfigureAwait(false);
            }

            user.Status = UserStatus.Inactive;
            user.DeactivatedAt = now;
            user.DeactivatedBy = caller.UserId;
        }
        else
        {
            await EnsureActiveSlotAsync(ct).ConfigureAwait(false);
            user.Status = UserStatus.Active;
            user.DeactivatedAt = null;
            user.DeactivatedBy = null;
        }

        user.UpdatedBy = caller.UserId;
        await SaveAsync(ct).ConfigureAwait(false);

        var signedOut = 0;
        if (user.Status == UserStatus.Inactive)
        {
            signedOut = await auth.RevokeAllForUserAsync(user.Id, RevokeReasons.UserDeactivated, ct).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId,
            user.IsActive ? AuditActions.UserActivated : AuditActions.UserDeactivated, AuditEntities.User, user.Id.ToString(),
            NewValue: new { status = user.Status.ToString(), sessionsSignedOut = signedOut }, Reason: request.Reason), ct)
            .ConfigureAwait(false);
        return await ToDetailAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<UserDetail> SetFundAccessAsync(Guid id, SetFundAccessRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = await FindTrackedAsync(id, ct).ConfigureAwait(false);
        if (user.IsAdmin)
        {
            throw new ConflictException("ADMIN_HAS_ALL_FUNDS", "Admins already have access to every fund.");
        }

        var before = await GrantsOfAsync(user.Id, ct).ConfigureAwait(false);
        await ReplaceFundAccessAsync(user, request.Items, clock.GetUtcNow(), ct).ConfigureAwait(false);
        var after = await GrantsOfAsync(user.Id, ct).ConfigureAwait(false);

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.FundAccessChanged,
            AuditEntities.User, user.Id.ToString(), before, after), ct).ConfigureAwait(false);
        return await ToDetailAsync(user, ct).ConfigureAwait(false);
    }

    public async Task<PinResetResult> ResetPinAsync(Guid id, CancellationToken ct)
    {
        var user = await FindTrackedAsync(id, ct).ConfigureAwait(false);
        if (id == caller.UserId)
        {
            throw new ConflictException("CANNOT_CHANGE_SELF", "Use Change PIN to change your own PIN.");
        }

        var now = clock.GetUtcNow();
        var temporaryPin = PinPolicy.GenerateTemporary(user.MobileE164);
        user.PinHash = hasher.Hash(temporaryPin);
        user.PinSetAt = now;                 // also restarts the lockout window (TR-013)
        user.PinMustChange = true;
        user.UpdatedBy = caller.UserId;
        await SaveAsync(ct).ConfigureAwait(false);

        var signedOut = await auth.RevokeAllForUserAsync(user.Id, RevokeReasons.PinReset, ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.PinReset, AuditEntities.User,
            user.Id.ToString(), NewValue: new { sessionsSignedOut = signedOut }), ct).ConfigureAwait(false);
        return new PinResetResult(temporaryPin);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(Guid id, CancellationToken ct)
    {
        _ = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException();

        var now = clock.GetUtcNow();
        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        var sessions = await db.UserSessions.AsNoTracking().Where(x => x.UserId == id).ToListAsync(ct).ConfigureAwait(false);

        return sessions
            .GroupBy(x => x.FamilyId)
            .Where(g => !g.Any(x => x.RevokedAt != null && x.RevokeReason != RevokeReasons.Rotated))
            .Select(g => (Family: g.Key, Head: g.Where(x => x.RevokedAt == null).OrderByDescending(x => x.CreatedAt).FirstOrDefault(),
                          Started: g.Min(x => x.CreatedAt)))
            .Where(x => x.Head is not null && now < x.Head.ExpiresAt
                        && now - (x.Head.LastUsedAt ?? x.Head.CreatedAt) <= TimeSpan.FromMinutes(s.SessionIdleMinutes))
            .Select(x => new SessionInfo(x.Family, x.Head!.DeviceLabel, x.Head.IpAddress?.ToString(), x.Started,
                x.Head.LastUsedAt, x.Head.ExpiresAt, x.Family == caller.FamilyId))
            .OrderByDescending(x => x.LastUsedAt)
            .ToList();
    }

    public async Task<int> RevokeSessionsAsync(Guid id, RevokeSessionsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException();

        var now = clock.GetUtcNow();
        var open = await db.UserSessions
            .Where(x => x.UserId == id && x.RevokedAt == null && (request.FamilyId == null || x.FamilyId == request.FamilyId))
            .ToListAsync(ct).ConfigureAwait(false);
        if (request.FamilyId is not null && open.Count == 0)
        {
            throw new NotFoundException();
        }

        open.ForEach(x => x.Revoke(now, RevokeReasons.AdminRevoked));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var families = open.Select(x => x.FamilyId).Distinct().Count();

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.SessionRevoked, AuditEntities.User,
            id.ToString(), NewValue: new { sessions = families, reason = RevokeReasons.AdminRevoked }), ct).ConfigureAwait(false);
        return families;
    }

    private async Task<User> FindTrackedAsync(Guid id, CancellationToken ct) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();

    private async Task EnsureActiveSlotAsync(CancellationToken ct)
    {
        var active = await db.Users.CountAsync(u => u.Status == UserStatus.Active, ct).ConfigureAwait(false);
        var max = await db.Organizations.Select(o => o.MaxActiveUsers).SingleAsync(ct).ConfigureAwait(false);
        if (active >= max)
        {
            throw new ConflictException("ACTIVE_USER_LIMIT_REACHED",
                $"You've reached the limit of {max} active users. Deactivate a user to add another.");
        }
    }

    private async Task EnsureAnotherActiveAdminAsync(Guid exceptUserId, CancellationToken ct)
    {
        var others = await db.Users.CountAsync(
            u => u.Role == UserRole.Admin && u.Status == UserStatus.Active && u.Id != exceptUserId, ct).ConfigureAwait(false);
        if (others == 0)
        {
            throw new ConflictException("LAST_ADMIN", "At least one active Admin is required.");
        }
    }

    private async Task ReplaceFundAccessAsync(User user, IReadOnlyList<FundAccessGrant> grants, DateTimeOffset now, CancellationToken ct)
    {
        var fundIds = grants.Select(g => g.FundId).ToList();
        var known = await db.Funds.AsNoTracking().Where(f => fundIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
        if (known.Count != fundIds.Count)
        {
            throw ValidationFailedException.For("fundAccess", "One or more funds don't exist.");
        }

        var existing = await db.UserFundAccess.Where(a => a.UserId == user.Id).ToListAsync(ct).ConfigureAwait(false);
        var removed = existing.Where(e => !fundIds.Contains(e.FundId)).Select(e => e.FundId).ToList();
        if (removed.Count > 0)
        {
            // The only table where the runtime role may DELETE: access rows are revocable by design.
            await db.UserFundAccess.Where(a => a.UserId == user.Id && removed.Contains(a.FundId)).ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        foreach (var grant in grants)
        {
            var row = existing.FirstOrDefault(e => e.FundId == grant.FundId);
            if (row is null)
            {
                row = new UserFundAccess { OrganizationId = user.OrganizationId, UserId = user.Id, FundId = grant.FundId };
                db.UserFundAccess.Add(row);
            }

            row.CanMoneyIn = grant.CanMoneyIn;
            row.CanMoneyOut = grant.CanMoneyOut;
            row.CanTransfer = grant.CanTransfer;
            row.CanViewReports = grant.CanViewReports;
            row.CanExport = grant.CanExport;
            row.CanViewAllTxns = grant.CanViewAllTxns;
            row.GrantedBy = caller.UserId;
            row.GrantedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<FundAccessGrant>> GrantsOfAsync(Guid userId, CancellationToken ct) =>
        await db.UserFundAccess.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.FundId)
            .Select(a => new FundAccessGrant(a.FundId, a.CanMoneyIn, a.CanMoneyOut, a.CanTransfer, a.CanViewReports, a.CanExport,
                a.CanViewAllTxns))
            .ToListAsync(ct).ConfigureAwait(false);

    private async Task<UserDetail> ToDetailAsync(User user, CancellationToken ct)
    {
        var version = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.Version).SingleAsync(ct)
            .ConfigureAwait(false);
        return new UserDetail(user.Id, user.FullName, user.MobileE164, user.Email, user.Role, user.Status, user.LastLoginAt,
            user.PinMustChange, user.CreatedAt, user.DeactivatedAt, await GrantsOfAsync(user.Id, ct).ConfigureAwait(false), version);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                                           && pg.ConstraintName == "uq_users_org_mobile")
        {
            throw new ConflictException("MOBILE_ALREADY_REGISTERED", "This mobile number is already registered.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("REVISION_CONFLICT", "This user was changed by someone else. Reload and try again.");
        }
    }

    /// <summary>Audit snapshot: never includes the PIN hash; mobile is masked (TR-077).</summary>
    private static object Snapshot(User u) => new
    {
        fullName = u.FullName,
        mobile = MobileNumber.Mask(u.MobileE164),
        email = u.Email,
        role = u.Role.ToString(),
        status = u.Status.ToString(),
    };
}
