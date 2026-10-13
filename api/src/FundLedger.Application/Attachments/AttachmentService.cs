using System.Globalization;
using System.Security.Cryptography;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Ledger;
using FundLedger.Application.Settings;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Attachments;

/// <summary>Where the bytes live (a private bucket in production, TRD §9). Keys are chosen by the service, never by the client.</summary>
public interface IObjectStorage
{
    /// <summary>False when no storage is set up on this server; uploads then answer 503 instead of failing later.</summary>
    bool IsConfigured { get; }

    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>A direct, short-lived link when the store can make one (TR-053); null when the API must stream the file itself.</summary>
    Uri? CreateDownloadUrl(string key, TimeSpan lifetime, string fileName, string contentType);
}

public sealed record ProcessedImage(byte[] Content, string ContentType, int Width, int Height);

/// <summary>Decodes and re-encodes an image (TR-051): the result carries no EXIF, GPS or other metadata, and is no larger than <c>maxEdge</c>.</summary>
public interface IImageProcessor
{
    /// <exception cref="UnsupportedMediaException">The bytes are not a readable image, or are absurdly large.</exception>
    ProcessedImage Reencode(byte[] data, string contentType, int maxEdge);
}

public sealed record AttachmentDto(Guid Id, string FileName, string ContentType, int SizeBytes, PersonRef UploadedBy, DateTimeOffset UploadedAt, bool IsHidden, string? HiddenReason);

public sealed record HideAttachmentRequest(string Reason);

/// <summary>Either a link to redirect to, or the bytes to stream.</summary>
public sealed record AttachmentDownload(string FileName, string ContentType, Uri? Url, Stream? Content);

