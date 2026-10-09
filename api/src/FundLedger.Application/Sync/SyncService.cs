using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Ledger;
using FundLedger.Application.Settings;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FundLedger.Application.Sync;

/// <summary>One queued entry as the device recorded it (TRD §8.2). <c>ClientCreatedAt</c> is the device clock: kept for audit only.</summary>
public sealed record SyncCommand(
    Guid ClientTxnId, TxnType Type, DateTimeOffset ClientCreatedAt, Guid FundId, string Amount, DateOnly TxnDate, string TxnTime,
    Guid? CategoryId, Guid? AccountId, Guid? FromAccountId, Guid? ToAccountId, Guid? PaymentModeId,
    string? ReceivedFrom, string? PaidTo, string Purpose, string? ReferenceNumber, string? Remarks);

public sealed record SyncRequest(IReadOnlyList<SyncCommand> Items);

public enum SyncOutcome
{
    /// <summary>Inserted; the response carries the server's entry and number.</summary>
    Created,

    /// <summary>The client id was already recorded (BR-019); the existing entry is returned.</summary>
    Duplicate,

    /// <summary>A business rule failed. Never retried automatically: the user fixes or discards it.</summary>
    Rejected,

    /// <summary>A transient server problem; the client backs off and tries again.</summary>
    Retry,
}

public sealed record SyncItemResult(
    Guid ClientTxnId, SyncOutcome Result, TransactionDto? Transaction, string? ErrorCode, string? Message, IReadOnlyDictionary<string, string[]>? FieldErrors);

public sealed record SyncResponse(IReadOnlyList<SyncItemResult> Items);

public sealed record DiscardRequest(Guid ClientTxnId, string? Summary);

/// <summary>
/// <c>POST /sync/transactions</c> (TRD §8.3, ADR-0005): up to 50 queued entries, each processed in its OWN database
/// transaction through the same code as an online entry, so every rule is re-checked against today's state (TR-041).
/// Uniqueness of the client id is the database's job (TR-040): two parallel syncs of one item end with one entry and one
/// <see cref="SyncOutcome.Duplicate"/>, never an error.
/// </summary>
public sealed partial class SyncService(IFundLedgerDb db, ICurrentUser caller, LedgerService ledger, ISettingsProvider settings, IAuditWriter audit, TimeProvider clock, ILogger<SyncService> log)
{
    public const int MaxItems = 50;

    public async Task<SyncResponse> SyncAsync(SyncRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items is not { Count: > 0 and <= MaxItems })
        {
            throw ValidationFailedException.For("items", $"Send between 1 and {MaxItems} entries.");
        }

        var results = new List<SyncItemResult>(request.Items.Count);
        foreach (var command in request.Items)
        {
            results.Add(await ProcessAsync(command, ct).ConfigureAwait(false));
        }

