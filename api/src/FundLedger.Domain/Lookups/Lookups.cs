namespace FundLedger.Domain.Lookups;

/// <summary>Org-configurable fund classification (Event, Charity, Masjid, ...). Maps to <c>fl.fund_types</c>.</summary>
public sealed class FundType
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }
}

/// <summary>Cash, UPI, Bank Transfer, ... Maps to <c>fl.payment_modes</c>.</summary>
public sealed class PaymentMode
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public required string Name { get; set; }

    /// <summary>When true, transactions using this mode must carry a reference number (UPI id, cheque no.).</summary>
    public bool RequiresReference { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }
}

public enum CategoryDirection
{
    MoneyIn,
    MoneyOut,
}

/// <summary>A transaction category. <see cref="FundId"/> null = available to every fund. Maps to <c>fl.categories</c>.</summary>
public sealed class Category
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid? FundId { get; set; }

    public CategoryDirection Direction { get; init; }

    public required string Name { get; set; }

    public string? Icon { get; set; }

    public bool IsActive { get; set; } = true;

    public short SortOrder { get; set; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