/// <summary>
/// Files attached to transactions (PRD §18, TRD §9): checked by content, images re-encoded, stored privately, shown only to
/// people who can see the transaction, and never deleted: an Admin hides with a reason (TR-054).
/// </summary>
public sealed class AttachmentService(
    IFundLedgerDb db, ICurrentUser caller, LedgerService ledger, ISettingsProvider settings, IObjectStorage storage, IImageProcessor images, IAuditWriter audit, TimeProvider clock)
{
    public const int MaxPerTransaction = 5;
    public const int ImageLongEdge = 2000;
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromSeconds(60);

    public async Task<AttachmentDto> UploadAsync(Guid transactionId, string? fileName, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!storage.IsConfigured)
        {
            throw new ServiceUnavailableException("STORAGE_NOT_CONFIGURED", "Attaching files isn't set up on this server yet.");
        }

        // Visible to the caller, or 404 (IDOR-safe); then the stricter rule for adding a file.
        var detail = await ledger.GetAsync(transactionId, ct).ConfigureAwait(false);
        var txn = detail.Transaction;
        if (!caller.IsAdmin && txn.CreatedBy.Id != caller.UserId)
        {
            throw new ForbiddenException();
        }

        if (txn.Status == TxnStatus.Cancelled)
        {
            throw new ConflictException("TXN_CANCELLED_IMMUTABLE", "A cancelled entry can't be changed.");
        }

        var fund = await db.Funds.AsNoTracking().SingleAsync(f => f.Id == txn.FundId, ct).ConfigureAwait(false);
        if (fund.Status != FundStatus.Active)
        {
            throw new ConflictException("FUND_NOT_ACTIVE", "This fund is not active. Files can't be added.");
        }

        var existing = await db.TransactionAttachments.AsNoTracking().CountAsync(a => a.TransactionId == transactionId && !a.IsHidden, ct).ConfigureAwait(false);
        if (existing >= MaxPerTransaction)
        {
            throw new ConflictException("ATTACHMENT_LIMIT", $"An entry can have at most {MaxPerTransaction} files.");
        }

        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        var maxBytes = s.AttachmentMaxMb * 1024 * 1024;
        var bytes = await ReadLimitedAsync(content, maxBytes, s.AttachmentMaxMb, ct).ConfigureAwait(false);

        var type = Sniff(bytes) ?? throw new UnsupportedMediaException("Only photos (JPEG, PNG, WebP) and PDF files can be attached.");
        var stored = bytes;
        if (type != "application/pdf")
        {
            var processed = images.Reencode(bytes, type, ImageLongEdge);
            (stored, type) = (processed.Content, processed.ContentType);
        }

        var id = Guid.CreateVersion7();
        var key = string.Create(CultureInfo.InvariantCulture, $"org/{caller.OrganizationId}/fund/{txn.FundId}/txn/{txn.Id}/{id}.{Extension(type)}");
        await storage.PutAsync(key, stored, type, ct).ConfigureAwait(false);

        var row = new TransactionAttachment
        {
            Id = id, OrganizationId = caller.OrganizationId, TransactionId = txn.Id, StorageKey = key, OriginalFilename = CleanName(fileName, type), ContentType = type,
            SizeBytes = stored.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(stored)), UploadedBy = caller.UserId, UploadedAt = clock.GetUtcNow(),
        };
        db.TransactionAttachments.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.AttachmentAdded, AuditEntities.Transaction, txn.Id.ToString(),
            NewValue: new { attachmentId = id, row.OriginalFilename, row.ContentType, row.SizeBytes }, FundId: txn.FundId), ct).ConfigureAwait(false);

        return await ToDtoAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid transactionId, CancellationToken ct)
    {
        await ledger.GetAsync(transactionId, ct).ConfigureAwait(false);               // 404 unless the caller can see the entry
        var rows = await db.TransactionAttachments.AsNoTracking().Where(a => a.TransactionId == transactionId && (caller.IsAdmin || !a.IsHidden))
            .OrderBy(a => a.UploadedAt).ToListAsync(ct).ConfigureAwait(false);
        var people = await PeopleAsync(rows.Select(r => r.UploadedBy), ct).ConfigureAwait(false);
        return rows.Select(r => ToDto(r, people)).ToList();
    }

    public async Task<AttachmentDownload> DownloadAsync(Guid attachmentId, CancellationToken ct)
    {
        var row = await db.TransactionAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await ledger.GetAsync(row.TransactionId, ct).ConfigureAwait(false);          // same visibility as the entry itself
        if (row.IsHidden && !caller.IsAdmin)
        {
            throw new NotFoundException();
        }

        var link = storage.CreateDownloadUrl(row.StorageKey, LinkLifetime, row.OriginalFilename, row.ContentType);
        if (link is not null)
        {
            return new AttachmentDownload(row.OriginalFilename, row.ContentType, link, null);
        }

        var stream = await storage.OpenReadAsync(row.StorageKey, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        return new AttachmentDownload(row.OriginalFilename, row.ContentType, null, stream);
    }

    public async Task<AttachmentDto> HideAsync(Guid attachmentId, HideAttachmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!caller.IsAdmin)
        {
            throw new ForbiddenException();
        }

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length < 5 || reason.Length > 200)
        {
            throw ValidationFailedException.For("reason", "Give a reason of 5 to 200 characters.");
        }

        var row = await db.TransactionAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        if (!row.IsHidden)
        {
            row.IsHidden = true;
            row.HiddenBy = caller.UserId;
            row.HiddenReason = reason;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var fundId = await db.Transactions.AsNoTracking().Where(t => t.Id == row.TransactionId).Select(t => t.FundId).SingleAsync(ct).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.AttachmentHidden, AuditEntities.Transaction, row.TransactionId.ToString(),
                OldValue: new { attachmentId, hidden = false }, NewValue: new { attachmentId, hidden = true, row.OriginalFilename }, Reason: reason, FundId: fundId), ct).ConfigureAwait(false);
        }

        return await ToDtoAsync(row, ct).ConfigureAwait(false);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------
    private static async Task<byte[]> ReadLimitedAsync(Stream input, int maxBytes, int maxMb, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new PayloadTooLargeException($"This file is larger than {maxMb} MB.");
            }
        }

        if (buffer.Length == 0)
        {
            throw ValidationFailedException.For("file", "Choose a file to attach.");
        }

        return buffer.ToArray();
    }

    /// <summary>The real type of the bytes, by their first bytes (TR-051); the client's Content-Type and file name are not trusted.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> b) => b switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => "application/pdf",
        _ => null,
    };

    private static string Extension(string type) => type switch
    {
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/webp" => "webp",
        _ => "pdf",
    };

    /// <summary>A safe display name: no path, no control characters, and an extension that matches the real type.</summary>
    internal static string CleanName(string? name, string type)
    {
        var baseName = Path.GetFileNameWithoutExtension(name ?? string.Empty).Replace('\\', '_').Replace('/', '_');
        baseName = new string(baseName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (baseName.Length == 0)
        {
            baseName = "attachment";
        }

        if (baseName.Length > 150)
        {
            baseName = baseName[..150];
        }

        return $"{baseName}.{Extension(type)}";
    }

    private async Task<Dictionary<Guid, string>> PeopleAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct).ConfigureAwait(false);
    }

    private async Task<AttachmentDto> ToDtoAsync(TransactionAttachment row, CancellationToken ct) => ToDto(row, await PeopleAsync([row.UploadedBy], ct).ConfigureAwait(false));

    private AttachmentDto ToDto(TransactionAttachment r, Dictionary<Guid, string> people) =>
        new(r.Id, r.OriginalFilename, r.ContentType, r.SizeBytes, new PersonRef(r.UploadedBy, people.GetValueOrDefault(r.UploadedBy, string.Empty)), r.UploadedAt,
            r.IsHidden, caller.IsAdmin ? r.HiddenReason : null);
}
