using FundLedger.Domain.Users;

namespace FundLedger.Application.Abstractions;

/// <summary>
/// The signed-in caller for this request, as re-validated against the database on
/// every request (session not revoked, user active, current role). Never cached
/// beyond the request (TRD TR-006), so deactivation and role changes apply at once.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    Guid OrganizationId { get; }

    Guid SessionId { get; }

    Guid FamilyId { get; }

    UserRole Role { get; }

    bool PinMustChange { get; }

    bool IsAdmin => Role == UserRole.Admin;
}

public sealed class CurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; private set; }

    public Guid UserId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid FamilyId { get; private set; }

    public UserRole Role { get; private set; }

    public bool PinMustChange { get; private set; }

    public void Set(Guid userId, Guid organizationId, Guid sessionId, Guid familyId, UserRole role, bool pinMustChange)
    {
        IsAuthenticated = true;
        UserId = userId;
        OrganizationId = organizationId;
        SessionId = sessionId;
        FamilyId = familyId;
        Role = role;
        PinMustChange = pinMustChange;
    }

    /// <summary>Clears the restriction once the user has chosen their own PIN.</summary>
    public void ClearPinMustChange() => PinMustChange = false;
}
