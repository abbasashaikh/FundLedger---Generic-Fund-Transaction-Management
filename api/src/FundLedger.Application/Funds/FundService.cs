using System.Text.RegularExpressions;
using FluentValidation;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Funds;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Application.Funds;

public sealed record FundSummary(
    Guid Id, string Code, string Name, Guid FundTypeId, string FundTypeName, string? Description,
    DateOnly? StartDate, DateOnly? EndDate, FundStatus Status, string ClosingBalance);

public sealed record OpeningBalanceDto(Guid AccountId, string AccountName, bool AccountActive, string Amount, DateOnly? AsOfDate);

public sealed record FundDetail(FundSummary Fund, bool HasTransactions, IReadOnlyList<OpeningBalanceDto> OpeningBalances);

public sealed record SaveFundRequest(
    string Code, string Name, Guid FundTypeId, string? Description, DateOnly? StartDate, DateOnly? EndDate);

public sealed record FundReasonRequest(string? Reason);

public sealed record OpeningBalanceItem(Guid AccountId, string Amount, DateOnly AsOfDate);

public sealed record SetOpeningBalancesRequest(IReadOnlyList<OpeningBalanceItem> Items, string? Reason);

public sealed partial class SaveFundValidator : AbstractValidator<SaveFundRequest>
{
    public SaveFundValidator()
    {
        RuleFor(x => x.Code).Must(c => c is not null && CodeRegex().IsMatch(c.Trim().ToUpperInvariant()))
            .WithMessage("Code must be 2–10 letters or digits.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.EndDate).GreaterThanOrEqualTo(x => x.StartDate)
            .When(x => x.StartDate is not null && x.EndDate is not null).WithMessage("End date can't be before the start date.");
    }

    [GeneratedRegex("^[A-Z0-9]{2,10}$")]
    private static partial Regex CodeRegex();
}

public sealed class FundReasonValidator : AbstractValidator<FundReasonRequest>
{
    public FundReasonValidator() => RuleFor(x => x.Reason).MaximumLength(300);
}

public sealed class SetOpeningBalancesValidator : AbstractValidator<SetOpeningBalancesRequest>
{
    public SetOpeningBalancesValidator()
    {
        RuleFor(x => x.Items).NotNull()
            .Must(i => i.Select(x => x.AccountId).Distinct().Count() == i.Count).WithMessage("Each account can be listed only once.");
        RuleForEach(x => x.Items).ChildRules(i =>
            i.RuleFor(x => x.Amount).Must(a => decimal.TryParse(a, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && decimal.Round(v, 2) == v)
             .WithMessage("Enter an amount of zero or more with at most 2 decimals."));
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

/// <summary>
/// Fund management (PRD §6, §21.2). Admin-only at the endpoint. Lifecycle:
/// DRAFT → ACTIVE → CLOSED → ARCHIVED, and CLOSED → ACTIVE (reopen, reason required).
/// Closed funds reject new transactions (BR-017, also enforced by a DB trigger).
/// </summary>
public sealed class FundService(IFundLedgerDb db, ICurrentUser caller, IAuditWriter audit, IBalanceReader balances, TimeProvider clock)
{
    public async Task<IReadOnlyList<FundSummary>> ListAsync(CancellationToken ct)
    {
        var funds = await (from f in db.Funds.AsNoTracking()
                           join t in db.FundTypes.AsNoTracking() on f.FundTypeId equals t.Id
                           orderby f.Status, f.Name
                           select new { f, TypeName = t.Name }).ToListAsync(ct).ConfigureAwait(false);
        var closing = await balances.GetAllFundBalancesAsync(ct).ConfigureAwait(false);
        return funds.Select(x => ToSummary(x.f, x.TypeName, closing.GetValueOrDefault(x.f.Id)?.Closing ?? 0m)).ToList();
    }

    public async Task<FundDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var fund = await FindAsync(id, tracked: false, ct).ConfigureAwait(false);
        return await DetailAsync(fund, ct).ConfigureAwait(false);
    }

    public async Task<FundDetail> CreateAsync(SaveFundRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await EnsureFundTypeAsync(r.FundTypeId, ct).ConfigureAwait(false);
        var fund = new Fund
        {
            Id = Guid.CreateVersion7(), OrganizationId = caller.OrganizationId, FundTypeId = r.FundTypeId,
            Code = r.Code.Trim().ToUpperInvariant(), Name = r.Name.Trim(), Description = Clean(r.Description),
            StartDate = r.StartDate, EndDate = r.EndDate, Status = FundStatus.Draft, CreatedBy = caller.UserId, UpdatedBy = caller.UserId,
        };
        db.Funds.Add(fund);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.FundCreated, fund, null, Snapshot(fund), null, ct).ConfigureAwait(false);
        return await DetailAsync(fund, ct).ConfigureAwait(false);
    }

    public async Task<FundDetail> UpdateAsync(Guid id, SaveFundRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var fund = await FindAsync(id, tracked: true, ct).ConfigureAwait(false);
        if (fund.Status == FundStatus.Archived)
        {
            throw new ConflictException("FUND_ARCHIVED", "An archived fund can't be changed.");
        }

        var before = Snapshot(fund);
        fund.Description = Clean(r.Description);
        if (fund.Status != FundStatus.Closed)
        {
            // Closed funds keep their identity; only the description may change (App Flow §5.2).
            await EnsureFundTypeAsync(r.FundTypeId, ct).ConfigureAwait(false);
            var newCode = r.Code.Trim().ToUpperInvariant();
            if (newCode != fund.Code && await HasTransactionsAsync(id, ct).ConfigureAwait(false))
            {
                throw new ConflictException("FUND_CODE_LOCKED", "The fund code can't change once transactions exist.");
            }

            (fund.Code, fund.Name, fund.FundTypeId, fund.StartDate, fund.EndDate) =
                (newCode, r.Name.Trim(), r.FundTypeId, r.StartDate, r.EndDate);
        }

        fund.UpdatedBy = caller.UserId;
        fund.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.FundUpdated, fund, before, Snapshot(fund), null, ct).ConfigureAwait(false);
        return await DetailAsync(fund, ct).ConfigureAwait(false);
    }

