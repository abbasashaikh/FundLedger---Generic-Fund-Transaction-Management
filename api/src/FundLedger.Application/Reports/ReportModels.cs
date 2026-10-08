using FundLedger.Domain.Ledger;

namespace FundLedger.Application.Reports;

/// <summary>The ten reports of TRD §10.1. The wire form is the upper-snake code (FUND_SUMMARY, ...).</summary>
public enum ReportCode
{
    FundSummary,
    Daily,
    DateRange,
    MoneyIn,
    MoneyOut,
    Transfer,
    UserActivity,
    Category,
    AccountBalance,
    Cancelled,
}

public enum ReportValueKind
{
    Text,
    Date,
    Money,
    Number,
}

/// <summary>Report parameters (TRD §10.1). Filters other than the dates apply to the list reports only.</summary>
public sealed record ReportQuery(
    Guid FundId, DateOnly? From, DateOnly? To, TxnType? Type = null, Guid? CategoryId = null, Guid? AccountId = null,
    Guid? UserId = null, Guid? PaymentModeId = null);

public sealed record ReportColumn(string Key, string Label, ReportValueKind Kind);

/// <summary>A headline figure ("Opening balance", "Money in", ...). Money values are decimal strings.</summary>
public sealed record ReportFigure(string Key, string Label, string Value, ReportValueKind Kind);

/// <summary>
/// One report, already computed. The viewer, CSV, Excel and PDF all render this same object, so the
/// numbers can never differ between screen and file. Cell values are strings: money as "1500.00",
/// dates as "yyyy-MM-dd", counts as integers.
/// </summary>
/// <param name="OwnOnly">True when the caller only sees their own entries (TRD TR-061): label it "My transactions only".</param>
/// <param name="Truncated">More rows exist than were returned; narrow the period or export instead.</param>
public sealed record ReportResult(
    string Code, string Title, string Description, Guid FundId, string FundName, DateOnly From, DateOnly To, bool OwnOnly,
    DateTimeOffset GeneratedAt, string GeneratedBy, string OrganizationName,
    IReadOnlyList<ReportFigure> Summary, IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, IReadOnlyDictionary<string, string?>? Totals, bool Truncated);

public static class ReportCodes
{
    private static readonly Dictionary<string, ReportCode> ByWire = Enum.GetValues<ReportCode>()
        .ToDictionary(Wire, c => c, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<ReportCode> All { get; } = Enum.GetValues<ReportCode>();

    /// <summary>FundSummary → FUND_SUMMARY.</summary>
    public static string Wire(ReportCode code) =>
        string.Concat(code.ToString().Select((ch, i) => i > 0 && char.IsUpper(ch) ? "_" + ch : ch.ToString())).ToUpperInvariant();

    public static bool TryParse(string? value, out ReportCode code) => ByWire.TryGetValue(value?.Trim() ?? string.Empty, out code);

    public static (string Title, string Description) Describe(ReportCode code) => code switch
    {
        ReportCode.FundSummary => ("Fund summary", "Opening, money in and out, adjustments and closing balance, with totals by category."),
        ReportCode.Daily => ("Daily summary", "Money in, money out and transfers for each day, with the closing balance."),
        ReportCode.DateRange => ("Date range summary", "Opening balance, movements and closing balance for the period, by account."),
        ReportCode.MoneyIn => ("Money in", "Every money-in entry with totals."),
        ReportCode.MoneyOut => ("Money out", "Every money-out entry with totals."),
        ReportCode.Transfer => ("Transfers", "Transfers between accounts, grouped by account pair."),
        ReportCode.UserActivity => ("User activity", "Money in, money out and transfers recorded by each person."),
        ReportCode.Category => ("Category-wise", "Totals by category for money in and money out."),
        ReportCode.AccountBalance => ("Account balances", "Each account's balance as of the end date."),
        ReportCode.Cancelled => ("Cancelled entries", "Cancelled entries with who cancelled them and why."),
        _ => throw new ArgumentOutOfRangeException(nameof(code)),
    };
}
