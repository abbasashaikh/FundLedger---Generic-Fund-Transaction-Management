using System.Net;

namespace FundLedger.Domain.Users;

public enum AuthMethod
{
    Pin,
}

/// <summary>
/// One refresh token in a rotation chain (TRD TR-015). Only the SHA-256 hash of the
/// token is stored. <see cref="FamilyId"/> links every rotation since login, so a
/// replayed old token can revoke the whole chain.
/// </summary>
public sealed class UserSession
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid UserId { get; init; }

    public Guid FamilyId { get; init; }

    public required byte[] RefreshTokenHash { get; init; }

    public Guid? ReplacedBy { get; set; }

    public AuthMethod AuthMethod { get; init; } = AuthMethod.Pin;

    public string? DeviceLabel { get; init; }

    public string? UserAgent { get; init; }

    public IPAddress? IpAddress { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokeReason { get; set; }

    public bool IsRevoked => RevokedAt is not null;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is null)
        {
            RevokedAt = now;
            RevokeReason = reason;
        }
    }
}

/// <summary>Why a session ended. Stored in <c>user_sessions.revoke_reason</c> (max 40 chars).</summary>
public static class RevokeReasons
{
    public const string Logout = "LOGOUT";
    public const string Rotated = "ROTATED";
    public const string ReuseDetected = "REUSE_DETECTED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string PinReset = "PIN_RESET";
    public const string PinChanged = "PIN_CHANGED";
    public const string AdminRevoked = "ADMIN_REVOKED";
    public const string Expired = "EXPIRED";
}
