using FluentValidation;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain.Accounts;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Lookups;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Application.Lookups;

public sealed record FundTypeDto(Guid Id, string Name, bool IsActive, short SortOrder);

public sealed record PaymentModeDto(Guid Id, string Name, bool RequiresReference, bool IsActive, short SortOrder);

public sealed record AccountDto(Guid Id, string Name, AccountKind Kind, string? BankName, string? AccountNumberLast4, bool IsActive, short SortOrder);

public sealed record CategoryDto(Guid Id, Guid? FundId, CategoryDirection Direction, string Name, string? Icon, bool IsActive, short SortOrder);

public sealed record UpsertFundTypeRequest(string Name, bool IsActive = true, short SortOrder = 0);

public sealed record UpsertPaymentModeRequest(string Name, bool RequiresReference, bool IsActive = true, short SortOrder = 0);

public sealed record UpsertAccountRequest(
    string Name, AccountKind Kind, string? BankName, string? AccountNumberLast4, bool IsActive = true, short SortOrder = 0);

/// <summary>Direction is fixed at creation: changing it would re-classify historical transactions.</summary>
public sealed record CreateCategoryRequest(CategoryDirection Direction, string Name, string? Icon, Guid? FundId, short SortOrder = 0);

public sealed record UpdateCategoryRequest(string Name, string? Icon, Guid? FundId, bool IsActive, short SortOrder);

public sealed class UpsertFundTypeValidator : AbstractValidator<UpsertFundTypeRequest>
{
    public UpsertFundTypeValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
}

public sealed class UpsertPaymentModeValidator : AbstractValidator<UpsertPaymentModeRequest>
{
    public UpsertPaymentModeValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(40);
}

public sealed class UpsertAccountValidator : AbstractValidator<UpsertAccountRequest>
{
    public UpsertAccountValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.BankName).MaximumLength(80);
        RuleFor(x => x.AccountNumberLast4).Matches("^[0-9]{4}$").WithMessage("Enter exactly the last 4 digits.")
            .When(x => !string.IsNullOrEmpty(x.AccountNumberLast4));
    }
}

public sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Direction).IsInEnum();
        RuleFor(x => x.Icon).MaximumLength(40);
    }
}

public sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryRequest>
{
    public UpdateCategoryValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Icon).MaximumLength(40);
    }
}

/// <summary>
/// Org-level master data (PRD §21.3–21.5). Readable by every signed-in user (inactive items
/// only for Admins); writable by Admins only (enforced at the endpoint). Nothing is ever
/// deleted: items are deactivated, so history keeps its labels.
/// </summary>
public sealed class LookupService(IFundLedgerDb db, ICurrentUser caller, IAuditWriter audit, TimeProvider clock)
{
    // ---- fund types ---------------------------------------------------------------
    public async Task<IReadOnlyList<FundTypeDto>> ListFundTypesAsync(bool includeInactive, CancellationToken ct) =>
        await db.FundTypes.AsNoTracking().Where(x => includeInactive && caller.IsAdmin || x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new FundTypeDto(x.Id, x.Name, x.IsActive, x.SortOrder)).ToListAsync(ct).ConfigureAwait(false);

