using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain.Funds;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Funds;

/// <summary>
/// Application-layer fund authorization (TRD TR-004, layer 2 of 3). Every fund-scoped
/// operation from Phase 2 onward calls <see cref="RequireAsync"/> first.
///
/// A fund the caller can't see returns 404, never 403, so ids can't be probed. A
/// visible fund without the specific capability returns 403.
/// </summary>
public sealed class FundAccessGuard(IFundLedgerDb db, ICurrentUser caller)
{
    public async Task<Fund> RequireAsync(Guid fundId, FundCapability permission, CancellationToken ct)
    {
        var fund = await db.Funds.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fundId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException();

        if (caller.IsAdmin)
        {
            return fund;
        }

        // Members never see drafts or archived funds (App Flow §5.2).
        if (fund.Status is FundStatus.Draft or FundStatus.Archived)
        {
            throw new NotFoundException();
        }

        var access = await db.UserFundAccess.AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == caller.UserId && a.FundId == fundId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException();

        return access.Allows(permission) ? fund : throw new ForbiddenException();
    }
}
