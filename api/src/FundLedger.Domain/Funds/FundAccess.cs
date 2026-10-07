namespace FundLedger.Domain.Funds;

/// <summary>A per-fund capability (TRD §5.2). Admins hold every permission on every fund.</summary>
public enum FundCapability
{
    View,
    MoneyIn,
    MoneyOut,
    Transfer,
    ViewReports,
    Export,
    ViewAllTransactions,
}

/// <summary>A member's access to one fund. Maps to <c>fl.user_fund_access</c>.</summary>
public sealed class UserFundAccess
{
    public Guid OrganizationId { get; init; }

    public Guid UserId { get; init; }

    public Guid FundId { get; init; }

    public bool CanMoneyIn { get; set; } = true;

    public bool CanMoneyOut { get; set; } = true;

    public bool CanTransfer { get; set; }

    public bool CanViewReports { get; set; } = true;

    public bool CanExport { get; set; }

    public bool CanViewAllTxns { get; set; } = true;

    public Guid GrantedBy { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    public bool Allows(FundCapability permission) => permission switch
    {
        FundCapability.View => true,                      // having a row means the fund is visible
        FundCapability.MoneyIn => CanMoneyIn,
        FundCapability.MoneyOut => CanMoneyOut,
        FundCapability.Transfer => CanTransfer,
        FundCapability.ViewReports => CanViewReports,
        FundCapability.Export => CanExport,
        FundCapability.ViewAllTransactions => CanViewAllTxns,
        _ => false,
    };
}
