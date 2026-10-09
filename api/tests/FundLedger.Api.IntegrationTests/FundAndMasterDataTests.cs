using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Fund lifecycle, opening balances (BR-015) and master data (PRD §6, §21).</summary>
[Collection(PostgresTests.Name)]
public sealed class FundAndMasterDataTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static object FundBody(string code, Guid type, string name = "Test fund") =>
        new { code, name, fundTypeId = type, description = (string?)null, startDate = (string?)null, endDate = (string?)null };

    [Fact]
    public async Task Fund_follows_draft_active_closed_archived_and_rejects_invalid_transitions()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var type = (await admin.GetJsonAsync("/api/v1/fund-types")).EnumerateArray().First().GetProperty("id").GetGuid();
        var created = await Books.JsonAsync(await admin.PostAsync("/api/v1/funds", FundBody("life", type)));
        var id = created.GetProperty("fund").GetProperty("id").GetGuid();

        Assert.Equal("DRAFT", created.GetProperty("fund").GetProperty("status").GetString());
        Assert.Equal("LIFE", created.GetProperty("fund").GetProperty("code").GetString());          // codes are upper-cased

        await ProblemAssert.HasCodeAsync(await admin.PostAsync($"/api/v1/funds/{id}/close", new { reason = (string?)null }), HttpStatusCode.Conflict, "INVALID_FUND_TRANSITION");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync($"/api/v1/funds/{id}/archive", null), HttpStatusCode.Conflict, "INVALID_FUND_TRANSITION");

        (await admin.PostAsync($"/api/v1/funds/{id}/activate", null)).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await admin.PostAsync($"/api/v1/funds/{id}/activate", null), HttpStatusCode.Conflict, "INVALID_FUND_TRANSITION");
        (await admin.PostAsync($"/api/v1/funds/{id}/close", new { reason = "Done" })).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/funds/{id}/archive", null)).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await admin.PutAsync($"/api/v1/funds/{id}", FundBody("LIFE", type, "Renamed")), HttpStatusCode.Conflict, "FUND_ARCHIVED");

        var audit = await ActionsAsync(org.OrganizationId, id.ToString());
        Assert.Equal(new[] { "FUND_CREATED", "FUND_ACTIVATED", "FUND_CLOSED", "FUND_ARCHIVED" }, audit);
    }

    [Fact]
    public async Task Fund_code_and_name_must_be_unique_and_valid()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var type = (await admin.GetJsonAsync("/api/v1/fund-types")).EnumerateArray().First().GetProperty("id").GetGuid();
        (await admin.PostAsync("/api/v1/funds", FundBody("ONE", type, "Fund one"))).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", FundBody("one", type, "Other name")), HttpStatusCode.Conflict, "FUND_CODE_EXISTS");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", FundBody("TWO", type, "Fund one")), HttpStatusCode.Conflict, "FUND_NAME_EXISTS");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", FundBody("X", type)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", FundBody("BAD-CODE", type)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", FundBody("TWO", Guid.NewGuid())), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/funds", new
        {
            code = "DATES", name = "Dates", fundTypeId = type, description = (string?)null, startDate = "2026-10-10", endDate = "2026-10-01",
        }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Fund_code_is_locked_once_transactions_exist()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "LCK");
        var type = (await b.Admin.GetJsonAsync("/api/v1/fund-types")).EnumerateArray().First().GetProperty("id").GetGuid();
        (await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}", FundBody("NEW", type))).EnsureSuccessStatusCode();   // free before the first entry
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit())).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}", FundBody("NEW2", type)), HttpStatusCode.Conflict, "FUND_CODE_LOCKED");
        (await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}", FundBody("NEW", type, "Renamed fund"))).EnsureSuccessStatusCode();   // other fields still editable
    }

    [Fact]
    public async Task Opening_balances_are_free_in_draft_need_a_reason_when_active_and_are_audited_BR015()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "OPN", openingCash: 1000, openingBank: 0);
        object Put(string cash, string? reason) => new
        {
            items = new[] { new { accountId = b.Cash, amount = cash, asOfDate = Books.Today().ToString("yyyy-MM-dd") } }, reason,
        };

        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}/opening-balances", Put("2500.00", null)),
            HttpStatusCode.BadRequest, "REASON_REQUIRED");
        (await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}/opening-balances", Put("2500.00", "Cash recount"))).EnsureSuccessStatusCode();
        (await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}/opening-balances", Put("2500.00", null))).EnsureSuccessStatusCode();   // unchanged: no reason needed

        var dash = await b.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}");
        Assert.Equal("2500.00", dash.GetProperty("balance").GetString());
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/funds/{b.FundId}/opening-balances", Put("-1.00", "x")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT reason, old_value::text, new_value::text FROM fl.audit_logs WHERE fund_id = '{b.FundId}' AND action = 'OPENING_BALANCE_CHANGED' AND reason IS NOT NULL", conn);
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Cash recount", r.GetString(0));
        Assert.Contains("1000.00", r.GetString(1), StringComparison.Ordinal);
        Assert.Contains("2500.00", r.GetString(2), StringComparison.Ordinal);
        Assert.False(await r.ReadAsync(TestContext.Current.CancellationToken));           // the no-op save wrote nothing
    }

    [Fact]
    public async Task Members_cannot_manage_funds_or_master_data_but_can_read_active_items()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();

        var denied = new Func<Task<HttpResponseMessage>>[]
        {
            () => member.Session.GetAsync("/api/v1/funds/manage"),
            () => member.Session.PostAsync($"/api/v1/funds/{b.FundId}/close", new { reason = "x" }),
            () => member.Session.PutAsync($"/api/v1/funds/{b.FundId}/opening-balances", new { items = Array.Empty<object>(), reason = "x" }),
            () => member.Session.PostAsync("/api/v1/accounts", new { name = "Sneaky", kind = "CASH", bankName = (string?)null, accountNumberLast4 = (string?)null, isActive = true, sortOrder = 0 }),
            () => member.Session.PostAsync("/api/v1/categories", new { direction = "MONEY_IN", name = "Sneaky", icon = (string?)null, fundId = (Guid?)null, sortOrder = 0 }),
            () => member.Session.PostAsync("/api/v1/payment-modes", new { name = "Sneaky", requiresReference = false, isActive = true, sortOrder = 0 }),
            () => member.Session.PostAsync("/api/v1/fund-types", new { name = "Sneaky", isActive = true, sortOrder = 0 }),
        };
        foreach (var call in denied)
        {
            await ProblemAssert.HasCodeAsync(await call(), HttpStatusCode.Forbidden, "FORBIDDEN");
        }

        Assert.True((await member.Session.GetJsonAsync("/api/v1/accounts")).GetArrayLength() >= 2);

        // Deactivated items disappear for members (but Admins can still list them).
        var food = (await b.Admin.GetJsonAsync("/api/v1/categories?direction=MONEY_OUT")).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == b.CatOut);
        (await b.Admin.PutAsync($"/api/v1/categories/{b.CatOut}", new { name = "Food", icon = (string?)null, fundId = (Guid?)null, isActive = false, sortOrder = 0 })).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await member.Session.GetJsonAsync("/api/v1/categories?direction=MONEY_OUT&includeInactive=true")).EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == b.CatOut);
        Assert.Contains((await b.Admin.GetJsonAsync("/api/v1/categories?direction=MONEY_OUT&includeInactive=true")).EnumerateArray(),
            c => c.GetProperty("id").GetGuid() == b.CatOut && !c.GetProperty("isActive").GetBoolean());
        Assert.Equal("Food", food.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Master_data_names_are_unique_and_changes_are_audited()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();

        var created = await admin.PostAsync("/api/v1/accounts", new { name = "Cash Counter 1", kind = "CASH", bankName = (string?)null, accountNumberLast4 = (string?)null, isActive = true, sortOrder = 5 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/accounts", new
        {
            name = "Cash Counter 1", kind = "CASH", bankName = (string?)null, accountNumberLast4 = (string?)null, isActive = true, sortOrder = 0,
        }), HttpStatusCode.Conflict, "NAME_ALREADY_EXISTS");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/accounts", new
        {
            name = "Bank X", kind = "BANK", bankName = "SBI", accountNumberLast4 = "12345", isActive = true, sortOrder = 0,
        }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync("/api/v1/categories", new
        {
            direction = "MONEY_IN", name = "collection", icon = (string?)null, fundId = (Guid?)null, sortOrder = 0,
        }), HttpStatusCode.Conflict, "NAME_ALREADY_EXISTS");           // case-insensitive within direction + scope

        var id = (await Books.JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Contains("ACCOUNT_CREATED", await ActionsAsync(org.OrganizationId, id.ToString()));
    }

    [Fact]
    public async Task Fund_list_for_admins_shows_each_fund_with_its_balance()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "LSTF", openingCash: 100);
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("50.00"))).EnsureSuccessStatusCode();
        var list = await b.Admin.GetJsonAsync("/api/v1/funds/manage");
        var fund = list.EnumerateArray().Single();
        Assert.Equal("150.00", fund.GetProperty("closingBalance").GetString());
        Assert.Equal("ACTIVE", fund.GetProperty("status").GetString());
    }

    private async Task<List<string>> ActionsAsync(Guid org, string entityId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand(
            $"SELECT action FROM fl.audit_logs WHERE organization_id = '{org}' AND entity_id = '{entityId}' ORDER BY id", conn);
        var result = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await r.ReadAsync(TestContext.Current.CancellationToken))
        {
            result.Add(r.GetString(0));
        }

        return result;
    }
}
