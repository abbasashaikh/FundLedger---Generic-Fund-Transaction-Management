namespace FundLedger.Domain.Ledger;

/// <summary>
/// Metadata of one file attached to a transaction (TRD §9). The bytes live in private object storage under
/// <see cref="StorageKey"/>. Never deleted (TR-054): an Admin can hide it with a reason. Maps to <c>fl.transaction_attachments</c>.
/// </summary>
public sealed class TransactionAttachment
{
    public Guid Id { get; init; }

    public Guid OrganizationId { get; init; }

    public Guid TransactionId { get; init; }

    public required string StorageKey { get; init; }

    public required string OriginalFilename { get; init; }

    public required string ContentType { get; init; }

    public int SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public Guid UploadedBy { get; init; }

    public DateTimeOffset UploadedAt { get; init; }

    public bool IsHidden { get; set; }

    public Guid? HiddenBy { get; set; }

    public string? HiddenReason { get; set; }
}
