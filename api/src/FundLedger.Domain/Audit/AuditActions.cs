namespace FundLedger.Domain.Audit;

/// <summary>Audit action codes (App Flow §8). Stable strings: reports and alerts depend on them.</summary>
public static class AuditActions
{
    public const string Login = "LOGIN";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string Logout = "LOGOUT";
    public const string SessionRevoked = "SESSION_REVOKED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string PinChanged = "PIN_CHANGED";
    public const string PinReset = "PIN_RESET";

    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string UserActivated = "USER_ACTIVATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string FundAccessChanged = "FUND_ACCESS_CHANGED";

    public const string OrganizationCreated = "ORGANIZATION_CREATED";
}

public static class AuditEntities
{
    public const string User = "User";
    public const string Session = "Session";
    public const string Organization = "Organization";
}