        return new SyncResponse(results);
    }

    /// <summary>Records that the user discarded a rejected offline entry (App Flow §4.8). Best effort from the device; carries no money.</summary>
    public async Task DiscardAsync(DiscardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.OfflineEntryDiscarded, AuditEntities.Transaction, request.ClientTxnId.ToString(),
            NewValue: new { request.ClientTxnId, summary = request.Summary is { Length: > 200 } s ? s[..200] : request.Summary }), ct).ConfigureAwait(false);
    }

    private async Task<SyncItemResult> ProcessAsync(SyncCommand c, CancellationToken ct)
    {
        var invalid = Validate(c);
        if (invalid is not null)
        {
            return Rejected(c, new ValidationFailedException(invalid));
        }

        // First attempt, and one more only when we lost a race for the same client id: by then the winner has committed,
        // so the second pass finds the existing entry and answers DUPLICATE.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await CreateAsync(c, ct).ConfigureAwait(false);
                if (!result.Duplicate)
                {
                    await FlagIfStaleAsync(c, result.Transaction, ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return new SyncItemResult(c.ClientTxnId, result.Duplicate ? SyncOutcome.Duplicate : SyncOutcome.Created, result.Transaction, null, null, null);
            }
            catch (ConflictException conflict) when (attempt == 0 && conflict.Code == "DUPLICATE_CLIENT_TXN")
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AppException { Status: < 500 } or DomainException)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Rejected(c, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogTransient(ex, c.ClientTxnId);
                return new SyncItemResult(c.ClientTxnId, SyncOutcome.Retry, null, "TRANSIENT", "The server couldn't save this just now. It will be tried again.", null);
            }
        }

        return new SyncItemResult(c.ClientTxnId, SyncOutcome.Retry, null, "TRANSIENT", "The server couldn't save this just now. It will be tried again.", null);
    }

    private Task<TransactionResult> CreateAsync(SyncCommand c, CancellationToken ct) => c.Type switch
    {
        TxnType.Deposit => ledger.CreateDepositAsync(new CreateDepositRequest(c.FundId, c.Amount, c.TxnDate, c.TxnTime, c.CategoryId ?? Guid.Empty, c.AccountId ?? Guid.Empty,
            c.PaymentModeId ?? Guid.Empty, c.ReceivedFrom, c.Purpose, c.ReferenceNumber, c.Remarks, c.ClientTxnId), ct, c.ClientCreatedAt),
        TxnType.Expense => ledger.CreateExpenseAsync(new CreateExpenseRequest(c.FundId, c.Amount, c.TxnDate, c.TxnTime, c.CategoryId ?? Guid.Empty, c.AccountId ?? Guid.Empty,
            c.PaymentModeId ?? Guid.Empty, c.PaidTo, c.Purpose, c.ReferenceNumber, c.Remarks, c.ClientTxnId), ct, c.ClientCreatedAt),
        TxnType.Transfer => ledger.CreateTransferAsync(new CreateTransferRequest(c.FundId, c.Amount, c.TxnDate, c.TxnTime, c.FromAccountId ?? Guid.Empty, c.ToAccountId ?? Guid.Empty,
            c.PaymentModeId, c.Purpose, c.ReferenceNumber, c.Remarks, c.ClientTxnId), ct, c.ClientCreatedAt),
        _ => throw ValidationFailedException.For("type", "Only Money In, Money Out and Transfer can be recorded offline."),
    };

    /// <summary>TR-043: an entry that waited longer than the limit still syncs, but an Admin is told.</summary>
    private async Task FlagIfStaleAsync(SyncCommand c, TransactionDto dto, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        var waited = clock.GetUtcNow() - c.ClientCreatedAt;
        if (waited <= TimeSpan.FromHours(s.OfflineMaxQueueAgeHours))
        {
            return;
        }

        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.OfflineEntryStale, AuditEntities.Transaction, dto.Id.ToString(),
            NewValue: new { dto.TxnNumber, waitedHours = (int)waited.TotalHours, limitHours = s.OfflineMaxQueueAgeHours, c.ClientCreatedAt }, FundId: dto.FundId), ct).ConfigureAwait(false);
    }

    /// <summary>Shape checks that the HTTP validator would do for an online entry, reported per field.</summary>
    private static Dictionary<string, string[]>? Validate(SyncCommand c)
    {
        var errors = new Dictionary<string, string[]>();
        if (c.ClientTxnId == Guid.Empty)
        {
            errors["clientTxnId"] = ["A client id is required."];
        }

        if (c.Type is not (TxnType.Deposit or TxnType.Expense or TxnType.Transfer))
        {
            errors["type"] = ["Only Money In, Money Out and Transfer can be recorded offline."];
        }

        if (!decimal.TryParse(c.Amount, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            errors["amount"] = ["Enter an amount greater than zero with at most 2 decimals."];
        }

        if (!TxnRules.TryParseTime(c.TxnTime, out _))
        {
            errors["txnTime"] = ["Enter the time as HH:mm."];
        }

        if (string.IsNullOrWhiteSpace(c.Purpose) || c.Purpose.Length > 300)
        {
            errors["purpose"] = ["Enter the purpose (up to 300 characters)."];
        }

        if (c.Type is TxnType.Deposit or TxnType.Expense && (c.CategoryId is null || c.AccountId is null || c.PaymentModeId is null))
        {
            errors["categoryId"] = ["Choose a category, an account and a payment mode."];
        }

        if (c.Type == TxnType.Transfer && (c.FromAccountId is null || c.ToAccountId is null))
        {
            errors["toAccountId"] = ["Choose two different accounts."];
        }

        return errors.Count == 0 ? null : errors;
    }

    private static SyncItemResult Rejected(SyncCommand c, Exception ex) => ex switch
    {
        ValidationFailedException v => new SyncItemResult(c.ClientTxnId, SyncOutcome.Rejected, null, v.Code, v.Message, v.Errors),
        AppException a => new SyncItemResult(c.ClientTxnId, SyncOutcome.Rejected, null, a.Code, a.Message, null),
        DomainException d => new SyncItemResult(c.ClientTxnId, SyncOutcome.Rejected, null, d.Code, d.Message, null),
        _ => new SyncItemResult(c.ClientTxnId, SyncOutcome.Rejected, null, "REJECTED", "This entry can't be added.", null),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync of {ClientTxnId} failed transiently")]
    private partial void LogTransient(Exception ex, Guid clientTxnId);
}
