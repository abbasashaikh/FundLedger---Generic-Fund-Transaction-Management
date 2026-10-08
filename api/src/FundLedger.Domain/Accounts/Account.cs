namespace FundLedger.Domain.Accounts;

public enum AccountKind
{
    Cash,
    Bank,
    Upi,
    Wallet,
    Other,
}

/// <summary>A money-holding place (ADR-0004: organization-level). Maps to <c>fl.accounts</c>.</summary>
public sealed class Account
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public required string Name { get; set; }

    public AccountKind Kind { get; set; }

    public string? BankName { get; set; }

    /// <summary>Only the last 4 digits are ever stored (privacy).</summary>
    public string? AccountNumberLast4 { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-(fund, account) opening balance (BR-015: Admin only). Maps to <c>fl.opening_balances</c>.</summary>
public sealed class OpeningBalance
{
    public Guid OrganizationId { get; init; }

    public Guid FundId { get; init; }

    public Guid AccountId { get; init; }

    public decimal Amount { get; set; }

    public DateOnly AsOfDate { get; set; }

    public Guid SetBy { get; set; }

    public DateTimeOffset SetAt { get; set; }
}
