using FundLedger.Application.Abstractions;

namespace FundLedger.Infrastructure.Persistence;

/// <summary>Reads the security-invoker balance views, so RLS applies to the caller.</summary>
public sealed class BalanceReader(FundLedgerDbContext db) : IBalanceReader
{
    public async Task<FundBalance> GetFundBalanceAsync(Guid fundId, CancellationToken ct)
    {
        var rows = await RawSql.QueryAsync(db,
            "SELECT opening_balance, money_in, money_out, adjustments_net, closing_balance FROM fl.v_fund_balances WHERE fund_id = @fund",
            [("fund", fundId)],
            r => new FundBalance(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)), ct).ConfigureAwait(false);
        return rows.SingleOrDefault() ?? FundBalance.Zero;
    }

    public async Task<IReadOnlyDictionary<Guid, FundBalance>> GetAllFundBalancesAsync(CancellationToken ct)
    {
        var rows = await RawSql.QueryAsync(db,
            "SELECT fund_id, opening_balance, money_in, money_out, adjustments_net, closing_balance FROM fl.v_fund_balances", [],
            r => (Id: r.GetGuid(0), Balance: new FundBalance(r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5))), ct)
            .ConfigureAwait(false);
        return rows.ToDictionary(x => x.Id, x => x.Balance);
    }

    public async Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(Guid fundId, CancellationToken ct) =>
        await RawSql.QueryAsync(db,
            """
            SELECT account_id, opening_balance, money_in, money_out, transfers_in, transfers_out, adjustments_net, closing_balance
              FROM fl.v_fund_account_balances WHERE fund_id = @fund
            """,
            [("fund", fundId)],
            r => new AccountBalance(r.GetGuid(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4),
                r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7)), ct).ConfigureAwait(false);
}
