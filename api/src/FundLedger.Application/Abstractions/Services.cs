using System.Net;
using FundLedger.Domain.Users;

namespace FundLedger.Application.Abstractions;

/// <summary>PIN hashing via ASP.NET Core Identity's PasswordHasher (ADR-0002). No custom crypto.</summary>
public interface IPinHasher
{
    string Hash(string pin);

    PinVerification Verify(string hash, string pin);

    /// <summary>Burns the same time as a real verification (unknown user) — TRD TR-011.</summary>
    void VerifyDummy(string pin);
}

public enum PinVerification
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>Signs access tokens and creates/hashes refresh tokens (TRD TR-014/015).</summary>
public interface ITokenService
{
    TimeSpan AccessTokenLifetime { get; }

    string IssueAccessToken(User user, Guid sessionId, Guid familyId);

    /// <summary>A new opaque refresh token and its SHA-256 hash (only the hash is stored).</summary>
    (string Token, byte[] Hash) NewRefreshToken();

    byte[] HashRefreshToken(string token);
}

/// <summary>Who/where a request came from, for audit records and session metadata.</summary>
public interface IRequestContext
{
    string? RequestId { get; }

    IPAddress? IpAddress { get; }

    string? UserAgent { get; }
}

/// <summary>Request context for non-HTTP callers (CLI, worker).</summary>
public sealed class SystemRequestContext : IRequestContext
{
    public string? RequestId { get; init; }

    public IPAddress? IpAddress => null;

    public string? UserAgent { get; init; } = "system";
}

/// <summary>An audit record (PRD §14). Values are serialized to JSON; never pass secrets.</summary>
public sealed record AuditEntry(
    Guid OrganizationId,
    Guid? UserId,
    string Action,
    string EntityType,
    string? EntityId,
    object? OldValue = null,
    object? NewValue = null,
    string? Reason = null,
    Guid? FundId = null);

/// <summary>Append-only audit writer. Must be called inside the same DB transaction as the change.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct);
}

/// <summary>Pre-authentication data access (ADR-0002/0003): narrow SECURITY DEFINER lookups and login attempts.</summary>
public interface IAuthStore
{
    Task<AuthUserRecord?> FindUserByMobileAsync(string mobileE164, CancellationToken ct);

    Task<AuthSessionRecord?> FindSessionByHashAsync(byte[] refreshTokenHash, CancellationToken ct);

    /// <summary>Failed attempts for this mobile since <paramref name="since"/> and since its last success.</summary>
    Task<FailureWindow> GetRecentFailuresAsync(string mobileE164, DateTimeOffset since, int take, CancellationToken ct);

    Task RecordAttemptAsync(string mobileE164, Guid? userId, bool succeeded, string? failureCode, CancellationToken ct);
}

public sealed record AuthUserRecord(
    Guid UserId, Guid OrganizationId, UserRole Role, UserStatus Status,
    string? PinHash, DateTimeOffset? PinSetAt, bool PinMustChange);

public sealed record AuthSessionRecord(Guid SessionId, Guid OrganizationId, Guid UserId, Guid FamilyId);

/// <param name="Failures">Failures counted (capped at the requested window size).</param>
/// <param name="OldestCounted">Oldest of the counted failures — the lock lifts lockout-minutes after it.</param>
public sealed record FailureWindow(int Failures, DateTimeOffset? OldestCounted);
