namespace FundLedger.Domain.Ledger;

public enum TxnType
{
    Deposit,
    Expense,
    Transfer,
    Adjustment,
}

public enum TxnStatus
{
    Active,
    Cancelled,
}

public enum AdjustmentDirection
{
    Increase,
    Decrease,
}

/// <summary>
/// One financial event (PRD §7). Maps to <c>fl.transactions</c>. Never deleted (BR-012);
/// the database also enforces the per-type shape rules, so the service sets exactly the
/// fields its type requires.
/// </summary>
public sealed class Transaction
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid FundId { get; init; }

    public required string TxnNumber { get; init; }

    public TxnType TxnType { get; init; }

    public decimal Amount { get; set; }

    public DateOnly TxnDate { get; set; }

    public TimeOnly TxnTime { get; set; }

    public Guid? CategoryId { get; set; }

    public Guid? AccountId { get; set; }

    public Guid? FromAccountId { get; set; }

    public Guid? ToAccountId { get; set; }

    public AdjustmentDirection? AdjustmentDirection { get; set; }

    public Guid? PaymentModeId { get; set; }

    public string? ReceivedFrom { get; set; }

    public string? PaidTo { get; set; }

    public string? Purpose { get; set; }

    public string? ReferenceNumber { get; set; }

    public string? Remarks { get; set; }

    public TxnStatus Status { get; set; } = TxnStatus.Active;

    public Guid? ClientTxnId { get; init; }

    public string Source { get; init; } = "ONLINE";

    public DateTimeOffset? ClientCreatedAt { get; init; }

    public int Revision { get; set; } = 1;

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? CancelledBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }
}
