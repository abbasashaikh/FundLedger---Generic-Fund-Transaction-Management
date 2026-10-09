namespace FundLedger.Application.Abstractions;

/// <summary>
/// The authenticated caller for the current request or job. The persistence
/// layer copies these values into the PostgreSQL session context
/// (<c>app.org_id</c>, <c>app.user_id</c>, <c>app.is_admin</c>) at the start of
/// every DB transaction, which is what Row-Level Security policies read
/// (TRD TR-002, ADR-0003). Unset values mean "no tenant": RLS returns no rows.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }

    Guid? UserId { get; }

    bool IsAdmin { get; }
}

/// <summary>Mutable implementation, populated by authentication (Phase 1) or by jobs.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? OrganizationId { get; private set; }

    public Guid? UserId { get; private set; }

    public bool IsAdmin { get; private set; }

    public void Set(Guid organizationId, Guid userId, bool isAdmin)
    {
        OrganizationId = organizationId;
        UserId = userId;
        IsAdmin = isAdmin;
    }

    public void Clear()
    {
        OrganizationId = null;
        UserId = null;
        IsAdmin = false;
    }
}
