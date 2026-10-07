using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Profile;

public sealed record FundPermissions(
    bool MoneyIn, bool MoneyOut, bool Transfer, bool ViewReports, bool Export, bool ViewAllTransactions, bool Adjust);

public sealed record AccessibleFund(Guid Id, string Code, string Name, FundStatus Status, FundPermissions Permissions);

public sealed record OrganizationInfo(Guid Id, string Name, string ShortCode, string CurrencyCode, string Timezone, string DateFormat);

public sealed record MeResponse(
    Guid Id, string FullName, string Mobile, UserRole Role, bool PinMustChange,
    OrganizationInfo Organization, IReadOnlyList<AccessibleFund> Funds);

/// <summary>
/// <c>GET /me</c> (TRD §11.2): who am I, what organization, and which funds with which
/// permissions. The PWA builds its navigation and quick actions from this.
/// </summary>
public sealed class MeService(IFundLedgerDb db, ICurrentUser caller)
{
    private static readonly FundPermissions All = new(true, true, true, true, true, true, true);

    public async Task<MeResponse> GetAsync(CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == caller.UserId, ct).ConfigureAwait(false)
            ?? throw new UnauthenticatedException();
        var org = await db.Organizations.AsNoTracking().SingleAsync(ct).ConfigureAwait(false);

        return new MeResponse(user.Id, user.FullName, user.MobileE164, user.Role, user.PinMustChange,
            new OrganizationInfo(org.Id, org.Name, org.ShortCode, org.CurrencyCode, org.Timezone, org.DateFormat),
            user.PinMustChange ? [] : await ListAccessibleFundsAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Funds the caller may open: Admins see all; Members see assigned Active/Closed funds.</summary>
    public async Task<IReadOnlyList<AccessibleFund>> ListAccessibleFundsAsync(CancellationToken ct)
    {
        if (caller.IsAdmin)
        {
            var funds = await db.Funds.AsNoTracking().OrderBy(f => f.Status).ThenBy(f => f.Name).ToListAsync(ct).ConfigureAwait(false);
            return funds.Select(f => new AccessibleFund(f.Id, f.Code, f.Name, f.Status, All)).ToList();
        }

        var rows = await (
                from a in db.UserFundAccess.AsNoTracking()
                join f in db.Funds.AsNoTracking() on a.FundId equals f.Id
                where a.UserId == caller.UserId && (f.Status == FundStatus.Active || f.Status == FundStatus.Closed)
                orderby f.Status, f.Name
                select new { f, a })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(x => new AccessibleFund(x.f.Id, x.f.Code, x.f.Name, x.f.Status,
            new FundPermissions(x.a.CanMoneyIn, x.a.CanMoneyOut, x.a.CanTransfer, x.a.CanViewReports, x.a.CanExport,
                x.a.CanViewAllTxns, Adjust: false))).ToList();
    }
}