    public async Task<FundTypeDto> CreateFundTypeAsync(UpsertFundTypeRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = new FundType { Id = Guid.CreateVersion7(), OrganizationId = caller.OrganizationId, Name = r.Name.Trim(), IsActive = r.IsActive, SortOrder = r.SortOrder };
        db.FundTypes.Add(e);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.LookupChanged, AuditEntities.Lookup, e.Id, null, new { kind = "FundType", e.Name, e.IsActive }, ct).ConfigureAwait(false);
        return new FundTypeDto(e.Id, e.Name, e.IsActive, e.SortOrder);
    }

    public async Task<FundTypeDto> UpdateFundTypeAsync(Guid id, UpsertFundTypeRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = await db.FundTypes.SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var before = new { kind = "FundType", e.Name, e.IsActive };
        (e.Name, e.IsActive, e.SortOrder) = (r.Name.Trim(), r.IsActive, r.SortOrder);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.LookupChanged, AuditEntities.Lookup, e.Id, before, new { kind = "FundType", e.Name, e.IsActive }, ct).ConfigureAwait(false);
        return new FundTypeDto(e.Id, e.Name, e.IsActive, e.SortOrder);
    }

    // ---- payment modes ------------------------------------------------------------
    public async Task<IReadOnlyList<PaymentModeDto>> ListPaymentModesAsync(bool includeInactive, CancellationToken ct) =>
        await db.PaymentModes.AsNoTracking().Where(x => includeInactive && caller.IsAdmin || x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new PaymentModeDto(x.Id, x.Name, x.RequiresReference, x.IsActive, x.SortOrder)).ToListAsync(ct).ConfigureAwait(false);

    public async Task<PaymentModeDto> CreatePaymentModeAsync(UpsertPaymentModeRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = new PaymentMode
        {
            Id = Guid.CreateVersion7(), OrganizationId = caller.OrganizationId, Name = r.Name.Trim(),
            RequiresReference = r.RequiresReference, IsActive = r.IsActive, SortOrder = r.SortOrder,
        };
        db.PaymentModes.Add(e);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.LookupChanged, AuditEntities.Lookup, e.Id, null, new { kind = "PaymentMode", e.Name, e.RequiresReference }, ct).ConfigureAwait(false);
        return new PaymentModeDto(e.Id, e.Name, e.RequiresReference, e.IsActive, e.SortOrder);
    }

    public async Task<PaymentModeDto> UpdatePaymentModeAsync(Guid id, UpsertPaymentModeRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = await db.PaymentModes.SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var before = new { kind = "PaymentMode", e.Name, e.RequiresReference, e.IsActive };
        (e.Name, e.RequiresReference, e.IsActive, e.SortOrder) = (r.Name.Trim(), r.RequiresReference, r.IsActive, r.SortOrder);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.LookupChanged, AuditEntities.Lookup, e.Id, before,
            new { kind = "PaymentMode", e.Name, e.RequiresReference, e.IsActive }, ct).ConfigureAwait(false);
        return new PaymentModeDto(e.Id, e.Name, e.RequiresReference, e.IsActive, e.SortOrder);
    }

    // ---- accounts -----------------------------------------------------------------
    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(bool includeInactive, CancellationToken ct) =>
        await db.Accounts.AsNoTracking().Where(x => includeInactive && caller.IsAdmin || x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new AccountDto(x.Id, x.Name, x.Kind, x.BankName, x.AccountNumberLast4, x.IsActive, x.SortOrder))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<AccountDto> CreateAccountAsync(UpsertAccountRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = new Account
        {
            Id = Guid.CreateVersion7(), OrganizationId = caller.OrganizationId, Name = r.Name.Trim(), Kind = r.Kind,
            BankName = Clean(r.BankName), AccountNumberLast4 = Clean(r.AccountNumberLast4), IsActive = r.IsActive,
            SortOrder = r.SortOrder, CreatedBy = caller.UserId, UpdatedBy = caller.UserId,
        };
        db.Accounts.Add(e);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.AccountCreated, AuditEntities.Account, e.Id, null, Snapshot(e), ct).ConfigureAwait(false);
        return ToDto(e);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid id, UpsertAccountRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = await db.Accounts.SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        var before = Snapshot(e);
        (e.Name, e.Kind, e.BankName, e.AccountNumberLast4, e.IsActive, e.SortOrder) =
            (r.Name.Trim(), r.Kind, Clean(r.BankName), Clean(r.AccountNumberLast4), r.IsActive, r.SortOrder);
        e.UpdatedBy = caller.UserId;
        e.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.AccountUpdated, AuditEntities.Account, e.Id, before, Snapshot(e), ct).ConfigureAwait(false);
        return ToDto(e);
    }

    // ---- categories ---------------------------------------------------------------
    /// <summary>Categories usable for a fund: org-wide ones plus that fund's own.</summary>
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(
        CategoryDirection? direction, Guid? fundId, bool includeInactive, CancellationToken ct)
    {
        var q = db.Categories.AsNoTracking().Where(x => includeInactive && caller.IsAdmin || x.IsActive);
        if (direction is { } d)
        {
            q = q.Where(x => x.Direction == d);
        }

        if (fundId is { } f)
        {
            q = q.Where(x => x.FundId == null || x.FundId == f);
        }

        return await q.OrderBy(x => x.Direction).ThenBy(x => x.SortOrder).ThenBy(x => x.Name)
            .Select(x => new CategoryDto(x.Id, x.FundId, x.Direction, x.Name, x.Icon, x.IsActive, x.SortOrder))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        await EnsureFundExistsAsync(r.FundId, ct).ConfigureAwait(false);
        var e = new Category
        {
            Id = Guid.CreateVersion7(), OrganizationId = caller.OrganizationId, FundId = r.FundId, Direction = r.Direction,
            Name = r.Name.Trim(), Icon = Clean(r.Icon), SortOrder = r.SortOrder, CreatedBy = caller.UserId, UpdatedBy = caller.UserId,
        };
        db.Categories.Add(e);
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.CategoryCreated, AuditEntities.Category, e.Id, null, Snapshot(e), ct).ConfigureAwait(false);
        return ToDto(e);
    }

    public async Task<CategoryDto> UpdateCategoryAsync(Guid id, UpdateCategoryRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        var e = await db.Categories.SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false) ?? throw new NotFoundException();
        await EnsureFundExistsAsync(r.FundId, ct).ConfigureAwait(false);
        var before = Snapshot(e);
        (e.Name, e.Icon, e.FundId, e.IsActive, e.SortOrder) = (r.Name.Trim(), Clean(r.Icon), r.FundId, r.IsActive, r.SortOrder);
        e.UpdatedBy = caller.UserId;
        e.UpdatedAt = clock.GetUtcNow();
        await SaveAsync(ct).ConfigureAwait(false);
        await AuditAsync(AuditActions.CategoryUpdated, AuditEntities.Category, e.Id, before, Snapshot(e), ct).ConfigureAwait(false);
        return ToDto(e);
    }

    // ---- helpers --------------------------------------------------------------------
    private async Task EnsureFundExistsAsync(Guid? fundId, CancellationToken ct)
    {
        if (fundId is { } f && !await db.Funds.AnyAsync(x => x.Id == f, ct).ConfigureAwait(false))
        {
            throw ValidationFailedException.For("fundId", "That fund doesn't exist.");
        }
    }

    private Task AuditAsync(string action, string entity, Guid id, object? old, object? @new, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(caller.OrganizationId, caller.UserId, action, entity, id.ToString(), old, @new), ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("NAME_ALREADY_EXISTS", "An item with this name already exists.");
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static AccountDto ToDto(Account e) => new(e.Id, e.Name, e.Kind, e.BankName, e.AccountNumberLast4, e.IsActive, e.SortOrder);

    private static CategoryDto ToDto(Category e) => new(e.Id, e.FundId, e.Direction, e.Name, e.Icon, e.IsActive, e.SortOrder);

    private static object Snapshot(Account e) => new { e.Name, kind = e.Kind.ToString(), e.BankName, e.AccountNumberLast4, e.IsActive };

    private static object Snapshot(Category e) => new { e.Name, direction = e.Direction.ToString(), e.FundId, e.IsActive };
}
