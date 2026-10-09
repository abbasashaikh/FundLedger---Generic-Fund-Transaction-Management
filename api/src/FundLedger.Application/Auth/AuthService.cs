using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Settings;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Auth;

/// <summary>Tokens and profile returned after login or refresh. The refresh token goes in an HttpOnly cookie.</summary>
public sealed record AuthOutcome(
    string AccessToken,
    int ExpiresInSeconds,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    SessionProfile User);

public sealed record SessionProfile(Guid Id, Guid OrganizationId, string FullName, UserRole Role, bool PinMustChange);

/// <summary>
/// Mobile + PIN authentication and session lifecycle (ADR-0002, TRD §6).
///
/// Security properties enforced here (each has an integration test):
///  * one generic failure for unknown number / inactive user / wrong PIN, with
///    equalized hashing time (TR-011);
///  * per-mobile lockout counted from <c>login_attempts</c>, also for unknown numbers (TR-013);
///  * refresh tokens rotate on every use; replaying a rotated token revokes the
///    whole chain, except a short race window for parallel tabs (TR-015);
///  * logout, PIN change/reset and deactivation revoke sessions server-side (TR-016);
///  * every authenticated request re-checks the session family and user (TR-006).
/// </summary>
public sealed class AuthService(
    IFundLedgerDb db,
    TenantContext tenant,
    IAuthStore authStore,
    IPinHasher hasher,
    ITokenService tokens,
    IAuditWriter audit,
    ISettingsProvider settings,
    IRequestContext request,
    TimeProvider clock)
{
    /// <summary>A rotated refresh token reused within this window is a parallel-tab race, not theft.</summary>
    public static readonly TimeSpan RefreshRaceWindow = TimeSpan.FromSeconds(10);

    private static UnauthenticatedException InvalidCredentials() =>
        new("INVALID_CREDENTIALS", "Mobile number or PIN is incorrect.");

    public async Task<AuthOutcome> LoginAsync(string? mobile, string? pin, CancellationToken ct)
    {
        if (!MobileNumber.TryNormalize(mobile, out var e164) || !PinPolicy.IsWellFormed(pin))
        {
            hasher.VerifyDummy(pin ?? string.Empty);
            throw InvalidCredentials();
        }

        var candidate = await authStore.FindUserByMobileAsync(e164, ct).ConfigureAwait(false);
        if (candidate is null)
        {
            await LoginUnknownNumberAsync(e164, pin!, ct).ConfigureAwait(false);
            throw InvalidCredentials();   // unreachable: the helper always throws
        }

        tenant.Set(candidate.OrganizationId, candidate.UserId, candidate.Role == UserRole.Admin);
        AuthOutcome? outcome = null;
        AppException? failure = null;

        await db.InTransactionAsync(async () =>
        {
            var s = await settings.GetAsync(ct).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var since = Later(now.AddMinutes(-s.PinLockoutMinutes), candidate.PinSetAt);
            var window = await authStore.GetRecentFailuresAsync(e164, since, s.PinMaxFailures, ct).ConfigureAwait(false);

            if (window.Failures >= s.PinMaxFailures)
            {
                await authStore.RecordAttemptAsync(e164, candidate.UserId, false, "LOCKED", ct).ConfigureAwait(false);
                hasher.VerifyDummy(pin!);
                failure = new TooManyAttemptsException(RetryAfter(window, s, now));
                return;
            }

            var verification = candidate.Status == UserStatus.Active && candidate.PinHash is not null
                ? hasher.Verify(candidate.PinHash, pin!)
                : DummyFailure(pin!);

            if (verification == PinVerification.Failed)
            {
                var code = candidate.Status == UserStatus.Active ? "BAD_PIN" : "INACTIVE";
                await authStore.RecordAttemptAsync(e164, candidate.UserId, false, code, ct).ConfigureAwait(false);
                await audit.WriteAsync(new AuditEntry(candidate.OrganizationId, candidate.UserId, AuditActions.LoginFailed,
                    AuditEntities.User, candidate.UserId.ToString(), NewValue: new { reason = code }), ct).ConfigureAwait(false);

                if (window.Failures + 1 >= s.PinMaxFailures)
                {
                    await audit.WriteAsync(new AuditEntry(candidate.OrganizationId, candidate.UserId, AuditActions.AccountLocked,
                        AuditEntities.User, candidate.UserId.ToString(),
                        NewValue: new { lockoutMinutes = s.PinLockoutMinutes }), ct).ConfigureAwait(false);
                }

                failure = InvalidCredentials();
                return;
            }

            var user = await db.Users.SingleAsync(u => u.Id == candidate.UserId, ct).ConfigureAwait(false);
            if (verification == PinVerification.SuccessRehashNeeded)
            {
                user.PinHash = hasher.Hash(pin!);
            }

            user.LastLoginAt = now;
            await authStore.RecordAttemptAsync(e164, user.Id, true, null, ct).ConfigureAwait(false);
            outcome = await StartSessionAsync(user, familyId: Guid.CreateVersion7(), expiresAt: now.AddDays(s.SessionAbsoluteDays), now, ct)
                .ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(user.OrganizationId, user.Id, AuditActions.Login, AuditEntities.Session,
                outcome.User.Id.ToString(), NewValue: new { method = "PIN" }), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // The transaction commits even on failure so attempts and audit rows persist.
        return failure is null ? outcome! : throw failure;
    }

    public async Task<AuthOutcome> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200)
        {
            throw new UnauthenticatedException();
        }

        var record = await authStore.FindSessionByHashAsync(tokens.HashRefreshToken(refreshToken), ct).ConfigureAwait(false)
            ?? throw new UnauthenticatedException();

        tenant.Set(record.OrganizationId, record.UserId, isAdmin: false);
        AuthOutcome? outcome = null;
        AppException? failure = null;

        await db.InTransactionAsync(async () =>
        {
            var now = clock.GetUtcNow();
            var s = await settings.GetAsync(ct).ConfigureAwait(false);
            var session = await db.UserSessions.SingleAsync(x => x.Id == record.SessionId, ct).ConfigureAwait(false);

            if (session.IsRevoked)
            {
                if (session.RevokeReason == RevokeReasons.Rotated && now - session.RevokedAt!.Value <= RefreshRaceWindow)
                {
                    failure = new UnauthenticatedException("REFRESH_RACE", "Session is being refreshed. Retry.");
                    return;
                }

                if (session.RevokeReason == RevokeReasons.Rotated)
                {
                    // A token that was already exchanged is being replayed: treat as theft.
                    await RevokeFamilyAsync(session.FamilyId, RevokeReasons.ReuseDetected, now, ct).ConfigureAwait(false);
                    await audit.WriteAsync(new AuditEntry(record.OrganizationId, record.UserId, AuditActions.SessionRevoked,
                        AuditEntities.Session, session.FamilyId.ToString(), NewValue: new { reason = RevokeReasons.ReuseDetected }), ct)
                        .ConfigureAwait(false);
                }

                failure = new UnauthenticatedException();
                return;
            }

            var lastActivity = session.LastUsedAt ?? session.CreatedAt;
            if (now >= session.ExpiresAt || now - lastActivity > TimeSpan.FromMinutes(s.SessionIdleMinutes))
            {
                session.Revoke(now, RevokeReasons.Expired);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                failure = new UnauthenticatedException("SESSION_EXPIRED", "Your session has expired. Please sign in again.");
                return;
            }

            var user = await db.Users.SingleAsync(u => u.Id == record.UserId, ct).ConfigureAwait(false);
            if (!user.IsActive)
            {
                await RevokeFamilyAsync(session.FamilyId, RevokeReasons.UserDeactivated, now, ct).ConfigureAwait(false);
                failure = new UnauthenticatedException("USER_INACTIVE", "Your access has been disabled. Contact your Admin.");
                return;
            }

            session.Revoke(now, RevokeReasons.Rotated);
            outcome = await StartSessionAsync(user, session.FamilyId, session.ExpiresAt, now, ct, rotatedFrom: session)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return failure is null ? outcome! : throw failure;
    }

    /// <summary>Ends the session chain identified by the refresh cookie and/or the caller's access token.</summary>
    public async Task LogoutAsync(string? refreshToken, ICurrentUser caller, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        Guid? org = null, user = null, family = null;

        if (!string.IsNullOrWhiteSpace(refreshToken) && refreshToken.Length <= 200 &&
            await authStore.FindSessionByHashAsync(tokens.HashRefreshToken(refreshToken), ct).ConfigureAwait(false) is { } rec)
        {
            (org, user, family) = (rec.OrganizationId, rec.UserId, rec.FamilyId);
        }
        else if (caller.IsAuthenticated)
        {
            (org, user, family) = (caller.OrganizationId, caller.UserId, caller.FamilyId);
        }

        if (family is null)
        {
            return; // nothing to end; logout is idempotent
        }

        tenant.Set(org!.Value, user!.Value, isAdmin: false);
        await db.InTransactionAsync(async () =>
        {
            var revoked = await RevokeFamilyAsync(family.Value, RevokeReasons.Logout, clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (revoked > 0)
            {
                await audit.WriteAsync(new AuditEntry(org.Value, user.Value, AuditActions.Logout, AuditEntities.Session,
                    family.Value.ToString()), ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The user replaces their PIN (also the mandatory first-login step). Wrong current PINs
    /// count towards lockout, so this endpoint can't be used to guess PINs either.
    /// </summary>
    public async Task ChangePinAsync(ICurrentUser caller, string? currentPin, string? newPin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (db.Database.CurrentTransaction is not null)
        {
            // A failed attempt must be committed before the error is returned; an ambient
            // request transaction would roll it back and make PIN guessing free.
            throw new InvalidOperationException("ChangePinAsync manages its own transaction.");
        }

        Exception? failure = null;
        await db.InTransactionAsync(async () =>
        {
            var now = clock.GetUtcNow();
            var s = await settings.GetAsync(ct).ConfigureAwait(false);
            var user = await db.Users.SingleAsync(u => u.Id == caller.UserId, ct).ConfigureAwait(false);

            var window = await authStore.GetRecentFailuresAsync(user.MobileE164,
                Later(now.AddMinutes(-s.PinLockoutMinutes), user.PinSetAt), s.PinMaxFailures, ct).ConfigureAwait(false);
            if (window.Failures >= s.PinMaxFailures)
            {
                failure = new TooManyAttemptsException(RetryAfter(window, s, now));
                return;
            }

            if (!PinPolicy.IsWellFormed(currentPin) || user.PinHash is null ||
                hasher.Verify(user.PinHash, currentPin!) == PinVerification.Failed)
            {
                await authStore.RecordAttemptAsync(user.MobileE164, user.Id, false, "PIN_CHANGE_BAD", ct).ConfigureAwait(false);
                failure = new DomainException("CURRENT_PIN_INCORRECT", "Your current PIN is incorrect.");
                return;
            }

            if (PinPolicy.Check(newPin, user.MobileE164) is not null || newPin == currentPin)
            {
                failure = newPin == currentPin
                    ? new DomainException("PIN_UNCHANGED", "Choose a PIN different from your current one.")
                    : CaptureRejection(newPin, user.MobileE164);
                return;
            }
            user.PinHash = hasher.Hash(newPin!);
            user.PinSetAt = now;
            user.PinMustChange = false;
            user.UpdatedBy = user.Id;

            // Every other device is signed out; this one stays signed in.
            var others = await db.UserSessions
                .Where(x => x.UserId == user.Id && x.FamilyId != caller.FamilyId && x.RevokedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            others.ForEach(x => x.Revoke(now, RevokeReasons.PinChanged));

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(user.OrganizationId, user.Id, AuditActions.PinChanged, AuditEntities.User,
                user.Id.ToString(), NewValue: new { otherSessionsSignedOut = others.Select(o => o.FamilyId).Distinct().Count() }), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static DomainException CaptureRejection(string? pin, string mobileE164)
    {
        try
        {
            PinPolicy.EnsureAcceptable(pin, mobileE164);
            return new DomainException("PIN_TOO_WEAK", "Choose a less predictable PIN.");
        }
        catch (DomainException ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Per-request check behind every access token: the user is active and the token's
    /// session chain has not been ended (logout, reset, deactivation, theft). Returns
    /// null when the token must be rejected.
    /// </summary>
    public async Task<(UserRole Role, bool PinMustChange)?> ValidateAccessAsync(
        Guid organizationId, Guid userId, Guid familyId, CancellationToken ct)
    {
        tenant.Set(organizationId, userId, isAdmin: false);
        return await db.InTransactionAsync<(UserRole, bool)?>(async () =>
        {
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Role, u.Status, u.PinMustChange })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (user is null || user.Status != UserStatus.Active)
            {
                return null;
            }

            var ended = await db.UserSessions.AsNoTracking().AnyAsync(x =>
                x.FamilyId == familyId && x.RevokedAt != null && x.RevokeReason != RevokeReasons.Rotated, ct).ConfigureAwait(false);
            return ended ? null : (user.Role, user.PinMustChange);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Ends every session chain of a user (deactivation, PIN reset, admin action).</summary>
    public async Task<int> RevokeAllForUserAsync(Guid userId, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var open = await db.UserSessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(ct).ConfigureAwait(false);
        open.ForEach(x => x.Revoke(now, reason));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return open.Select(x => x.FamilyId).Distinct().Count();
    }

    private async Task LoginUnknownNumberAsync(string e164, string pin, CancellationToken ct)
    {
        // No tenant: login_attempts is the only table touched, and it has no RLS.
        tenant.Clear();
        var defaults = OrganizationSettings.Defaults;
        AppException failure = InvalidCredentials();
        await db.InTransactionAsync(async () =>
        {
            var now = clock.GetUtcNow();
            var window = await authStore.GetRecentFailuresAsync(e164, now.AddMinutes(-defaults.PinLockoutMinutes),
                defaults.PinMaxFailures, ct).ConfigureAwait(false);
            hasher.VerifyDummy(pin);
            if (window.Failures >= defaults.PinMaxFailures)
            {
                await authStore.RecordAttemptAsync(e164, null, false, "LOCKED", ct).ConfigureAwait(false);
                failure = new TooManyAttemptsException(RetryAfter(window, defaults, now));
                return;
            }

            await authStore.RecordAttemptAsync(e164, null, false, "UNKNOWN", ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        throw failure;
    }

    private async Task<AuthOutcome> StartSessionAsync(
        User user, Guid familyId, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct, UserSession? rotatedFrom = null)
    {
        var (refreshToken, hash) = tokens.NewRefreshToken();
        var session = new UserSession
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = user.OrganizationId,
            UserId = user.Id,
            FamilyId = familyId,
            RefreshTokenHash = hash,
            AuthMethod = AuthMethod.Pin,
            UserAgent = Truncate(request.UserAgent, 500),
            DeviceLabel = DeviceLabelFor(request.UserAgent),
            IpAddress = request.IpAddress,
            CreatedAt = now,
            LastUsedAt = now,
            ExpiresAt = expiresAt,
        };
        db.UserSessions.Add(session);
        if (rotatedFrom is not null)
        {
            // Saved in two steps: the new row must exist before the old one can reference it.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            rotatedFrom.ReplacedBy = session.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AuthOutcome(
            tokens.IssueAccessToken(user, session.Id, familyId),
            (int)tokens.AccessTokenLifetime.TotalSeconds,
            refreshToken,
            expiresAt,
            new SessionProfile(user.Id, user.OrganizationId, user.FullName, user.Role, user.PinMustChange));
    }

    private async Task<int> RevokeFamilyAsync(Guid familyId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.UserSessions.Where(x => x.FamilyId == familyId && x.RevokedAt == null).ToListAsync(ct).ConfigureAwait(false);
        open.ForEach(x => x.Revoke(now, reason));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return open.Count;
    }

    private PinVerification DummyFailure(string pin)
    {
        hasher.VerifyDummy(pin);
        return PinVerification.Failed;
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset? b) => b is { } v && v > a ? v : a;

    private static TimeSpan RetryAfter(FailureWindow window, OrganizationSettings s, DateTimeOffset now)
    {
        var unlockAt = (window.OldestCounted ?? now).AddMinutes(s.PinLockoutMinutes);
        var wait = unlockAt - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(30);
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    /// <summary>A human label for the sessions list, e.g. "Chrome on Android".</summary>
    internal static string? DeviceLabelFor(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        static bool Has(string ua, string token) => ua.Contains(token, StringComparison.OrdinalIgnoreCase);
        var os = Has(userAgent, "Android") ? "Android"
            : Has(userAgent, "iPhone") || Has(userAgent, "iPad") ? "iOS"
            : Has(userAgent, "Windows") ? "Windows"
            : Has(userAgent, "Mac OS") ? "macOS"
            : Has(userAgent, "Linux") ? "Linux" : "Unknown OS";
        var browser = Has(userAgent, "Edg/") ? "Edge"
            : Has(userAgent, "SamsungBrowser") ? "Samsung Internet"
            : Has(userAgent, "Firefox") ? "Firefox"
            : Has(userAgent, "Chrome") ? "Chrome"
            : Has(userAgent, "Safari") ? "Safari" : "Browser";
        return $"{browser} on {os}";
    }
}