    public Task<FundDetail> ActivateAsync(Guid id, CancellationToken ct) =>
        TransitionAsync(id, [FundStatus.Draft], FundStatus.Active, AuditActions.FundActivated, reason: null, requireReason: false, ct);

    public Task<FundDetail> CloseAsync(Guid id, FundReasonRequest r, CancellationToken ct) =>
        TransitionAsync(id, [FundStatus.Active], FundStatus.Closed, AuditActions.FundClosed, r?.Reason, requireReason: false, ct);

    public Task<FundDetail> ReopenAsync(Guid id, FundReasonRequest r, CancellationToken ct) =>
        TransitionAsync(id, [FundStatus.Closed], FundStatus.Active, AuditActions.FundReopened, r?.Reason, requireReason: true, ct);

    public Task<FundDetail> ArchiveAsync(Guid id, CancellationToken ct) =>
        TransitionAsync(id, [FundStatus.Closed], FundStatus.Archived, AuditActions.FundArchived, reason: null, requireReason: false, ct);

    /// <summary>Every active account with its opening amount for this fund (zero when none was set).</summary>
    public async Task<IReadOnlyList<OpeningBalanceDto>> GetOpeningBalancesAsync(Guid id, CancellationToken ct)
    {
        _ = await FindAsync(id, tracked: false, ct).ConfigureAwait(false);
        return await OpeningRowsAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets opening balances (BR-015, Admin only). On an ACTIVE fund any change needs a reason
    /// and is audited with old and new values. Rows are never deleted: clearing sets zero.
    /// </summary>
    public async Task<IReadOnlyList<OpeningBalanceDto>> SetOpeningBalancesAsync(Guid id, SetOpeningBalancesRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var fund = await FindAsync(id, tracked: false, ct).ConfigureAwait(false);
        if (fund.Status is not (FundStatus.Draft or FundStatus.Active))
        {
            throw new ConflictException("FUND_NOT_EDITABLE", "Opening balances can only be set while the fund is Draft or Active.");
        }

        var accountIds = r.Items.Select(i => i.AccountId).ToList();
        var active = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id) && a.IsActive).Select(a => a.Id).ToListAsync(ct)
            .ConfigureAwait(false);
        if (active.Count != accountIds.Count)
        {
            throw ValidationFailedException.For("items", "One or more accounts don't exist or are inactive.");
        }

        var existing = await db.OpeningBalances.Where(o => o.FundId == id).ToListAsync(ct).ConfigureAwait(false);
        var oldValues = existing.ToDictionary(o => o.AccountId.ToString(), o => Money.Of(o.Amount).ToString());
        var newValues = new Dictionary<string, string>();
        var changed = false;
        var now = clock.GetUtcNow();

        foreach (var item in r.Items)
        {
            var amount = Money.Parse(item.Amount).Amount;
            var row = existing.FirstOrDefault(o => o.AccountId == item.AccountId);
            if (row is null)
            {
                row = new Domain.Accounts.OpeningBalance { OrganizationId = caller.OrganizationId, FundId = id, AccountId = item.AccountId };
                db.OpeningBalances.Add(row);
                changed |= amount != 0m;
            }
            else
            {
                changed |= row.Amount != amount || row.AsOfDate != item.AsOfDate;
            }

            (row.Amount, row.AsOfDate, row.SetBy, row.SetAt) = (amount, item.AsOfDate, caller.UserId, now);
            newValues[item.AccountId.ToString()] = Money.Of(amount).ToString();
        }

