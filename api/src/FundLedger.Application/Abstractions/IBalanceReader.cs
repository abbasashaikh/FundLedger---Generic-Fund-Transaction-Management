namespace FundLedger.Application.Abstractions;

/// <summary>Fund totals from the <c>v_fund_balances</c> view (ADR-0004).</summary>
public sealed record FundBalance(decimal Opening, decimal MoneyIn, decimal MoneyOut, decimal AdjustmentsNet, decimal Closing)
{
    public static FundBalance Zero { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>One account's balance within one fund (<c>v_fund_account_balances</c>).</summary>
public sealed record AccountBalance(
    Guid AccountId, decimal Opening, decimal MoneyIn, decimal MoneyOut, decimal TransfersIn, decimal TransfersOut, decimal AdjustmentsNet, decimal Closing);

/// <summary>Reads computed balances. Balances are never stored (ADR-0004); cancelled transactions are excluded by the views.</summary>
public interface IBalanceReader
{
    Task<FundBalance> GetFundBalanceAsync(Guid fundId, CancellationToken ct);

    /// <summary>Balances of every fund the caller can see, keyed by fund id (one query).</summary>
    Task<IReadOnlyDictionary<Guid, FundBalance>> GetAllFundBalancesAsync(CancellationToken ct);

    Task<IReadOnlyList<AccountBalance>> GetAccountBalancesAsync(Guid fundId, CancellationToken ct);
}
