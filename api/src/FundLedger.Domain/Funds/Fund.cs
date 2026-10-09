namespace FundLedger.Domain.Funds;

public enum FundStatus
{
    Draft,
    Active,
    Closed,
    Archived,
}

/// <summary>A fund/activity (PRD §6.2). Maps to <c>fl.funds</c>. Full behaviour arrives in Phase 2.</summary>
public sealed class Fund
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid FundTypeId { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateOnly? StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public FundStatus Status { get; set; } = FundStatus.Draft;

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedBy { get; set; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
