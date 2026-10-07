using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Domain.Audit;
using FundLedger.Domain.Organizations;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FundLedger.Application.Bootstrap;

public sealed record BootstrapRequest(string OrganizationName, string ShortCode, string AdminName, string AdminMobile);

public sealed record BootstrapResult(Guid OrganizationId, Guid AdminUserId, string AdminMobile, string TemporaryPin);

/// <summary>
/// First-run setup (App Flow §3.4, P1-01): creates an organization, its first Admin
/// (with a one-time temporary PIN) and the default lookups from the PRD, in one
/// transaction under the new organization's tenant context, so RLS applies as usual.
/// </summary>
public sealed class BootstrapService(IFundLedgerDb db, TenantContext tenant, IPinHasher hasher, IAuditWriter audit, TimeProvider clock)
{
    private static readonly string[] FundTypes = ["Event", "Charity", "Masjid", "Medical", "Education", "Society", "Project", "Other"];

    private static readonly (string Name, bool RequiresReference)[] PaymentModes =
        [("Cash", false), ("UPI", true), ("Bank Transfer", true), ("Cheque", true), ("Card", false), ("Other", false)];

    private static readonly (string Name, string Kind)[] Accounts = [("Main Cash", "CASH"), ("Bank", "BANK")];

    private static readonly string[] MoneyIn = ["Collection", "Donation", "Contribution", "Sponsorship", "Grant", "Membership", "Other Income"];

    private static readonly string[] MoneyOut =
        ["Food", "Transport", "Medical", "Education", "Rent", "Electricity", "Cleaning", "Equipment", "Printing", "Maintenance", "Miscellaneous", "Other"];

    public async Task<BootstrapResult> RunAsync(BootstrapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.OrganizationName.Trim();
        var code = request.ShortCode.Trim().ToUpperInvariant();
        if (name.Length is 0 or > 150 || code.Length is < 2 or > 10 || !code.All(char.IsAsciiLetterOrDigit))
        {
            throw ValidationFailedException.For("organization", "Name is required (≤150 chars); code must be 2–10 letters/digits.");
        }

        if (string.IsNullOrWhiteSpace(request.AdminName) || request.AdminName.Trim().Length > 120)
        {
            throw ValidationFailedException.For("adminName", "Admin name is required (≤120 chars).");
        }

        var mobile = MobileNumber.Normalize(request.AdminMobile);
        var orgId = Guid.CreateVersion7();
        var adminId = Guid.CreateVersion7();
        var now = clock.GetUtcNow();
        var pin = PinPolicy.GenerateTemporary(mobile);

        tenant.Set(orgId, adminId, isAdmin: true);
        await db.InTransactionAsync(async () =>
        {
            db.Organizations.Add(new Organization { Id = orgId, Name = name, ShortCode = code });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);   // org first: users reference it
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConflictException("ORGANIZATION_EXISTS", $"An organization with code '{code}' already exists.");
            }

            db.Users.Add(new User
            {
                Id = adminId,
                OrganizationId = orgId,
                FullName = request.AdminName.Trim(),
                MobileE164 = mobile,
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                PinHash = hasher.Hash(pin),
                PinSetAt = now,
                PinMustChange = true,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var sort = 0;
            foreach (var t in FundTypes)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO fl.fund_types (id, organization_id, name, sort_order) VALUES ({Guid.CreateVersion7()}, {orgId}, {t}, {sort++})",
                    ct).ConfigureAwait(false);
            }

            sort = 0;
            foreach (var (mode, requiresRef) in PaymentModes)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO fl.payment_modes (id, organization_id, name, requires_reference, sort_order) VALUES ({Guid.CreateVersion7()}, {orgId}, {mode}, {requiresRef}, {sort++})",
                    ct).ConfigureAwait(false);
            }

            sort = 0;
            foreach (var (account, kind) in Accounts)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO fl.accounts (id, organization_id, name, kind, sort_order, created_by) VALUES ({Guid.CreateVersion7()}, {orgId}, {account}, {kind}::fl.account_kind, {sort++}, {adminId})",
                    ct).ConfigureAwait(false);
            }

            foreach (var (names, direction) in new[] { (MoneyIn, "MONEY_IN"), (MoneyOut, "MONEY_OUT") })
            {
                sort = 0;
                foreach (var category in names)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"INSERT INTO fl.categories (id, organization_id, direction, name, sort_order, created_by) VALUES ({Guid.CreateVersion7()}, {orgId}, {direction}::fl.category_direction, {category}, {sort++}, {adminId})",
                        ct).ConfigureAwait(false);
                }
            }

            await audit.WriteAsync(new AuditEntry(orgId, null, AuditActions.OrganizationCreated, AuditEntities.Organization,
                orgId.ToString(), NewValue: new { name, code }), ct).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(orgId, null, AuditActions.UserCreated, AuditEntities.User, adminId.ToString(),
                NewValue: new { fullName = request.AdminName.Trim(), mobile = MobileNumber.Mask(mobile), role = "Admin", via = "bootstrap" }), ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new BootstrapResult(orgId, adminId, mobile, pin);
    }
}
