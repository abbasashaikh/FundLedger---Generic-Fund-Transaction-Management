using System.Globalization;
using System.Text.Json;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Settings;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Ledger;

/// <summary>
/// Accountability (PRD §14): editing, cancelling, correcting by adjustment, and the per-entry history.
///
/// Rules (TRD §7.4): a Member may edit their OWN entry inside the edit window (default 15 min); an Admin
/// may correct any ACTIVE entry but must always give a reason; nobody can edit a cancelled entry or an
/// entry in a non-active fund. Edits and cancellations must name the revision they saw (If-Match), so a
/// stale screen can never silently overwrite someone else's change. Every change writes a new revision
/// snapshot and an audit event with the old and new values, in the same DB transaction.
/// </summary>
public sealed partial class LedgerService
{
    // ---- adjustment (Admin only, BR-016) -------------------------------------------------
    public async Task<TransactionResult> CreateAdjustmentAsync(CreateAdjustmentRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!caller.IsAdmin)
        {
            throw new ForbiddenException();
        }

        return await CreateAsync(r.FundId, FundCapability.View, TxnType.Adjustment, r.Amount, r.TxnDate, r.TxnTime, r.ClientTxnId,
            _ => RequireAccountAsync(r.AccountId, "accountId", ct),
            t =>
            {
                t.AccountId = r.AccountId;
                t.AdjustmentDirection = r.Direction;
                t.Purpose = r.Reason.Trim();      // the mandatory reason is the entry's purpose (shape rule in the DB)
                t.Remarks = Clean(r.Remarks);
            }, ct).ConfigureAwait(false);
    }

    // ---- edit -------------------------------------------------------------------------------
    public async Task<TransactionResult> UpdateAsync(Guid id, int? expectedRevision, UpdateTransactionRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var txn = await db.Transactions.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var fund = await guard.RequireAsync(txn.FundId, FundCapability.View, ct).ConfigureAwait(false);
        if (await OwnOnlyAsync(txn.FundId, ct).ConfigureAwait(false) && txn.CreatedBy != caller.UserId)
        {
            throw new NotFoundException();
        }

        if (expectedRevision is null)
        {
            throw new PreconditionRequiredException();
        }

        if (txn.Status == TxnStatus.Cancelled)
        {
            throw new ConflictException("TXN_CANCELLED_IMMUTABLE", "A cancelled transaction can't be changed.");
        }

        if (fund.Status != FundStatus.Active)
        {
            throw new ConflictException("FUND_NOT_ACTIVE", "This fund is not active. Entries can't be changed.");
        }

        var s = await settings.GetAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var rights = await RightsAsync(txn.TxnType, txn.CreatedBy, txn.CreatedAt, txn.FundId, now, s, ct).ConfigureAwait(false);
        if (!rights.Allowed)
        {
            throw new ForbiddenException(rights.DenyCode ?? "FORBIDDEN", rights.DenyMessage ?? "You don't have permission to do this.");
        }

        if (txn.Revision != expectedRevision)
        {
            throw new PreconditionFailedException();
        }

        if (rights.RequiresReason && string.IsNullOrWhiteSpace(r.Reason))
        {
            throw new DomainException("REASON_REQUIRED", "Please give a reason for this change.");
        }

        var before = await GetDtoAsync(id, ct).ConfigureAwait(false);
        await ApplyEditAsync(txn, r, s, ct).ConfigureAwait(false);

        if (!db.ChangeTracker.HasChanges())
        {
            return new TransactionResult(before, await ClosingAsync(txn.FundId, ct).ConfigureAwait(false), Duplicate: false);   // nothing changed: no new revision
        }

        txn.UpdatedBy = caller.UserId;
        txn.UpdatedAt = now;
        await SaveChangeAsync(ct).ConfigureAwait(false);

        var after = await GetDtoAsync(id, ct).ConfigureAwait(false);
        var changes = Diff(before, after);
        var reason = r.Reason?.Trim();
        await WriteRevisionAsync(after, reason, ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.TxnUpdated, AuditEntities.Transaction, id.ToString(),
            changes.ToDictionary(c => c.Field, c => c.Old), changes.ToDictionary(c => c.Field, c => c.New), reason, txn.FundId), ct).ConfigureAwait(false);
        return new TransactionResult(after, await ClosingAsync(txn.FundId, ct).ConfigureAwait(false), Duplicate: false);
    }

    private async Task ApplyEditAsync(Transaction txn, UpdateTransactionRequest r, OrganizationSettings s, CancellationToken ct)
    {
        var amount = Money.Parse(r.Amount);
        if (amount.Amount <= 0m || amount.Amount > s.MaxAmount)
        {
            throw new DomainException("AMOUNT_OUT_OF_RANGE", $"Amount must be between 0.01 and {IndianNumberFormat.Format(s.MaxAmount, includeDecimals: false)}.");
        }

        if (!TxnRules.TryParseTime(r.TxnTime, out var time))
        {
            throw ValidationFailedException.For("txnTime", "Enter the time as HH:mm.");
        }

        var (today, nowLocal) = await OrgNowAsync(ct).ConfigureAwait(false);
        if (r.TxnDate > today || (r.TxnDate == today && time > TimeOnly.FromDateTime(nowLocal).AddMinutes(5)))
        {
            throw new DomainException("DATE_IN_FUTURE", "The date and time can't be in the future.");
        }

        if (!caller.IsAdmin && r.TxnDate != txn.TxnDate && today.DayNumber - r.TxnDate.DayNumber > s.BackdateDaysMember)
        {
            throw new DomainException("BACKDATE_LIMIT_EXCEEDED", $"Entries can be dated at most {s.BackdateDaysMember} days back. Ask an Admin.");
        }

        txn.Amount = amount.Amount;
        txn.TxnDate = r.TxnDate;
        txn.TxnTime = time;
        txn.Remarks = Clean(r.Remarks);
        txn.ReferenceNumber = Clean(r.ReferenceNumber);

        switch (txn.TxnType)
        {
            case TxnType.Deposit:
            case TxnType.Expense:
                var direction = txn.TxnType == TxnType.Deposit ? CategoryDirection.MoneyIn : CategoryDirection.MoneyOut;
                var categoryId = r.CategoryId ?? throw ValidationFailedException.For("categoryId", "Choose a category.");
                var accountId = r.AccountId ?? throw ValidationFailedException.For("accountId", "Choose an account.");
                var modeId = r.PaymentModeId ?? throw ValidationFailedException.For("paymentModeId", "Choose a payment mode.");
                await RequireCategoryAsync(categoryId, direction, txn.FundId, ct, existing: txn.CategoryId).ConfigureAwait(false);
                await RequireAccountAsync(accountId, "accountId", ct, existing: txn.AccountId).ConfigureAwait(false);
                await RequirePaymentModeAsync(modeId, r.ReferenceNumber, ct, existing: txn.PaymentModeId).ConfigureAwait(false);
                RequirePurpose(r.Purpose);
                (txn.CategoryId, txn.AccountId, txn.PaymentModeId) = (categoryId, accountId, modeId);
                txn.Purpose = r.Purpose!.Trim();
                if (txn.TxnType == TxnType.Deposit)
                {
                    txn.ReceivedFrom = Clean(r.ReceivedFrom);
                }
                else
                {
                    txn.PaidTo = Clean(r.PaidTo);
                }

                break;

            case TxnType.Transfer:
                var from = r.FromAccountId ?? throw ValidationFailedException.For("fromAccountId", "Choose the source account.");
                var to = r.ToAccountId ?? throw ValidationFailedException.For("toAccountId", "Choose the destination account.");
                if (from == to)
                {
                    throw new DomainException("TRANSFER_SAME_ACCOUNT", "Choose two different accounts.");
                }

                await RequireAccountAsync(from, "fromAccountId", ct, existing: txn.FromAccountId).ConfigureAwait(false);
                await RequireAccountAsync(to, "toAccountId", ct, existing: txn.ToAccountId).ConfigureAwait(false);
                if (r.PaymentModeId is { } pm)
                {
                    await RequirePaymentModeAsync(pm, r.ReferenceNumber, ct, existing: txn.PaymentModeId).ConfigureAwait(false);
                }

                RequirePurpose(r.Purpose);
                (txn.FromAccountId, txn.ToAccountId, txn.PaymentModeId) = (from, to, r.PaymentModeId);
                txn.Purpose = r.Purpose!.Trim();
                break;

            case TxnType.Adjustment:
                var adjAccount = r.AccountId ?? throw ValidationFailedException.For("accountId", "Choose an account.");
                var dir = r.AdjustmentDirection ?? throw ValidationFailedException.For("adjustmentDirection", "Choose increase or decrease.");
                await RequireAccountAsync(adjAccount, "accountId", ct, existing: txn.AccountId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(r.Purpose) || r.Purpose.Trim().Length < 10)
                {
                    throw ValidationFailedException.For("purpose", "Explain the adjustment in at least 10 characters.");
                }

                (txn.AccountId, txn.AdjustmentDirection) = (adjAccount, dir);
                txn.Purpose = r.Purpose.Trim();
                break;

            default:
                throw new InvalidOperationException("Unknown transaction type.");
        }
    }

    private static void RequirePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw ValidationFailedException.For("purpose", "Enter the purpose.");
        }
    }

    // ---- cancel (Admin only, Q-04) ------------------------------------------------------------
    public async Task<TransactionResult> CancelAsync(Guid id, int? expectedRevision, CancelTransactionRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!caller.IsAdmin)
        {
            throw new ForbiddenException();
        }

        var txn = await db.Transactions.SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var fund = await guard.RequireAsync(txn.FundId, FundCapability.View, ct).ConfigureAwait(false);
        if (expectedRevision is null)
        {
            throw new PreconditionRequiredException();
        }

        if (txn.Status == TxnStatus.Cancelled)
        {
            throw new ConflictException("TXN_CANCELLED_IMMUTABLE", "This transaction is already cancelled.");
        }

        if (fund.Status != FundStatus.Active)
        {
            throw new ConflictException("FUND_NOT_ACTIVE", "This fund is not active. Entries can't be cancelled.");
        }

        if (txn.Revision != expectedRevision)
        {
            throw new PreconditionFailedException();
        }

        var reason = r.Reason.Trim();
        if (reason.Length < 5)
        {
            throw new DomainException("REASON_REQUIRED", "Give a reason of at least 5 characters.");
        }

        var now = clock.GetUtcNow();
        (txn.Status, txn.CancelledBy, txn.CancelledAt, txn.CancellationReason) = (TxnStatus.Cancelled, caller.UserId, now, reason);
        (txn.UpdatedBy, txn.UpdatedAt) = (caller.UserId, now);
        await SaveChangeAsync(ct).ConfigureAwait(false);

        var after = await GetDtoAsync(id, ct).ConfigureAwait(false);
        await WriteRevisionAsync(after, reason, ct).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, AuditActions.TxnCancelled, AuditEntities.Transaction, id.ToString(),
            new { status = "Active" }, new { status = "Cancelled", after.TxnNumber, after.Amount }, reason, txn.FundId), ct).ConfigureAwait(false);
        return new TransactionResult(after, await ClosingAsync(txn.FundId, ct).ConfigureAwait(false), Duplicate: false);
    }

    private async Task SaveChangeAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request changed the entry between our read and write (the UPDATE is guarded by revision).
            throw new PreconditionFailedException();
        }
    }

    // ---- history ----------------------------------------------------------------------------
    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid id, CancellationToken ct)
    {
        _ = await GetAsync(id, ct).ConfigureAwait(false);   // same visibility rules (404 for anything the caller can't see)

        var revisions = await db.TransactionRevisions.AsNoTracking().Where(r => r.TransactionId == id).OrderBy(r => r.Revision).ToListAsync(ct).ConfigureAwait(false);
        var userIds = revisions.Select(r => r.ChangedBy).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct).ConfigureAwait(false);

        var entries = new List<HistoryEntry>();
        TransactionDto? previous = null;
        foreach (var rev in revisions)
        {
            var snapshot = JsonSerializer.Deserialize<TransactionDto>(rev.Snapshot, Json);
            if (snapshot is null)
            {
                continue;
            }

            var who = new PersonRef(rev.ChangedBy, names.GetValueOrDefault(rev.ChangedBy, "Unknown"));
            var kind = previous is null ? HistoryKind.Created : snapshot.Status == TxnStatus.Cancelled ? HistoryKind.Cancelled : HistoryKind.Edited;
            entries.Add(new HistoryEntry(rev.Revision, kind, who, rev.ChangedAt, rev.ChangeReason, previous is null ? [] : Diff(previous, snapshot)));
            previous = snapshot;
        }

        entries.Reverse();   // newest first
        return entries;
    }

    /// <summary>Field-level differences between two versions, as display strings (names, not ids).</summary>
    internal static List<FieldChange> Diff(TransactionDto a, TransactionDto b)
    {
        var changes = new List<FieldChange>();
        void Compare(string field, string? x, string? y)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(field, x, y));
            }
        }

        Compare("amount", a.Amount, b.Amount);
        Compare("date", a.TxnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), b.TxnDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Compare("time", a.TxnTime, b.TxnTime);
        Compare("category", a.Category?.Name, b.Category?.Name);
        Compare("account", a.Account?.Name, b.Account?.Name);
        Compare("fromAccount", a.FromAccount?.Name, b.FromAccount?.Name);
        Compare("toAccount", a.ToAccount?.Name, b.ToAccount?.Name);
        Compare("paymentMode", a.PaymentMode?.Name, b.PaymentMode?.Name);
        Compare("adjustmentDirection", a.AdjustmentDirection?.ToString(), b.AdjustmentDirection?.ToString());
        Compare("receivedFrom", a.ReceivedFrom, b.ReceivedFrom);
        Compare("paidTo", a.PaidTo, b.PaidTo);
        Compare("purpose", a.Purpose, b.Purpose);
        Compare("referenceNumber", a.ReferenceNumber, b.ReferenceNumber);
        Compare("remarks", a.Remarks, b.Remarks);
        Compare("status", a.Status.ToString(), b.Status.ToString());
        return changes;
    }

    // ---- what may the caller do? -------------------------------------------------------------
    private sealed record Rights(bool Allowed, bool RequiresReason, DateTimeOffset? EditableUntil, string? DenyCode, string? DenyMessage);

    private async Task<Rights> RightsAsync(
        TxnType type, Guid createdBy, DateTimeOffset createdAt, Guid fundId, DateTimeOffset now, OrganizationSettings s, CancellationToken ct)
    {
        if (caller.IsAdmin)
        {
            return new Rights(true, RequiresReason: true, EditableUntil: null, null, null);   // Admins always explain their changes (TRD §7.4)
        }

        var deny = new Rights(false, false, null, "FORBIDDEN", "You don't have permission to do this.");
        if (type == TxnType.Adjustment || createdBy != caller.UserId)
        {
            return deny;
        }

        var access = await db.UserFundAccess.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == caller.UserId && a.FundId == fundId, ct).ConfigureAwait(false);
        var capability = type switch
        {
            TxnType.Deposit => FundCapability.MoneyIn,
            TxnType.Expense => FundCapability.MoneyOut,
            TxnType.Transfer => FundCapability.Transfer,
            _ => FundCapability.View,
        };
        if (access is null || !access.Allows(capability))
        {
            return deny;
        }

        var until = createdAt.AddMinutes(s.EditWindowMinutes);
        return now <= until
            ? new Rights(true, RequiresReason: false, until, null, null)
            : new Rights(false, true, until, "EDIT_WINDOW_EXPIRED", "The editing window has ended. Ask an Admin to correct it.");
    }

    private async Task<TransactionDetail> DetailForAsync(TransactionDto dto, CancellationToken ct)
    {
        var fund = await db.Funds.AsNoTracking().SingleAsync(f => f.Id == dto.FundId, ct).ConfigureAwait(false);
        var open = dto.Status == TxnStatus.Active && fund.Status == FundStatus.Active;
        var rights = await RightsAsync(dto.Type, dto.CreatedBy.Id, dto.CreatedAt, dto.FundId, clock.GetUtcNow(), await settings.GetAsync(ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
        return new TransactionDetail(dto, open && rights.Allowed, open && caller.IsAdmin, open && rights.Allowed ? rights.EditableUntil : null, rights.RequiresReason);
    }
}
