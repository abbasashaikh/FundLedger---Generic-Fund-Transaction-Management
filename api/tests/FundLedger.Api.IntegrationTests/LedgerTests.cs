using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Recording money and the rules around it (PRD §7–§13, BR-005..BR-019).</summary>
[Collection(PostgresTests.Name)]
public sealed class LedgerTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static string Money(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task Worked_example_from_the_schema_doc_balances_correctly_and_transfers_do_not_change_the_fund_total()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, openingCash: 5000, openingBank: 10000);

        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("25000.00"))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense("8500.00"))).EnsureSuccessStatusCode();
        var transfer = await b.Admin.PostAsync("/api/v1/transactions/transfer", b.Transfer("20000.00"));
        var afterTransfer = await Books.JsonAsync(transfer);

        Assert.Equal("31500.00", Money(afterTransfer, "fundClosingBalance"));     // 15,000 + 25,000 − 8,500, unchanged by the transfer

        var accounts = await b.Admin.GetJsonAsync($"/api/v1/accounts/balances?fundId={b.FundId}");
        var cash = accounts.EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == b.Cash);
        var bank = accounts.EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == b.Bank);
        Assert.Equal("1500.00", Money(cash, "closing"));
        Assert.Equal("30000.00", Money(bank, "closing"));
        Assert.Equal("20000.00", Money(cash, "transfersOut"));
        Assert.Equal("20000.00", Money(bank, "transfersIn"));

        var dash = await b.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}");
        Assert.Equal("31500.00", Money(dash, "balance"));
        Assert.Equal("25000.00", Money(dash, "moneyIn"));
        Assert.Equal("8500.00", Money(dash, "moneyOut"));
        Assert.Equal("25000.00", Money(dash, "todayIn"));
        Assert.Equal(3, dash.GetProperty("recent").GetArrayLength());
    }

    [Fact]
    public async Task Entries_get_gap_free_numbers_per_fund_and_financial_year()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "NUM");
        var first = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: new DateOnly(2026, 3, 31))));
        var second = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: new DateOnly(2026, 4, 1))));
        var third = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: new DateOnly(2026, 4, 2))));

        Assert.Equal("NUM-2025-26-000001", first.GetProperty("transaction").GetProperty("txnNumber").GetString());
        Assert.Equal("NUM-2026-27-000001", second.GetProperty("transaction").GetProperty("txnNumber").GetString());   // April starts a new year
        Assert.Equal("NUM-2026-27-000002", third.GetProperty("transaction").GetProperty("txnNumber").GetString());
    }

    [Fact]
    public async Task Parallel_entries_never_share_a_number()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "PAR");
        var tasks = Enumerable.Range(0, 8).Select(_ => b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit())).ToArray();
        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var numbers = new List<string>();
        foreach (var r in responses)
        {
            numbers.Add((await Books.JsonAsync(r)).GetProperty("transaction").GetProperty("txnNumber").GetString()!);
        }

        Assert.Equal(8, numbers.Distinct().Count());
    }

    [Theory]
    [InlineData("0", "AMOUNT")]
    [InlineData("-5.00", "AMOUNT")]
    [InlineData("10.005", "AMOUNT")]
    [InlineData("abc", "AMOUNT")]
    public async Task Invalid_amounts_are_rejected_BR005(string amount, string field)
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var response = await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(amount));
        var problem = await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field.ToLowerInvariant(), out _));
    }

    [Fact]
    public async Task Amount_above_the_configured_maximum_is_rejected()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("1000000.01")),
            HttpStatusCode.BadRequest, "AMOUNT_OUT_OF_RANGE");
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("1000000.00"))).StatusCode);
    }

    [Fact]
    public async Task Expense_needs_a_purpose_BR008_and_transfers_need_two_different_accounts_BR010()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var noPurpose = await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense(purpose: "  "));
        var problem = await ProblemAssert.HasCodeAsync(noPurpose, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("purpose", out _));

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/transfer", b.Transfer(from: b.Cash, to: b.Cash)),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Future_dates_are_rejected_and_members_can_backdate_only_seven_days_but_admins_any_BR_Q03()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: Books.Today().AddDays(1))),
            HttpStatusCode.BadRequest, "DATE_IN_FUTURE");

        Assert.Equal(HttpStatusCode.Created, (await member.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: Books.Today().AddDays(-7)))).StatusCode);
        await ProblemAssert.HasCodeAsync(await member.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: Books.Today().AddDays(-8))),
            HttpStatusCode.BadRequest, "BACKDATE_LIMIT_EXCEEDED");
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(date: Books.Today().AddDays(-200)))).StatusCode);
    }

    [Fact]
    public async Task Category_must_match_direction_be_active_and_belong_to_the_fund()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);

        // Money-out category on a deposit
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(b.Deposit()))!;
        dict["categoryId"] = b.CatOut;
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", dict), HttpStatusCode.BadRequest, "CATEGORY_DIRECTION_MISMATCH");

        // Deactivated category
        var cat = await b.Admin.GetJsonAsync("/api/v1/categories?direction=MONEY_IN");
        var other = cat.EnumerateArray().First(c => c.GetProperty("id").GetGuid() != b.CatIn);
        (await b.Admin.PutAsync($"/api/v1/categories/{other.GetProperty("id").GetGuid()}", new
        {
            name = other.GetProperty("name").GetString(), icon = (string?)null, fundId = (Guid?)null, isActive = false, sortOrder = 0,
        })).EnsureSuccessStatusCode();
        dict["categoryId"] = other.GetProperty("id").GetGuid();
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", dict), HttpStatusCode.BadRequest, "CATEGORY_INACTIVE");

        // A category scoped to another fund can't be used here
        var otherFund = await Books.CreateFundAsync(b, "OTH");
        var scoped = await b.Admin.PostAsync("/api/v1/categories", new { direction = "MONEY_IN", name = "Tent hire", icon = (string?)null, fundId = otherFund, sortOrder = 0 });
        scoped.EnsureSuccessStatusCode();
        dict["categoryId"] = (await Books.JsonAsync(scoped)).GetProperty("id").GetGuid();
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", dict), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Inactive_account_and_payment_mode_that_requires_a_reference_are_enforced()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(mode: b.ModeUpi)),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit(mode: b.ModeUpi, reference: "UPI-8841"))).StatusCode);

        var acc = (await b.Admin.GetJsonAsync("/api/v1/accounts")).EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == b.Cash);
        (await b.Admin.PutAsync($"/api/v1/accounts/{b.Cash}", new
        {
            name = acc.GetProperty("name").GetString(), kind = "CASH", bankName = (string?)null, accountNumberLast4 = (string?)null, isActive = false, sortOrder = 0,
        })).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit()), HttpStatusCode.BadRequest, "ACCOUNT_INACTIVE");
    }

    [Fact]
    public async Task Closed_and_draft_funds_reject_new_entries_until_reopened_BR017()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        (await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/close", new { reason = "Event over" })).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit()), HttpStatusCode.Conflict, "FUND_NOT_ACTIVE");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/reopen", new { reason = "" }), HttpStatusCode.BadRequest, "REASON_REQUIRED");

        (await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/reopen", new { reason = "Late bills" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Created, (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit())).StatusCode);

        var draft = await Books.CreateFundAsync(b, "DRF", activate: false);
        var toDraft = b.Deposit() as object;
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(toDraft))!;
        dict["fundId"] = draft;
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", dict), HttpStatusCode.Conflict, "FUND_NOT_ACTIVE");
    }

    [Fact]
    public async Task Resubmitting_the_same_client_id_returns_the_original_entry_and_counts_once()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var client = Guid.CreateVersion7();

        var first = await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("500.00", clientId: client));
        var second = await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("500.00", clientId: client));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await Books.JsonAsync(first);
        var c = await Books.JsonAsync(second);
        Assert.Equal(a.GetProperty("transaction").GetProperty("id").GetGuid(), c.GetProperty("transaction").GetProperty("id").GetGuid());
        Assert.True(c.GetProperty("duplicate").GetBoolean());
        Assert.Equal("500.00", Money(c, "fundClosingBalance"));

        var member = await b.MemberAsync();
        await ProblemAssert.HasCodeAsync(await member.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit("500.00", clientId: client)),
            HttpStatusCode.Conflict, "DUPLICATE_CLIENT_TXN");
    }

    [Fact]
    public async Task Parallel_submissions_of_one_client_id_record_a_single_entry()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var client = Guid.CreateVersion7();
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("10.00", clientId: client))));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var page = await b.Admin.GetJsonAsync($"/api/v1/transactions?fundId={b.FundId}");
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Members_need_the_capability_and_the_fund_assignment()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var collector = await b.MemberAsync(moneyIn: true, moneyOut: false);

        Assert.Equal(HttpStatusCode.Created, (await collector.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit())).StatusCode);
        await ProblemAssert.HasCodeAsync(await collector.Session.PostAsync("/api/v1/transactions/expense", b.Expense()), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await collector.Session.PostAsync("/api/v1/transactions/transfer", b.Transfer()), HttpStatusCode.Forbidden, "FORBIDDEN");

        var other = await Books.CreateFundAsync(b, "OTH");
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(b.Deposit()))!;
        dict["fundId"] = other;
        await ProblemAssert.HasCodeAsync(await collector.Session.PostAsync("/api/v1/transactions/deposit", dict), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await collector.Session.GetAsync($"/api/v1/dashboard?fundId={other}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Members_without_see_all_only_see_their_own_entries_everywhere()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var mine = await b.MemberAsync(viewAll: false, name: "Private");
        var adminEntry = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("900.00")));
        var ownEntry = await Books.JsonAsync(await mine.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit("100.00")));

        var page = await mine.Session.GetJsonAsync($"/api/v1/transactions?fundId={b.FundId}");
        Assert.True(page.GetProperty("ownOnly").GetBoolean());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal("100.00", Money(page.GetProperty("totals"), "moneyIn"));

        var dash = await mine.Session.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}");
        Assert.True(dash.GetProperty("ownOnly").GetBoolean());
        Assert.Equal("100.00", Money(dash, "todayIn"));

        var theirId = adminEntry.GetProperty("transaction").GetProperty("id").GetGuid();
        await ProblemAssert.HasCodeAsync(await mine.Session.GetAsync($"/api/v1/transactions/{theirId}"), HttpStatusCode.NotFound, "NOT_FOUND");
        var myId = ownEntry.GetProperty("transaction").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await mine.Session.GetAsync($"/api/v1/transactions/{myId}")).StatusCode);
    }

    [Fact]
    public async Task Another_organizations_admin_cannot_see_or_write_to_this_ledger()
    {
        SkipIfNoDb();
        var a = await Books.CreateAsync(_host, "AAA");
        var other = await _host.CreateOrgAsync();
        var intruder = await other.AdminAsync();
        var created = await Books.JsonAsync(await a.Admin.PostAsync("/api/v1/transactions/deposit", a.Deposit("700.00")));
        var txnId = created.GetProperty("transaction").GetProperty("id").GetGuid();

        var attempts = new Func<Task<HttpResponseMessage>>[]
        {
            () => intruder.GetAsync($"/api/v1/transactions/{txnId}"),
            () => intruder.GetAsync($"/api/v1/transactions?fundId={a.FundId}"),
            () => intruder.GetAsync($"/api/v1/dashboard?fundId={a.FundId}"),
            () => intruder.GetAsync($"/api/v1/accounts/balances?fundId={a.FundId}"),
            () => intruder.PostAsync("/api/v1/transactions/deposit", a.Deposit()),
            () => intruder.GetAsync($"/api/v1/funds/{a.FundId}"),
            () => intruder.PostAsync($"/api/v1/funds/{a.FundId}/close", new { reason = "x" }),
            () => intruder.PutAsync($"/api/v1/funds/{a.FundId}/opening-balances", new { items = Array.Empty<object>(), reason = "x" }),
            () => intruder.PutAsync($"/api/v1/categories/{a.CatIn}", new { name = "Hacked", icon = (string?)null, fundId = (Guid?)null, isActive = true, sortOrder = 0 }),
            () => intruder.PutAsync($"/api/v1/accounts/{a.Cash}", new { name = "Hacked", kind = "CASH", bankName = (string?)null, accountNumberLast4 = (string?)null, isActive = true, sortOrder = 0 }),
        };
        foreach (var attempt in attempts)
        {
            await ProblemAssert.HasCodeAsync(await attempt(), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        Assert.Equal("700.00", Money(await a.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={a.FundId}"), "balance"));
        Assert.Equal(0, (await intruder.GetJsonAsync("/api/v1/funds/manage")).GetArrayLength());
    }

    [Fact]
    public async Task Ledger_search_filters_sorting_totals_and_paging_work_together()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, "LST");
        var member = await b.MemberAsync(name: "Imran");
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("300.00", date: Books.Today().AddDays(-3)))).EnsureSuccessStatusCode();
        (await member.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit("100.00"))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense("40.00", purpose: "Tent rope"))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense("60.00", purpose: "Printing banners"))).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/transfer", b.Transfer("50.00"))).EnsureSuccessStatusCode();
        var baseUrl = $"/api/v1/transactions?fundId={b.FundId}";

        var all = await b.Admin.GetJsonAsync(baseUrl);
        Assert.Equal(5, all.GetProperty("items").GetArrayLength());
        Assert.Equal("400.00", Money(all.GetProperty("totals"), "moneyIn"));
        Assert.Equal("100.00", Money(all.GetProperty("totals"), "moneyOut"));
        Assert.Equal("300.00", Money(all.GetProperty("totals"), "net"));

        Assert.Equal(2, (await b.Admin.GetJsonAsync($"{baseUrl}&type=EXPENSE")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"{baseUrl}&q=rope")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"{baseUrl}&q=imran")).GetProperty("items").GetArrayLength());       // search by user
        Assert.Equal(2, (await b.Admin.GetJsonAsync($"{baseUrl}&q=Food")).GetProperty("items").GetArrayLength());        // search by category
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"{baseUrl}&userId={member.Id}")).GetProperty("items").GetArrayLength());
        Assert.Equal(4, (await b.Admin.GetJsonAsync($"{baseUrl}&from={Books.Today():yyyy-MM-dd}")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"{baseUrl}&to={Books.Today().AddDays(-1):yyyy-MM-dd}")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"{baseUrl}&type=TRANSFER&accountId={b.Bank}")).GetProperty("items").GetArrayLength());   // transfer matches either side
        Assert.Equal(0, (await b.Admin.GetJsonAsync($"{baseUrl}&q=%25")).GetProperty("items").GetArrayLength());           // % is literal, not a wildcard

        var highest = await b.Admin.GetJsonAsync($"{baseUrl}&sort=HIGHEST&limit=1");
        Assert.Equal("300.00", Money(highest.GetProperty("items")[0], "amount"));

        var page1 = await b.Admin.GetJsonAsync($"{baseUrl}&limit=2");
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await b.Admin.GetJsonAsync($"{baseUrl}&limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var page3 = await b.Admin.GetJsonAsync($"{baseUrl}&limit=2&cursor={Uri.EscapeDataString(page2.GetProperty("nextCursor").GetString()!)}");
        Assert.Equal(1, page3.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page3.GetProperty("nextCursor").ValueKind);
        var ids = new[] { page1, page2, page3 }.SelectMany(p => p.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())).ToList();
        Assert.Equal(5, ids.Distinct().Count());   // no row repeated or skipped across pages
    }

    [Fact]
    public async Task Every_entry_writes_an_audit_event_and_a_first_revision_snapshot()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var created = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense("75.00")));
        var id = created.GetProperty("transaction").GetProperty("id").GetGuid();

        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var audit = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM fl.audit_logs WHERE entity_id = '{id}' AND action = 'TXN_CREATED' AND fund_id = '{b.FundId}'", conn);
        Assert.Equal(1L, await audit.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await using var rev = new Npgsql.NpgsqlCommand($"SELECT revision, snapshot->>'amount' FROM fl.transaction_revisions WHERE transaction_id = '{id}'", conn);
        await using var reader = await rev.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("75.00", reader.GetString(1));
    }
}
