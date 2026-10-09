using System.Net;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// The ten reports against a small dataset whose answers are worked out by hand (PRD §12, §29).
/// Opening: cash 5,000, bank 10,000. Entries (Admin unless noted):
///   D-3 Money in 25,000 cash · D-2 Money out 8,500 cash (Food) · D-2 transfer 20,000 cash → bank ·
///   D-1 Money in 1,000 bank (Member "Imran") · D-1 Money out 300 cash, then cancelled · D-1 adjustment −100 cash.
/// Hand-computed: cash 1,400 · bank 31,000 · fund 32,400.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class ReportTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static string Day(int offset) => Books.Today().AddDays(offset).ToString("yyyy-MM-dd");

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private sealed record Dataset(Books B, (Guid Id, Session Session) Member, Guid MemberEntry);

    private async Task<Dataset> SeedAsync(bool memberViewsAll = true)
    {
        var b = await Books.CreateAsync(_host, openingCash: 5000, openingBank: 10000);
        var member = await b.MemberAsync(viewAll: memberViewsAll, name: "Imran");
        object Deposit(string amount, int day, Guid account) => new
        {
            fundId = b.FundId, amount, txnDate = Day(day), txnTime = "09:00", categoryId = b.CatIn, accountId = account, paymentModeId = b.ModeCash,
            receivedFrom = "Area 4", purpose = "Collection", referenceNumber = (string?)null, remarks = (string?)null, clientTxnId = (Guid?)null,
        };
        object Expense(string amount, int day) => new
        {
            fundId = b.FundId, amount, txnDate = Day(day), txnTime = "10:00", categoryId = b.CatOut, accountId = b.Cash, paymentModeId = b.ModeCash,
            paidTo = "Caterer", purpose = "Lunch", referenceNumber = (string?)null, remarks = (string?)null, clientTxnId = (Guid?)null,
        };

        (await b.Admin.PostAsync("/api/v1/transactions/deposit", Deposit("25000.00", -3, b.Cash))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/expense", Expense("8500.00", -2))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/transfer", new
        {
            fundId = b.FundId, amount = "20000.00", txnDate = Day(-2), txnTime = "11:00", fromAccountId = b.Cash, toAccountId = b.Bank,
            paymentModeId = (Guid?)null, purpose = "Cash to bank", referenceNumber = (string?)null, remarks = (string?)null, clientTxnId = (Guid?)null,
        })).EnsureSuccessStatusCode();
        var mine = await Books.RecordAsync(member.Session, "deposit", Deposit("1000.00", -1, b.Bank));
        var cancelled = await Books.RecordAsync(b.Admin, "expense", Expense("300.00", -1));
        (await b.Admin.PostAsync($"/api/v1/transactions/{cancelled.Id}/cancel", new { reason = "Entered twice" }, cancelled.Revision)).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/adjustment", new
        {
            fundId = b.FundId, amount = "100.00", txnDate = Day(-1), txnTime = "12:00", accountId = b.Cash, direction = "DECREASE",
            reason = "Cash count was short", remarks = (string?)null, clientTxnId = (Guid?)null,
        })).EnsureSuccessStatusCode();
        return new Dataset(b, member, mine.Id);
    }

    private static async Task<JsonElement> RunAsync(Session s, string code, Guid fund, string query = "") =>
        await s.GetJsonAsync($"/api/v1/reports/{code}?fundId={fund}&from={Day(-3)}&to={Day(-1)}{query}");

    private static Dictionary<string, string> Figures(JsonElement report) =>
        report.GetProperty("summary").EnumerateArray().ToDictionary(f => Str(f, "key"), f => Str(f, "value"));

    private static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().ToList();

    [Fact]
    public async Task Fund_summary_matches_the_hand_computed_figures()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        var r = await RunAsync(d.B.Admin, "FUND_SUMMARY", d.B.FundId);
        var f = Figures(r);
        Assert.Equal("15000.00", f["opening"]);
        Assert.Equal("26000.00", f["moneyIn"]);
        Assert.Equal("8500.00", f["moneyOut"]);               // the cancelled 300 is not counted
        Assert.Equal("-100.00", f["adjustments"]);
        Assert.Equal("20000.00", f["transfers"]);
        Assert.Equal("32400.00", f["closing"]);
        Assert.False(r.GetProperty("ownOnly").GetBoolean());
        // The report agrees with the dashboard's computed balance.
        Assert.Equal("32400.00", Str(await d.B.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={d.B.FundId}"), "balance"));
    }

    [Fact]
    public async Task Daily_summary_gives_each_days_movements_and_running_closing_balance()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        var r = await RunAsync(d.B.Admin, "DAILY", d.B.FundId);
        var rows = Rows(r);
        Assert.Equal(3, rows.Count);
        Assert.Equal((Day(-3), "25000.00", "0.00", "40000.00"), (Str(rows[0], "date"), Str(rows[0], "moneyIn"), Str(rows[0], "moneyOut"), Str(rows[0], "closing")));
        Assert.Equal((Day(-2), "8500.00", "20000.00", "31500.00"), (Str(rows[1], "date"), Str(rows[1], "moneyOut"), Str(rows[1], "transfers"), Str(rows[1], "closing")));
        Assert.Equal((Day(-1), "1000.00", "-100.00", "32400.00"), (Str(rows[2], "date"), Str(rows[2], "moneyIn"), Str(rows[2], "adjustments"), Str(rows[2], "closing")));
        Assert.Equal("26000.00", Str(r.GetProperty("totals"), "moneyIn"));
    }

    [Fact]
    public async Task Date_range_uses_the_opening_balance_at_the_start_date_TR060()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        // Starting on D-2: cash opened that day at 5,000 + 25,000; bank at 10,000.
        var r = await d.B.Admin.GetJsonAsync($"/api/v1/reports/DATE_RANGE?fundId={d.B.FundId}&from={Day(-2)}&to={Day(-1)}");
        var f = Figures(r);
        Assert.Equal("40000.00", f["opening"]);
        Assert.Equal("1000.00", f["moneyIn"]);
        Assert.Equal("8500.00", f["moneyOut"]);
        Assert.Equal("32400.00", f["closing"]);
        var accounts = Rows(r).ToDictionary(x => Str(x, "account"));
        Assert.Equal(("30000.00", "20000.00", "-100.00", "1400.00"),
            (Str(accounts["Main Cash"], "opening"), Str(accounts["Main Cash"], "transfersOut"), Str(accounts["Main Cash"], "adjustments"), Str(accounts["Main Cash"], "closing")));
        Assert.Equal(("10000.00", "20000.00", "1000.00", "31000.00"),
            (Str(accounts["Bank"], "opening"), Str(accounts["Bank"], "transfersIn"), Str(accounts["Bank"], "moneyIn"), Str(accounts["Bank"], "closing")));
    }

    [Fact]
    public async Task Money_in_money_out_and_transfer_lists_carry_totals_and_filters()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        var admin = d.B.Admin;
        var income = await RunAsync(admin, "MONEY_IN", d.B.FundId);
        Assert.Equal(2, Rows(income).Count);
        Assert.Equal("26000.00", Str(income.GetProperty("totals"), "amount"));
        Assert.Equal("26000.00", Figures(income)["total"]);

        var mineOnly = await RunAsync(admin, "MONEY_IN", d.B.FundId, $"&userId={d.Member.Id}");
        Assert.Equal("1000.00", Str(mineOnly.GetProperty("totals"), "amount"));
        Assert.Equal("Imran", Str(Rows(mineOnly).Single(), "recordedBy"));
        Assert.Single(Rows(await RunAsync(admin, "MONEY_IN", d.B.FundId, $"&accountId={d.B.Bank}")));

        var spend = await RunAsync(admin, "MONEY_OUT", d.B.FundId);
        Assert.Single(Rows(spend));
        Assert.Equal("8500.00", Str(spend.GetProperty("totals"), "amount"));

        var moved = await RunAsync(admin, "TRANSFER", d.B.FundId);
        var transfer = Rows(moved).Single();
        Assert.Equal(("Main Cash", "Bank", "20000.00"), (Str(transfer, "from"), Str(transfer, "to"), Str(transfer, "amount")));
    }

    [Fact]
    public async Task User_category_account_and_cancelled_reports()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        var admin = d.B.Admin;

        var users = Rows(await RunAsync(admin, "USER_ACTIVITY", d.B.FundId)).ToDictionary(x => Str(x, "user"));
        Assert.Equal(("25000.00", "8500.00", "20000.00"), (Str(users["Test Admin"], "moneyIn"), Str(users["Test Admin"], "moneyOut"), Str(users["Test Admin"], "transfers")));
        Assert.Equal(("1000.00", "0.00", "1"), (Str(users["Imran"], "moneyIn"), Str(users["Imran"], "moneyOut"), Str(users["Imran"], "count")));

        var cats = Rows(await RunAsync(admin, "CATEGORY", d.B.FundId)).ToDictionary(x => Str(x, "direction") + "/" + Str(x, "category"));
        Assert.Equal(("26000.00", "100.0"), (Str(cats["Money in/Collection"], "amount"), Str(cats["Money in/Collection"], "share")));
        Assert.Equal("8500.00", Str(cats["Money out/Food"], "amount"));

        var balances = await RunAsync(admin, "ACCOUNT_BALANCE", d.B.FundId);
        var byName = Rows(balances).ToDictionary(x => Str(x, "account"));
        Assert.Equal("1400.00", Str(byName["Main Cash"], "balance"));
        Assert.Equal("31000.00", Str(byName["Bank"], "balance"));
        Assert.Equal("32400.00", Str(balances.GetProperty("totals"), "balance"));

        var cancelled = Rows(await RunAsync(admin, "CANCELLED", d.B.FundId)).Single();
        Assert.Equal(("Entered twice", "300.00", "Test Admin"), (Str(cancelled, "reason"), Str(cancelled, "amount"), Str(cancelled, "cancelledBy")));
    }

    [Fact]
    public async Task A_member_who_cant_see_everything_gets_only_their_own_entries_and_no_fund_wide_balances_TR061()
    {
        SkipIfNoDb();
        var d = await SeedAsync(memberViewsAll: false);
        var r = await RunAsync(d.Member.Session, "FUND_SUMMARY", d.B.FundId);
        Assert.True(r.GetProperty("ownOnly").GetBoolean());
        var f = Figures(r);
        Assert.Equal("1000.00", f["moneyIn"]);
        Assert.Equal("0.00", f["moneyOut"]);
        Assert.DoesNotContain("opening", f.Keys);               // fund-wide balances would reveal other people's entries
        Assert.DoesNotContain("closing", f.Keys);

        var list = await RunAsync(d.Member.Session, "MONEY_IN", d.B.FundId);
        Assert.Single(Rows(list));
        Assert.Equal("1000.00", Str(list.GetProperty("totals"), "amount"));
        Assert.Empty(Rows(await RunAsync(d.Member.Session, "CANCELLED", d.B.FundId)));
        Assert.Single(Rows(await RunAsync(d.Member.Session, "USER_ACTIVITY", d.B.FundId)));
        Assert.DoesNotContain(Rows(await RunAsync(d.Member.Session, "DAILY", d.B.FundId)), x => x.GetProperty("closing").ValueKind != JsonValueKind.Null);

        // A member who may see everything still gets the fund-wide figures.
        var open = await SeedAsync();
        Assert.Equal("32400.00", Figures(await RunAsync(open.Member.Session, "FUND_SUMMARY", open.B.FundId))["closing"]);
    }

    [Fact]
    public async Task Reports_need_the_capability_and_the_fund_and_a_known_code()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        // Capability withdrawn.
        (await d.B.Admin.PutAsync($"/api/v1/users/{d.Member.Id}/fund-access", new
        {
            items = new[] { new { fundId = d.B.FundId, canMoneyIn = true, canMoneyOut = true, canTransfer = false, canViewReports = false, canExport = false, canViewAllTxns = true } },
        })).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await d.Member.Session.GetAsync($"/api/v1/reports/FUND_SUMMARY?fundId={d.B.FundId}"), HttpStatusCode.Forbidden, "FORBIDDEN");

        // Unknown report, bad dates, another organization's fund.
        await ProblemAssert.HasCodeAsync(await d.B.Admin.GetAsync($"/api/v1/reports/NOPE?fundId={d.B.FundId}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await d.B.Admin.GetAsync($"/api/v1/reports/DAILY?fundId={d.B.FundId}&from={Day(0)}&to={Day(-1)}"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var other = await _host.CreateOrgAsync();
        var intruder = await other.AdminAsync();
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/reports/FUND_SUMMARY?fundId={d.B.FundId}"), HttpStatusCode.NotFound, "NOT_FOUND");

        // Catalogue lists all ten, and the code is case-insensitive.
        Assert.Equal(10, (await d.B.Admin.GetJsonAsync("/api/v1/reports")).GetArrayLength());
        Assert.Equal("FUND_SUMMARY", Str(await d.B.Admin.GetJsonAsync($"/api/v1/reports/fund_summary?fundId={d.B.FundId}"), "code"));
    }

    [Fact]
    public async Task Without_dates_the_period_runs_from_the_fund_start_to_today()
    {
        SkipIfNoDb();
        var d = await SeedAsync();
        var r = await d.B.Admin.GetJsonAsync($"/api/v1/reports/FUND_SUMMARY?fundId={d.B.FundId}");
        Assert.Equal(Books.Today().ToString("yyyy-MM-dd"), Str(r, "to"));
        Assert.Equal("32400.00", Figures(r)["closing"]);
        Assert.Equal("Test Org", Str(r, "organizationName"));
    }
}