        if (!changed)
        {
            return await OpeningRowsAsync(id, ct).ConfigureAwait(false);
        }

        if (fund.Status == FundStatus.Active && string.IsNullOrWhiteSpace(r.Reason))
        {
            throw new DomainException("REASON_REQUIRED", "A reason is required to change opening balances on an active fund.");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.OpeningBalanceChanged, fund, oldValues, newValues, r.Reason?.Trim(), ct).ConfigureAwait(false);
        return await OpeningRowsAsync(id, ct).ConfigureAwait(false);
    }

    // ---- internals --------------------------------------------------------------------
    private async Task<FundDetail> TransitionAsync(
        Guid id, FundStatus[] allowedFrom, FundStatus to, string action, string? reason, bool requireReason, CancellationToken ct)
    {
        var fund = await FindAsync(id, tracked: true, ct).ConfigureAwait(false);
        if (!allowedFrom.Contains(fund.Status))
        {
            throw new ConflictException("INVALID_FUND_TRANSITION", $"A {fund.Status.ToString().ToUpperInvariant()} fund can't become {to.ToString().ToUpperInvariant()}.");
        }

        if (requireReason && string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("REASON_REQUIRED", "Please give a reason.");
        }

        var from = fund.Status;
        var now = clock.GetUtcNow();
        fund.Status = to;
        (fund.ClosedAt, fund.ClosedBy) = to == FundStatus.Closed ? (now, caller.UserId) : to == FundStatus.Active ? (null, null) : (fund.ClosedAt, fund.ClosedBy);
        fund.UpdatedBy = caller.UserId;
        fund.UpdatedAt = now;
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(action, fund, new { status = from.ToString() }, new { status = to.ToString() }, reason?.Trim(), ct).ConfigureAwait(false);
        return await DetailAsync(fund, ct).ConfigureAwait(false);
    }

    private async Task<Fund> FindAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.Funds : db.Funds.AsNoTracking();
        return await q.SingleOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
    }

    private async Task<FundDetail> DetailAsync(Fund fund, CancellationToken ct)
    {
        var typeName = await db.FundTypes.AsNoTracking().Where(t => t.Id == fund.FundTypeId).Select(t => t.Name).SingleAsync(ct).ConfigureAwait(false);
        var closing = (await balances.GetFundBalanceAsync(fund.Id, ct).ConfigureAwait(false)).Closing;
        return new FundDetail(ToSummary(fund, typeName, closing), await HasTransactionsAsync(fund.Id, ct).ConfigureAwait(false),
            await OpeningRowsAsync(fund.Id, ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<OpeningBalanceDto>> OpeningRowsAsync(Guid fundId, CancellationToken ct)
    {
        var rows = await db.OpeningBalances.AsNoTracking().Where(o => o.FundId == fundId).ToListAsync(ct).ConfigureAwait(false);
        var accounts = await db.Accounts.AsNoTracking().OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct).ConfigureAwait(false);
        return accounts
            .Where(a => a.IsActive || rows.Any(r => r.AccountId == a.Id))
            .Select(a =>
            {
                var row = rows.FirstOrDefault(r => r.AccountId == a.Id);
                return new OpeningBalanceDto(a.Id, a.Name, a.IsActive, Money.Of(row?.Amount ?? 0m).ToString(), row?.AsOfDate);
            }).ToList();
    }

    private Task<bool> HasTransactionsAsync(Guid fundId, CancellationToken ct) =>
        db.Transactions.AsNoTracking().AnyAsync(t => t.FundId == fundId, ct);

    private async Task EnsureFundTypeAsync(Guid fundTypeId, CancellationToken ct)
    {
        if (!await db.FundTypes.AsNoTracking().AnyAsync(t => t.Id == fundTypeId && t.IsActive, ct).ConfigureAwait(false))
        {
            throw ValidationFailedException.For("fundTypeId", "Choose an active fund type.");
        }
    }

    private Task AuditAsync(string action, Fund fund, object? old, object? @new, string? reason, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, action, AuditEntities.Fund, fund.Id.ToString(),
            old, @new, reason, fund.Id), ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw pg.ConstraintName == "uq_funds_code"
                ? new ConflictException("FUND_CODE_EXISTS", "Another fund already uses this code.")
                : new ConflictException("FUND_NAME_EXISTS", "Another fund already has this name.");
        }
    }

    private static FundSummary ToSummary(Fund f, string typeName, decimal closing) =>
        new(f.Id, f.Code, f.Name, f.FundTypeId, typeName, f.Description, f.StartDate, f.EndDate, f.Status, Money.Of(closing).ToString());

    private static object Snapshot(Fund f) => new { f.Code, f.Name, f.FundTypeId, f.Description, f.StartDate, f.EndDate, status = f.Status.ToString() };

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
