namespace FundLedger.Domain.Users;

public enum UserRole
{
    Admin,
    Member,
}

public enum UserStatus
{
    Active,
    Inactive,
}

/// <summary>A person allowed to sign in (PRD §4–5). Maps to <c>fl.users</c>. Never deleted.</summary>
public sealed class User
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public required string FullName { get; set; }

    public required string MobileE164 { get; set; }

    public string? Email { get; set; }

    public UserRole Role { get; set; } = UserRole.Member;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public string? PinHash { get; set; }

    public DateTimeOffset? PinSetAt { get; set; }

    public bool PinMustChange { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset? DeactivatedAt { get; set; }

    public Guid? DeactivatedBy { get; set; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>: optimistic concurrency for admin edits.</summary>
    public uint Version { get; set; }

    public bool IsAdmin => Role == UserRole.Admin;

    public bool IsActive => Status == UserStatus.Active;
}
