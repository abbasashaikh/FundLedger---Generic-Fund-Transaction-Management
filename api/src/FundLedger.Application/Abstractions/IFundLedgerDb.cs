using FundLedger.Domain.Accounts;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using FundLedger.Domain.Lookups;
using FundLedger.Domain.Organizations;
using FundLedger.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FundLedger.Application.Abstractions;

/// <summary>The application's view of the database (implemented by the EF Core context).</summary>
public interface IFundLedgerDb
{
    DbSet<Organization> Organizations { get; }

    DbSet<User> Users { get; }

    DbSet<UserSession> UserSessions { get; }

    DbSet<Fund> Funds { get; }

    DbSet<UserFundAccess> UserFundAccess { get; }

    DbSet<FundType> FundTypes { get; }

    DbSet<PaymentMode> PaymentModes { get; }

    DbSet<Account> Accounts { get; }

    DbSet<OpeningBalance> OpeningBalances { get; }

    DbSet<Category> Categories { get; }

    DbSet<Transaction> Transactions { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public static class FundLedgerDbExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> inside a DB transaction, so the tenant context is
    /// applied (ADR-0008). Reuses the ambient transaction opened by the request filter;
    /// otherwise opens, commits (or rolls back on exception) its own.
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(this IFundLedgerDb db, Func<Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(work);
        if (db.Database.CurrentTransaction is not null)
        {
            return await work().ConfigureAwait(false);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var result = await work().ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public static Task InTransactionAsync(this IFundLedgerDb db, Func<Task> work, CancellationToken ct) =>
        db.InTransactionAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(work);
            await work().ConfigureAwait(false);
            return true;
        }, ct);
}
