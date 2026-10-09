namespace FundLedger.Domain.Ledger;

/// <summary>
/// A full snapshot of a transaction as it was AFTER one revision (create, edit or cancel), with the
/// reason and who made it. Append-only (BR-014); maps to <c>fl.transaction_revisions</c>.
/// </summary>
public sealed class TransactionRevision
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid TransactionId { get; init; }

    public int Revision { get; init; }

    /// <summary>The transaction as JSON (jsonb).</summary>
    public required string Snapshot { get; init; }

    public string? ChangeReason { get; init; }

    public Guid ChangedBy { get; init; }

    public DateTimeOffset ChangedAt { get; init; }
}
