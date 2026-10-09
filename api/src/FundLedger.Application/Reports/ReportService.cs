using System.Globalization;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Funds;
using FundLedger.Domain;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Application.Reports;

/// <summary>
/// Computes the ten reports (TRD §10.1). Every report:
/// <list type="bullet">
/// <item>needs <c>can_view_reports</c> on the fund (404 for an invisible fund, 403 without the capability);</item>
/// <item>uses the ledger's scope rule (TR-061): a Member without "see all" gets only their own entries, totals included,
///   and no fund-wide balances (those would include other people's entries);</item>
/// <item>counts ACTIVE entries only, except the Cancelled report;</item>
/// <item>uses TR-060 for the opening balance at <c>from</c>: fund opening balances plus every active movement before it.</item>
/// </list>
/// Entries are loaded once and aggregated in memory: a fund holds thousands of entries, not millions (PRD §2).
/// </summary>
public sealed class ReportService(IFundLedgerDb db, ICurrentUser caller, FundAccessGuard guard, TimeProvider clock)
{
    /// <summary>Rows shown on screen. Exports use <see cref="ExportRowLimit"/>.</summary>
    public const int ViewRowLimit = 2000;

    public const int ExportRowLimit = 50_000;

    public async Task<ReportResult> RunAsync(ReportCode code, ReportQuery q, CancellationToken ct, int rowLimit = ViewRowLimit)
    {
        ArgumentNullException.ThrowIfNull(q);
        var fund = await guard.RequireAsync(q.FundId, FundCapability.ViewReports, ct).ConfigureAwait(false);
        var ownOnly = !caller.IsAdmin && !await db.UserFundAccess.AsNoTracking()
            .Where(a => a.UserId == caller.UserId && a.FundId == q.FundId).Select(a => a.CanViewAllTxns).SingleOrDefaultAsync(ct).ConfigureAwait(false);

        var org = await db.Organizations.AsNoTracking().Select(o => new { o.Name, o.Timezone }).SingleAsync(ct).ConfigureAwait(false);
        var tz = OrgZone.Find(org.Timezone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).DateTime);
        var to = q.To ?? today;
        var from = q.From ?? fund.StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fund.CreatedAt, tz).DateTime);
        if (from > to)
        {
            throw ValidationFailedException.For("from", "The start date must be on or before the end date.");
        }

        var rows = await LoadAsync(q.FundId, to, ownOnly, ct).ConfigureAwait(false);
        var openings = ownOnly
            ? []
            : await db.OpeningBalances.AsNoTracking().Where(o => o.FundId == q.FundId)
                .Select(o => new { o.AccountId, o.Amount }).ToDictionaryAsync(o => o.AccountId, o => o.Amount, ct).ConfigureAwait(false);
        var me = await db.Users.AsNoTracking().Where(u => u.Id == caller.UserId).Select(u => u.FullName).SingleAsync(ct).ConfigureAwait(false);

        var ctx = new Ctx(q, from, to, ownOnly, rows, openings, rowLimit);
        var (summary, columns, data, totals, truncated) = code switch
        {
            ReportCode.FundSummary => FundSummary(ctx),
            ReportCode.Daily => Daily(ctx),
            ReportCode.DateRange => DateRange(ctx, await AccountNamesAsync(ct).ConfigureAwait(false)),
            ReportCode.MoneyIn => List(ctx, TxnType.Deposit),
            ReportCode.MoneyOut => List(ctx, TxnType.Expense),
            ReportCode.Transfer => Transfers(ctx),
            ReportCode.UserActivity => UserActivity(ctx),
            ReportCode.Category => Category(ctx),
            ReportCode.AccountBalance => AccountBalance(ctx, await AccountNamesAsync(ct).ConfigureAwait(false)),
            ReportCode.Cancelled => Cancelled(ctx),
            _ => throw new NotFoundException(),
        };

        var (title, description) = ReportCodes.Describe(code);
        return new ReportResult(ReportCodes.Wire(code), title, description, fund.Id, fund.Name, from, to, ownOnly, clock.GetUtcNow(), me, org.Name,
            summary, columns, data, totals, truncated);
    }

    // ---- data ---------------------------------------------------------------------------
    internal sealed record Row(
        Guid Id, string Number, TxnType Type, TxnStatus Status, decimal Amount, DateOnly Date, TimeOnly Time,
        Guid? CategoryId, string? Category, Guid? AccountId, string? Account, Guid? FromId, string? From, Guid? ToId, string? To,
        Guid? ModeId, string? Mode, AdjustmentDirection? Adjustment, string? ReceivedFrom, string? PaidTo, string? Purpose, string? Reference,
        Guid CreatedBy, string CreatedByName, string? CancelledByName, DateTimeOffset? CancelledAt, string? CancelReason)
    {
        public bool IsActive => Status == TxnStatus.Active;

        /// <summary>Effect on the fund total: transfers move money between accounts and net to zero (BR-011).</summary>
        public decimal FundEffect => Type switch
        {
            TxnType.Deposit => Amount,
            TxnType.Expense => -Amount,
            TxnType.Adjustment => Adjustment == AdjustmentDirection.Increase ? Amount : -Amount,
            _ => 0m,
        };

        /// <summary>Signed effect on one account (PRD §12.4).</summary>
        public decimal AccountEffect(Guid account) => Type switch
        {
            TxnType.Transfer when FromId == account => -Amount,
            TxnType.Transfer when ToId == account => Amount,
            TxnType.Transfer => 0m,
            _ when AccountId == account => FundEffect,
            _ => 0m,
        };
    }

    private sealed record Ctx(
        ReportQuery Q, DateOnly From, DateOnly To, bool OwnOnly, IReadOnlyList<Row> All, IReadOnlyDictionary<Guid, decimal> Openings, int RowLimit)
    {
        public IEnumerable<Row> Active => All.Where(r => r.IsActive);

        public IEnumerable<Row> InPeriod => Active.Where(r => r.Date >= From && r.Date <= To);

        public decimal FundOpening => Openings.Values.Sum();

        /// <summary>TR-060: opening balances plus every active movement dated before <paramref name="day"/>.</summary>
        public decimal FundBalanceBefore(DateOnly day) => FundOpening + Active.Where(r => r.Date < day).Sum(r => r.FundEffect);
    }

    private async Task<List<Row>> LoadAsync(Guid fundId, DateOnly to, bool ownOnly, CancellationToken ct)
    {
        var query =
            from t in db.Transactions.AsNoTracking()
            where t.FundId == fundId && t.TxnDate <= to && (!ownOnly || t.CreatedBy == caller.UserId)
            join u in db.Users.AsNoTracking() on t.CreatedBy equals u.Id
            join c in db.Categories.AsNoTracking() on t.CategoryId equals c.Id into cg
            from c in cg.DefaultIfEmpty()
            join a in db.Accounts.AsNoTracking() on t.AccountId equals a.Id into ag
            from a in ag.DefaultIfEmpty()
            join fa in db.Accounts.AsNoTracking() on t.FromAccountId equals fa.Id into fag
            from fa in fag.DefaultIfEmpty()
            join ta in db.Accounts.AsNoTracking() on t.ToAccountId equals ta.Id into tag
            from ta in tag.DefaultIfEmpty()
            join pm in db.PaymentModes.AsNoTracking() on t.PaymentModeId equals pm.Id into pmg
            from pm in pmg.DefaultIfEmpty()
            join cu in db.Users.AsNoTracking() on t.CancelledBy equals cu.Id into cug
            from cu in cug.DefaultIfEmpty()
            orderby t.TxnDate, t.TxnTime, t.TxnNumber
            select new Row(t.Id, t.TxnNumber, t.TxnType, t.Status, t.Amount, t.TxnDate, t.TxnTime, t.CategoryId, c.Name, t.AccountId, a.Name,
                t.FromAccountId, fa.Name, t.ToAccountId, ta.Name, t.PaymentModeId, pm.Name, t.AdjustmentDirection, t.ReceivedFrom, t.PaidTo,
                t.Purpose, t.ReferenceNumber, t.CreatedBy, u.FullName, cu.FullName, t.CancelledAt, t.CancellationReason);
        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<Guid, (string Name, string Kind, bool Active, int Sort)>> AccountNamesAsync(CancellationToken ct) =>
        await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => (a.Name, a.Kind.ToString().ToUpperInvariant(), a.IsActive, (int)a.SortOrder), ct)
            .ConfigureAwait(false);

    // ---- reports ------------------------------------------------------------------------
    private static Built FundSummary(Ctx c)
    {
        var period = c.InPeriod.ToList();
        var moneyIn = Sum(period, TxnType.Deposit);
        var moneyOut = Sum(period, TxnType.Expense);
        var adjustments = period.Where(r => r.Type == TxnType.Adjustment).Sum(r => r.FundEffect);
        var summary = new List<ReportFigure>();
        if (!c.OwnOnly)
        {
            summary.Add(MoneyFig("opening", "Opening balance", c.FundBalanceBefore(c.From)));
        }

        summary.Add(MoneyFig("moneyIn", "Money in", moneyIn));
        summary.Add(MoneyFig("moneyOut", "Money out", moneyOut));
        summary.Add(MoneyFig("adjustments", "Adjustments (net)", adjustments));
        summary.Add(MoneyFig("transfers", "Moved between accounts", Sum(period, TxnType.Transfer)));
        if (!c.OwnOnly)
        {
            summary.Add(MoneyFig("closing", "Closing balance", c.FundBalanceBefore(c.To.AddDays(1))));
        }

        var rows = period.Where(r => r.Type is TxnType.Deposit or TxnType.Expense)
            .GroupBy(r => (r.Type, r.Category))
            .OrderBy(g => g.Key.Type).ThenByDescending(g => g.Sum(r => r.Amount))
            .Select(g => Cells(("direction", g.Key.Type == TxnType.Deposit ? "Money in" : "Money out"), ("category", g.Key.Category ?? "—"),
                ("count", Count(g.Count())), ("amount", Money(g.Sum(r => r.Amount))))).ToList();
        return new(summary, [Col("direction", "Direction"), Col("category", "Category"), Col("count", "Entries", ReportValueKind.Number), MoneyCol("amount", "Amount")],
            rows, null, false);
    }

    private static Built Daily(Ctx c)
    {
        var running = c.OwnOnly ? 0m : c.FundBalanceBefore(c.From);
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        decimal tin = 0, tout = 0, ttr = 0, tadj = 0;
        foreach (var day in c.InPeriod.GroupBy(r => r.Date).OrderBy(g => g.Key))
        {
            var mi = Sum(day, TxnType.Deposit);
            var mo = Sum(day, TxnType.Expense);
            var tr = Sum(day, TxnType.Transfer);
            var adj = day.Where(r => r.Type == TxnType.Adjustment).Sum(r => r.FundEffect);
            running += mi - mo + adj;
            tin += mi;
            tout += mo;
            ttr += tr;
            tadj += adj;
            rows.Add(Cells(("date", Date(day.Key)), ("moneyIn", Money(mi)), ("moneyOut", Money(mo)), ("transfers", Money(tr)), ("adjustments", Money(adj)),
                ("net", Money(mi - mo + adj)), ("closing", c.OwnOnly ? null : Money(running))));
        }

        var summary = new List<ReportFigure> { MoneyFig("moneyIn", "Money in", tin), MoneyFig("moneyOut", "Money out", tout), MoneyFig("net", "Net", tin - tout + tadj) };
        if (!c.OwnOnly)
        {
            summary.Insert(0, MoneyFig("opening", "Opening balance", c.FundBalanceBefore(c.From)));
            summary.Add(MoneyFig("closing", "Closing balance", c.FundBalanceBefore(c.To.AddDays(1))));
        }

        var columns = new List<ReportColumn>
        {
            Col("date", "Date", ReportValueKind.Date), MoneyCol("moneyIn", "Money in"), MoneyCol("moneyOut", "Money out"),
            MoneyCol("transfers", "Transfers"), MoneyCol("adjustments", "Adjustments"), MoneyCol("net", "Net"),
        };
        if (!c.OwnOnly)
        {
            columns.Add(MoneyCol("closing", "Closing balance"));
        }

        return new(summary, columns, rows,
            Cells(("date", "Total"), ("moneyIn", Money(tin)), ("moneyOut", Money(tout)), ("transfers", Money(ttr)), ("adjustments", Money(tadj)), ("net", Money(tin - tout + tadj))),
            false);
    }

    private static Built DateRange(Ctx c, Dictionary<Guid, (string Name, string Kind, bool Active, int Sort)> accounts)
    {
        var period = c.InPeriod.ToList();
        var before = c.Active.Where(r => r.Date < c.From).ToList();
        var ids = AccountsTouched(c, accounts);
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        decimal tOpen = 0, tIn = 0, tOut = 0, tTi = 0, tTo = 0, tAdj = 0, tClose = 0;
        foreach (var id in ids)
        {
            var open = (c.OwnOnly ? 0m : c.Openings.GetValueOrDefault(id)) + before.Sum(r => r.AccountEffect(id));
            var mi = period.Where(r => r.Type == TxnType.Deposit && r.AccountId == id).Sum(r => r.Amount);
            var mo = period.Where(r => r.Type == TxnType.Expense && r.AccountId == id).Sum(r => r.Amount);
            var ti = period.Where(r => r.Type == TxnType.Transfer && r.ToId == id).Sum(r => r.Amount);
            var tf = period.Where(r => r.Type == TxnType.Transfer && r.FromId == id).Sum(r => r.Amount);
            var adj = period.Where(r => r.Type == TxnType.Adjustment && r.AccountId == id).Sum(r => r.FundEffect);
            var close = open + mi - mo + ti - tf + adj;
            (tOpen, tIn, tOut, tTi, tTo, tAdj, tClose) = (tOpen + open, tIn + mi, tOut + mo, tTi + ti, tTo + tf, tAdj + adj, tClose + close);
            rows.Add(Cells(("account", accounts[id].Name), ("opening", Money(open)), ("moneyIn", Money(mi)), ("moneyOut", Money(mo)),
                ("transfersIn", Money(ti)), ("transfersOut", Money(tf)), ("adjustments", Money(adj)), ("closing", Money(close))));
        }

        var summary = new List<ReportFigure>
        {
            MoneyFig("moneyIn", "Money in", tIn), MoneyFig("moneyOut", "Money out", tOut), MoneyFig("adjustments", "Adjustments (net)", tAdj),
        };
        if (!c.OwnOnly)
        {
            summary.Insert(0, MoneyFig("opening", "Opening balance", tOpen));
            summary.Add(MoneyFig("closing", "Closing balance", tClose));
        }

        return new(summary,
            [Col("account", "Account"), MoneyCol("opening", "Opening"), MoneyCol("moneyIn", "Money in"), MoneyCol("moneyOut", "Money out"),
             MoneyCol("transfersIn", "Transfers in"), MoneyCol("transfersOut", "Transfers out"), MoneyCol("adjustments", "Adjustments"), MoneyCol("closing", "Closing")],
            rows,
            Cells(("account", "Total"), ("opening", Money(tOpen)), ("moneyIn", Money(tIn)), ("moneyOut", Money(tOut)), ("transfersIn", Money(tTi)),
                ("transfersOut", Money(tTo)), ("adjustments", Money(tAdj)), ("closing", Money(tClose))),
            false);
    }

    private static Built List(Ctx c, TxnType type)
    {
        var all = Filtered(c, c.InPeriod.Where(r => r.Type == type)).ToList();
        var party = type == TxnType.Deposit ? ("party", "Received from") : ("party", "Paid to");
        var rows = all.Take(c.RowLimit).Select(r => Cells(("date", Date(r.Date)), ("number", r.Number), ("category", r.Category), ("account", r.Account),
            ("mode", r.Mode), ("party", type == TxnType.Deposit ? r.ReceivedFrom : r.PaidTo), ("purpose", r.Purpose), ("reference", r.Reference),
            ("recordedBy", r.CreatedByName), ("amount", Money(r.Amount)))).ToList();
        var total = all.Sum(r => r.Amount);
        return new(
            [MoneyFig("total", type == TxnType.Deposit ? "Total money in" : "Total money out", total), CountFig("count", "Entries", all.Count)],
            [Col("date", "Date", ReportValueKind.Date), Col("number", "Number"), Col("category", "Category"), Col("account", "Account"), Col("mode", "Mode"),
             Col(party.Item1, party.Item2), Col("purpose", "Purpose"), Col("reference", "Reference"), Col("recordedBy", "Recorded by"), MoneyCol("amount", "Amount")],
            rows, Cells(("date", "Total"), ("amount", Money(total))), all.Count > c.RowLimit);
    }

    private static Built Transfers(Ctx c)
    {
        var all = Filtered(c, c.InPeriod.Where(r => r.Type == TxnType.Transfer))
            .OrderBy(r => r.From, StringComparer.CurrentCulture).ThenBy(r => r.To, StringComparer.CurrentCulture).ThenBy(r => r.Date).ToList();
        var rows = all.Take(c.RowLimit).Select(r => Cells(("from", r.From), ("to", r.To), ("date", Date(r.Date)), ("number", r.Number),
            ("purpose", r.Purpose), ("reference", r.Reference), ("recordedBy", r.CreatedByName), ("amount", Money(r.Amount)))).ToList();
        var summary = new List<ReportFigure> { MoneyFig("total", "Total moved", all.Sum(r => r.Amount)), CountFig("count", "Transfers", all.Count) };
        summary.AddRange(all.GroupBy(r => (r.From, r.To)).Select((g, i) =>
            MoneyFig($"pair{i}", $"{g.Key.From} → {g.Key.To}", g.Sum(r => r.Amount))));
        return new(summary,
            [Col("from", "From"), Col("to", "To"), Col("date", "Date", ReportValueKind.Date), Col("number", "Number"), Col("purpose", "Purpose"),
             Col("reference", "Reference"), Col("recordedBy", "Recorded by"), MoneyCol("amount", "Amount")],
            rows, Cells(("from", "Total"), ("amount", Money(all.Sum(r => r.Amount)))), all.Count > c.RowLimit);
    }

    private static Built UserActivity(Ctx c)
    {
        var groups = c.InPeriod.Where(r => c.Q.UserId is null || r.CreatedBy == c.Q.UserId)
            .GroupBy(r => (r.CreatedBy, r.CreatedByName)).OrderBy(g => g.Key.CreatedByName, StringComparer.CurrentCulture).ToList();
        var rows = groups.Select(g => Cells(("user", g.Key.CreatedByName), ("moneyIn", Money(Sum(g, TxnType.Deposit))), ("moneyOut", Money(Sum(g, TxnType.Expense))),
            ("transfers", Money(Sum(g, TxnType.Transfer))), ("adjustments", Money(g.Where(r => r.Type == TxnType.Adjustment).Sum(r => r.FundEffect))),
            ("count", Count(g.Count())))).ToList();
        var all = groups.SelectMany(g => g).ToList();
        return new(
            [CountFig("people", "People", groups.Count), CountFig("count", "Entries", all.Count), MoneyFig("moneyIn", "Money in", Sum(all, TxnType.Deposit)),
             MoneyFig("moneyOut", "Money out", Sum(all, TxnType.Expense))],
            [Col("user", "Recorded by"), MoneyCol("moneyIn", "Money in"), MoneyCol("moneyOut", "Money out"), MoneyCol("transfers", "Transfers"),
             MoneyCol("adjustments", "Adjustments"), Col("count", "Entries", ReportValueKind.Number)],
            rows,
            Cells(("user", "Total"), ("moneyIn", Money(Sum(all, TxnType.Deposit))), ("moneyOut", Money(Sum(all, TxnType.Expense))), ("transfers", Money(Sum(all, TxnType.Transfer))),
                ("adjustments", Money(all.Where(r => r.Type == TxnType.Adjustment).Sum(r => r.FundEffect))), ("count", Count(all.Count))),
            false);
    }

    private static Built Category(Ctx c)
    {
        var period = c.InPeriod.Where(r => r.Type is TxnType.Deposit or TxnType.Expense).ToList();
        var totalIn = Sum(period, TxnType.Deposit);
        var totalOut = Sum(period, TxnType.Expense);
        var rows = period.GroupBy(r => (r.Type, r.Category)).OrderBy(g => g.Key.Type).ThenByDescending(g => g.Sum(r => r.Amount)).Select(g =>
        {
            var sum = g.Sum(r => r.Amount);
            var whole = g.Key.Type == TxnType.Deposit ? totalIn : totalOut;
            return Cells(("direction", g.Key.Type == TxnType.Deposit ? "Money in" : "Money out"), ("category", g.Key.Category ?? "—"), ("count", Count(g.Count())),
                ("amount", Money(sum)), ("share", whole == 0 ? "0.0" : Math.Round(sum * 100 / whole, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture)));
        }).ToList();
        return new([MoneyFig("moneyIn", "Money in", totalIn), MoneyFig("moneyOut", "Money out", totalOut)],
            [Col("direction", "Direction"), Col("category", "Category"), Col("count", "Entries", ReportValueKind.Number), MoneyCol("amount", "Amount"), Col("share", "Share %", ReportValueKind.Number)],
            rows, null, false);
    }

    private static Built AccountBalance(Ctx c, Dictionary<Guid, (string Name, string Kind, bool Active, int Sort)> accounts)
    {
        // "As of" the end date: everything up to and including it (the start date does not apply).
        var upTo = c.Active.ToList();
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        decimal total = 0;
        foreach (var id in AccountsTouched(c, accounts))
        {
            var open = c.OwnOnly ? 0m : c.Openings.GetValueOrDefault(id);
            var mi = upTo.Where(r => r.Type == TxnType.Deposit && r.AccountId == id).Sum(r => r.Amount);
            var mo = upTo.Where(r => r.Type == TxnType.Expense && r.AccountId == id).Sum(r => r.Amount);
            var ti = upTo.Where(r => r.Type == TxnType.Transfer && r.ToId == id).Sum(r => r.Amount);
            var tf = upTo.Where(r => r.Type == TxnType.Transfer && r.FromId == id).Sum(r => r.Amount);
            var adj = upTo.Where(r => r.Type == TxnType.Adjustment && r.AccountId == id).Sum(r => r.FundEffect);
            var bal = open + mi - mo + ti - tf + adj;
            total += bal;
            rows.Add(Cells(("account", accounts[id].Name), ("kind", accounts[id].Kind), ("opening", Money(open)), ("moneyIn", Money(mi)), ("moneyOut", Money(mo)),
                ("transfersIn", Money(ti)), ("transfersOut", Money(tf)), ("adjustments", Money(adj)), ("balance", Money(bal))));
        }

        return new([MoneyFig("balance", c.OwnOnly ? "Net of my entries" : "Fund balance", total)],
            [Col("account", "Account"), Col("kind", "Type"), MoneyCol("opening", "Opening"), MoneyCol("moneyIn", "Money in"), MoneyCol("moneyOut", "Money out"),
             MoneyCol("transfersIn", "Transfers in"), MoneyCol("transfersOut", "Transfers out"), MoneyCol("adjustments", "Adjustments"), MoneyCol("balance", "Balance")],
            rows, Cells(("account", "Total"), ("balance", Money(total))), false);
    }

    private static Built Cancelled(Ctx c)
    {
        var all = Filtered(c, c.All.Where(r => !r.IsActive && r.Date >= c.From && r.Date <= c.To)).ToList();
        var rows = all.Take(c.RowLimit).Select(r => Cells(("date", Date(r.Date)), ("number", r.Number), ("type", TypeLabel(r.Type)), ("recordedBy", r.CreatedByName),
            ("cancelledBy", r.CancelledByName), ("cancelledAt", r.CancelledAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC"),
            ("reason", r.CancelReason), ("amount", Money(r.Amount)))).ToList();
        return new([CountFig("count", "Cancelled entries", all.Count), MoneyFig("amount", "Amount (not counted)", all.Sum(r => r.Amount))],
            [Col("date", "Date", ReportValueKind.Date), Col("number", "Number"), Col("type", "Type"), Col("recordedBy", "Recorded by"), Col("cancelledBy", "Cancelled by"),
             Col("cancelledAt", "Cancelled at"), Col("reason", "Reason"), MoneyCol("amount", "Amount")],
            rows, null, all.Count > c.RowLimit);
    }

    // ---- helpers ------------------------------------------------------------------------
    private sealed record Built(
        IReadOnlyList<ReportFigure> Summary, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows,
        IReadOnlyDictionary<string, string?>? Totals, bool Truncated);


    private static IEnumerable<Row> Filtered(Ctx c, IEnumerable<Row> rows)
    {
        var q = c.Q;
        return rows.Where(r => (q.Type is null || r.Type == q.Type) && (q.CategoryId is null || r.CategoryId == q.CategoryId)
            && (q.AccountId is null || r.AccountId == q.AccountId || r.FromId == q.AccountId || r.ToId == q.AccountId)
            && (q.UserId is null || r.CreatedBy == q.UserId) && (q.PaymentModeId is null || r.ModeId == q.PaymentModeId));
    }

    /// <summary>Accounts with an opening balance or any entry, plus every active account; in the usual order.</summary>
    private static List<Guid> AccountsTouched(Ctx c, Dictionary<Guid, (string Name, string Kind, bool Active, int Sort)> accounts)
    {
        var used = new HashSet<Guid>(c.Openings.Keys);
        foreach (var r in c.Active)
        {
            foreach (var id in new[] { r.AccountId, r.FromId, r.ToId })
            {
                if (id is { } x)
                {
                    used.Add(x);
                }
            }
        }

        if (c.Q.AccountId is { } only)
        {
            used.IntersectWith([only]);
        }
        else
        {
            used.UnionWith(accounts.Where(a => a.Value.Active).Select(a => a.Key));
        }

        return used.Where(accounts.ContainsKey).OrderBy(id => accounts[id].Sort).ThenBy(id => accounts[id].Name, StringComparer.CurrentCulture).ToList();
    }

    private static decimal Sum(IEnumerable<Row> rows, TxnType type) => rows.Where(r => r.Type == type).Sum(r => r.Amount);

    private static string Money(decimal v) => Domain.Money.Of(v).ToString();

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string TypeLabel(TxnType t) => t switch
    {
        TxnType.Deposit => "Money in",
        TxnType.Expense => "Money out",
        TxnType.Transfer => "Transfer",
        _ => "Adjustment",
    };

    private static ReportColumn Col(string key, string label, ReportValueKind kind = ReportValueKind.Text) => new(key, label, kind);

    private static ReportColumn MoneyCol(string key, string label) => new(key, label, ReportValueKind.Money);

    private static ReportFigure MoneyFig(string key, string label, decimal v) => new(key, label, Money(v), ReportValueKind.Money);

    private static ReportFigure CountFig(string key, string label, int n) => new(key, label, Count(n), ReportValueKind.Number);

    private static Dictionary<string, string?> Cells(params (string Key, string? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);
}

/// <summary>The organization's time zone, with a fixed IST fallback when the host has no tz data.</summary>
public static class OrgZone
{
    public static TimeZoneInfo Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        }
    }
}
