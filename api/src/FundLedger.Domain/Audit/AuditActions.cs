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

    public const string FundCreated = "FUND_CREATED";
    public const string FundUpdated = "FUND_UPDATED";
    public const string FundActivated = "FUND_ACTIVATED";
    public const string FundClosed = "FUND_CLOSED";
    public const string FundReopened = "FUND_REOPENED";
    public const string FundArchived = "FUND_ARCHIVED";
    public const string OpeningBalanceChanged = "OPENING_BALANCE_CHANGED";

    public const string AccountCreated = "ACCOUNT_CREATED";
    public const string AccountUpdated = "ACCOUNT_UPDATED";
    public const string CategoryCreated = "CATEGORY_CREATED";
    public const string CategoryUpdated = "CATEGORY_UPDATED";
    public const string LookupChanged = "LOOKUP_CHANGED";

    public const string TxnCreated = "TXN_CREATED";
    public const string TxnUpdated = "TXN_UPDATED";
    public const string TxnCancelled = "TXN_CANCELLED";
    public const string AuditExported = "AUDIT_EXPORTED";
    public const string SettingsChanged = "SETTINGS_CHANGED";
    public const string ExportPerformed = "EXPORT_PERFORMED";
    public const string ReceiptGenerated = "RECEIPT_GENERATED";
    public const string OfflineEntryStale = "OFFLINE_ENTRY_STALE";
    public const string OfflineEntryDiscarded = "OFFLINE_ENTRY_DISCARDED";
}

public static class AuditEntities
{
    public const string User = "User";
    public const string Session = "Session";
    public const string Organization = "Organization";
    public const string Fund = "Fund";
    public const string Account = "Account";
    public const string Category = "Category";
    public const string Lookup = "Lookup";
    public const string Transaction = "Transaction";
}
