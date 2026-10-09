using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FundLedger.Api.IntegrationTests.Support;

/// <summary>Ids of the seeded master data plus one ACTIVE fund, created through the real API.</summary>
public sealed record Books(TestOrg Org, Session Admin, Guid FundId, string FundCode, Guid Cash, Guid Bank, Guid CatIn, Guid CatOut, Guid ModeCash, Guid ModeUpi)
{
    /// <summary>Today in the organization's timezone (what the server calls "today").</summary>
    public static DateOnly Today()
    {
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    }

    public static async Task<Books> CreateAsync(ApiHost host, string code = "IJT26", decimal openingCash = 0, decimal openingBank = 0)
    {
        var org = await host.CreateOrgAsync();
        var admin = await org.AdminAsync();

        var fundTypes = await admin.GetJsonAsync("/api/v1/fund-types");
        var fundTypeId = fundTypes.EnumerateArray().First().GetProperty("id").GetGuid();
        var create = await admin.PostAsync("/api/v1/funds", new { code, name = "Fund " + code, fundTypeId, startDate = (string?)null, endDate = (string?)null, description = (string?)null });
        create.EnsureSuccessStatusCode();
        var fundId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fund").GetProperty("id").GetGuid();

        var books = new Books(org, admin, fundId, code,
            await IdAsync(admin, "/api/v1/accounts", "Main Cash"), await IdAsync(admin, "/api/v1/accounts", "Bank"),
            await IdAsync(admin, "/api/v1/categories?direction=MONEY_IN", "Collection"), await IdAsync(admin, "/api/v1/categories?direction=MONEY_OUT", "Food"),
            await IdAsync(admin, "/api/v1/payment-modes", "Cash"), await IdAsync(admin, "/api/v1/payment-modes", "UPI"));

        if (openingCash != 0 || openingBank != 0)
        {
            (await admin.PutAsync($"/api/v1/funds/{fundId}/opening-balances", new
            {
                items = new object[]
                {
                    new { accountId = books.Cash, amount = openingCash.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), asOfDate = Today().ToString("yyyy-MM-dd") },
                    new { accountId = books.Bank, amount = openingBank.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), asOfDate = Today().ToString("yyyy-MM-dd") },
                },
                reason = (string?)null,
            })).EnsureSuccessStatusCode();
        }

        (await admin.PostAsync($"/api/v1/funds/{fundId}/activate", null)).EnsureSuccessStatusCode();
        return books;
    }

    /// <summary>Creates another fund in the same organization (activated unless asked otherwise).</summary>
    public static async Task<Guid> CreateFundAsync(Books b, string code, bool activate = true)
    {
        var fundTypeId = (await b.Admin.GetJsonAsync("/api/v1/fund-types")).EnumerateArray().First().GetProperty("id").GetGuid();
        var r = await b.Admin.PostAsync("/api/v1/funds", new { code, name = "Fund " + code, fundTypeId, startDate = (string?)null, endDate = (string?)null, description = (string?)null });
        r.EnsureSuccessStatusCode();
        var id = (await JsonAsync(r)).GetProperty("fund").GetProperty("id").GetGuid();
        if (activate)
        {
            (await b.Admin.PostAsync($"/api/v1/funds/{id}/activate", null)).EnsureSuccessStatusCode();
        }

        return id;
    }

    public static async Task<Guid> IdAsync(Session s, string path, string name)
    {
        var list = await s.GetJsonAsync(path);
        return list.EnumerateArray().First(x => x.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }

    public object Deposit(string amount = "100.00", DateOnly? date = null, Guid? clientId = null, Guid? mode = null, string? reference = null) => new
    {
        fundId = FundId, amount, txnDate = (date ?? Today()).ToString("yyyy-MM-dd"), txnTime = "00:00", categoryId = CatIn, accountId = Cash,
        paymentModeId = mode ?? ModeCash, receivedFrom = "Area 4 team", purpose = "Collection", referenceNumber = reference, remarks = (string?)null, clientTxnId = clientId,
    };

    public object Expense(string amount = "50.00", DateOnly? date = null, string purpose = "Lunch for volunteers") => new
    {
        fundId = FundId, amount, txnDate = (date ?? Today()).ToString("yyyy-MM-dd"), txnTime = "00:00", categoryId = CatOut, accountId = Cash,
        paymentModeId = ModeCash, paidTo = "Caterer", purpose, referenceNumber = (string?)null, remarks = (string?)null, clientTxnId = (Guid?)null,
    };

    public object Transfer(string amount = "100.00", Guid? from = null, Guid? to = null) => new
    {
        fundId = FundId, amount, txnDate = Today().ToString("yyyy-MM-dd"), txnTime = "00:00", fromAccountId = from ?? Cash, toAccountId = to ?? Bank,
        paymentModeId = (Guid?)null, purpose = "Deposit cash to bank", referenceNumber = (string?)null, remarks = (string?)null, clientTxnId = (Guid?)null,
    };

    /// <summary>Creates a Member with access to this fund and returns a session that has already chosen its own PIN.</summary>
    public async Task<(Guid Id, Session Session)> MemberAsync(bool moneyIn = true, bool moneyOut = true, bool transfer = false, bool viewAll = true, string name = "Ahmed")
    {
        var m = await Org.CreateMemberAsync(Admin, name);
        (await Admin.PutAsync($"/api/v1/users/{m.Id}/fund-access", new
        {
            items = new[] { new { fundId = FundId, canMoneyIn = moneyIn, canMoneyOut = moneyOut, canTransfer = transfer, canViewReports = true, canExport = false, canViewAllTxns = viewAll } },
        })).EnsureSuccessStatusCode();
        var s = await Session.LoginAsync(Org.Host, m.Mobile, m.TemporaryPin);
        await s.ChangePinAsync(m.TemporaryPin, "730264");
        return (m.Id, s);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text);
    }
}
