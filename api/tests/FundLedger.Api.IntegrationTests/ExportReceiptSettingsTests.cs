using System.Net;
using System.Text;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;
using FundLedger.Application.Errors;
using FundLedger.Application.Reports;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Exports (TRD §10.2), Money In receipts (§10.3) and the Settings screen (App Flow §5.5).</summary>
[Collection(PostgresTests.Name)]
public sealed class ExportReceiptSettingsTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static object ExportBody(Books b, string report = "MONEY_IN", string format = "CSV") =>
        new { reportCode = report, format, fundId = b.FundId, from = (string?)null, to = (string?)null, type = (string?)null, categoryId = (Guid?)null, accountId = (Guid?)null, userId = (Guid?)null, paymentModeId = (Guid?)null };

    private static async Task<JsonElement> WaitForAsync(Session s, string path)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await s.GetJsonAsync(path);
            if (Str(job, "status") is not ("QUEUED" or "RUNNING"))
            {
                return job;
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The export did not finish.");
    }

    private async Task<(Books B, (Guid Id, Session Session) Treasurer)> WithTreasurerAsync()
    {
        var b = await Books.CreateAsync(_host);
        var m = await b.Org.CreateMemberAsync(b.Admin, "Treasurer");
        (await b.Admin.PutAsync($"/api/v1/users/{m.Id}/fund-access", new
        {
            items = new[] { new { fundId = b.FundId, canMoneyIn = true, canMoneyOut = true, canTransfer = false, canViewReports = true, canExport = true, canViewAllTxns = true } },
        })).EnsureSuccessStatusCode();
        var s = await Session.LoginAsync(_host, m.Mobile, m.TemporaryPin);
        await s.ChangePinAsync(m.TemporaryPin, "730264");
        return (b, (m.Id, s));
    }

    // ---- exports ----------------------------------------------------------------------------
    [Fact]
    public async Task An_export_runs_in_the_background_downloads_once_ready_and_is_audited()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("2500.00"))).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/exports", ExportBody(b)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");   // no Idempotency-Key

        var key = Guid.NewGuid();
        var accepted = await b.Admin.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var job = await Books.JsonAsync(accepted);
        var id = job.GetProperty("id").GetGuid();

        var done = await WaitForAsync(b.Admin, $"/api/v1/exports/{id}");
        Assert.Equal("SUCCEEDED", Str(done, "status"));
        Assert.Equal(1, done.GetProperty("rowCount").GetInt32());

        var file = await b.Admin.GetAsync($"/api/v1/exports/{id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("text/csv", file.Content.Headers.ContentType?.MediaType);
        var csv = Encoding.UTF8.GetString(await file.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Money in — Fund IJT26", csv, StringComparison.Ordinal);
        Assert.Contains("2500.00", csv, StringComparison.Ordinal);

        // The same Idempotency-Key returns the same job instead of a second export.
        var again = await b.Admin.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await Books.JsonAsync(again)).GetProperty("id").GetGuid());
        Assert.Equal(1, (await b.Admin.GetJsonAsync("/api/v1/exports")).GetArrayLength());

        var audit = await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=EXPORT_PERFORMED");
        var entry = audit.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("MONEY_IN", Str(entry.GetProperty("newValue"), "report"));
        Assert.Equal(1, entry.GetProperty("newValue").GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task Excel_and_pdf_exports_produce_real_files()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, openingCash: 1000);
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("125000.00"))).EnsureSuccessStatusCode();
        foreach (var (format, magic) in new[] { ("XLSX", "PK"), ("PDF", "%PDF") })
        {
            var job = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/exports", ExportBody(b, "FUND_SUMMARY", format), idempotencyKey: Guid.NewGuid()));
            var id = job.GetProperty("id").GetGuid();
            Assert.Equal("SUCCEEDED", Str(await WaitForAsync(b.Admin, $"/api/v1/exports/{id}"), "status"));
            var bytes = await (await b.Admin.GetAsync($"/api/v1/exports/{id}/file")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.True(bytes.Length > 1000, $"{format} is suspiciously small ({bytes.Length} bytes)");
            Assert.Equal(magic, Encoding.ASCII.GetString(bytes, 0, magic.Length));
        }
    }

    [Fact]
    public async Task Exports_need_the_export_capability_and_belong_to_their_requester()
    {
        SkipIfNoDb();
        var (b, treasurer) = await WithTreasurerAsync();
        var viewer = await b.MemberAsync(name: "Viewer");          // can view reports, cannot export

        await ProblemAssert.HasCodeAsync(await viewer.Session.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: Guid.NewGuid()), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await treasurer.Session.PostAsync("/api/v1/exports", new { reportCode = "NOPE", format = "CSV", fundId = b.FundId }, idempotencyKey: Guid.NewGuid()),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await treasurer.Session.PostAsync("/api/v1/exports", ExportBody(b, format: "DOCX"), idempotencyKey: Guid.NewGuid()),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var job = await Books.JsonAsync(await treasurer.Session.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: Guid.NewGuid()));
        var id = job.GetProperty("id").GetGuid();
        await WaitForAsync(treasurer.Session, $"/api/v1/exports/{id}");

        // Not even an Admin sees someone else's export; another organization certainly does not.
        await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync($"/api/v1/exports/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync($"/api/v1/exports/{id}/file"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal(0, (await b.Admin.GetJsonAsync("/api/v1/exports")).GetArrayLength());
        var intruder = await (await _host.CreateOrgAsync()).AdminAsync();
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/exports/{id}/file"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task A_members_export_contains_only_their_own_entries()
    {
        SkipIfNoDb();
        var (b, treasurer) = await WithTreasurerAsync();
        // Same capability, but not allowed to see other people's entries.
        (await b.Admin.PutAsync($"/api/v1/users/{treasurer.Id}/fund-access", new
        {
            items = new[] { new { fundId = b.FundId, canMoneyIn = true, canMoneyOut = true, canTransfer = false, canViewReports = true, canExport = true, canViewAllTxns = false } },
        })).EnsureSuccessStatusCode();
        (await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("111.00"))).EnsureSuccessStatusCode();
        (await treasurer.Session.PostAsync("/api/v1/transactions/deposit", b.Deposit("222.00"))).EnsureSuccessStatusCode();

        var job = await Books.JsonAsync(await treasurer.Session.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: Guid.NewGuid()));
        var id = job.GetProperty("id").GetGuid();
        Assert.Equal("SUCCEEDED", Str(await WaitForAsync(treasurer.Session, $"/api/v1/exports/{id}"), "status"));
        var csv = Encoding.UTF8.GetString(await (await treasurer.Session.GetAsync($"/api/v1/exports/{id}/file")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains("222.00", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("111.00", csv, StringComparison.Ordinal);
        Assert.Contains("My transactions only", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_files_expire_after_24_hours()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var job = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/exports", ExportBody(b), idempotencyKey: Guid.NewGuid()));
        var id = job.GetProperty("id").GetGuid();
        await WaitForAsync(b.Admin, $"/api/v1/exports/{id}");
        Assert.Equal(HttpStatusCode.OK, (await b.Admin.GetAsync($"/api/v1/exports/{id}/file")).StatusCode);

        _host.Clock.Offset = TimeSpan.FromHours(25);
        try
        {
            Assert.Equal("EXPIRED", Str(await b.Admin.GetJsonAsync($"/api/v1/exports/{id}"), "status"));
            await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync($"/api/v1/exports/{id}/file"), HttpStatusCode.Conflict, "EXPORT_EXPIRED");
        }
        finally
        {
            _host.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Csv_cells_that_look_like_formulas_are_neutralised()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        (await b.Admin.PostAsync("/api/v1/transactions/expense", b.Expense("5.00", purpose: "=HYPERLINK(\"http://evil.test\")"))).EnsureSuccessStatusCode();
        var job = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/exports", ExportBody(b, "MONEY_OUT"), idempotencyKey: Guid.NewGuid()));
        var id = job.GetProperty("id").GetGuid();
        await WaitForAsync(b.Admin, $"/api/v1/exports/{id}");
        var csv = Encoding.UTF8.GetString(await (await b.Admin.GetAsync($"/api/v1/exports/{id}/file")).Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Contains("\"'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.DoesNotContain(",\"=HYPERLINK", csv, StringComparison.Ordinal);
    }

    // ---- receipts ---------------------------------------------------------------------------
    [Fact]
    public async Task A_money_in_receipt_is_a_pdf_audited_and_follows_the_entrys_access_rules()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var mine = await b.MemberAsync(viewAll: false, name: "Collector");
        var other = await b.MemberAsync(viewAll: false, name: "Other");
        var (id, _, _) = await Books.RecordAsync(mine.Session, "deposit", b.Deposit("125000.50"));

        var response = await mine.Session.GetAsync($"/api/v1/transactions/{id}/receipt.pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("receipt-IJT26", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty, StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        // Same visibility as the entry: another own-only member and another organization get a 404.
        await ProblemAssert.HasCodeAsync(await other.Session.GetAsync($"/api/v1/transactions/{id}/receipt.pdf"), HttpStatusCode.NotFound, "NOT_FOUND");
        var intruder = await (await _host.CreateOrgAsync()).AdminAsync();
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/transactions/{id}/receipt.pdf"), HttpStatusCode.NotFound, "NOT_FOUND");

        var audit = await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=RECEIPT_GENERATED");
        var item = audit.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(id.ToString(), Str(item, "entityId"));
        Assert.Equal(1, item.GetProperty("newValue").GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Receipts_are_for_money_in_only_and_can_be_switched_off_and_cancelled_entries_still_print()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var expense = await Books.RecordAsync(b.Admin, "expense", b.Expense());
        await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync($"/api/v1/transactions/{expense.Id}/receipt.pdf"), HttpStatusCode.BadRequest, "RECEIPT_NOT_AVAILABLE");

        var deposit = await Books.RecordAsync(b.Admin, "deposit", b.Deposit());
        (await b.Admin.PostAsync($"/api/v1/transactions/{deposit.Id}/cancel", new { reason = "Entered twice" }, deposit.Revision)).EnsureSuccessStatusCode();
        var cancelled = await b.Admin.GetAsync($"/api/v1/transactions/{deposit.Id}/receipt.pdf");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);       // printed with a CANCELLED watermark

        var settings = await b.Admin.GetJsonAsync("/api/v1/settings");
        Assert.True(settings.GetProperty("receipts").GetProperty("enabled").GetBoolean());
        (await b.Admin.PutAsync("/api/v1/settings", Edit(settings, s => s["receipts"]!["enabled"] = false))).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync($"/api/v1/transactions/{deposit.Id}/receipt.pdf"), HttpStatusCode.BadRequest, "RECEIPT_NOT_AVAILABLE");
        Assert.False((await b.Admin.GetJsonAsync("/api/v1/me")).GetProperty("organization").GetProperty("receiptsEnabled").GetBoolean());
    }

    [Fact]
    public void Receipts_are_limited_to_60_per_user_per_hour_TR068()
    {
        var limiter = new ReceiptRateLimiter();
        var user = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < ReceiptRateLimiter.PerHour; i++)
        {
            limiter.Take(user, start.AddSeconds(i));
        }

        Assert.Throws<RateLimitedException>(() => limiter.Take(user, start.AddMinutes(30)));
        limiter.Take(Guid.NewGuid(), start.AddMinutes(30));                  // someone else is unaffected
        limiter.Take(user, start.AddMinutes(61));                            // the window slides
    }

    // ---- settings ---------------------------------------------------------------------------
    private static Dictionary<string, object?> Edit(JsonElement current, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(current.GetRawText())!.AsObject();
        change(node);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(node.ToJsonString())!;
    }

    [Fact]
    public async Task Settings_are_admin_only_validated_applied_and_audited_with_old_and_new_values()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();
        await ProblemAssert.HasCodeAsync(await member.Session.GetAsync("/api/v1/settings"), HttpStatusCode.Forbidden, "FORBIDDEN");

        var current = await b.Admin.GetJsonAsync("/api/v1/settings");
        Assert.Equal(15, current.GetProperty("transactions").GetProperty("editWindowMinutes").GetInt32());
        Assert.Equal("1000000.00", Str(current.GetProperty("transactions"), "maxAmount"));

        await ProblemAssert.HasCodeAsync(await member.Session.PutAsync("/api/v1/settings", Edit(current, _ => { })), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync("/api/v1/settings", Edit(current, s => s["transactions"]!["editWindowMinutes"] = 5000)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync("/api/v1/settings", Edit(current, s => s["transactions"]!["maxAmount"] = "-5")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync("/api/v1/settings", Edit(current, s => s["organization"]!["currencyCode"] = "USD")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync("/api/v1/settings", Edit(current, s => s["organization"]!["name"] = " ")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var saved = await Books.JsonAsync(await b.Admin.PutAsync("/api/v1/settings", Edit(current, s =>
        {
            s["transactions"]!["editWindowMinutes"] = 30;
            s["transactions"]!["maxAmount"] = "500000";
            s["organization"]!["registrationNumber"] = "Reg. 123/2020";
            s["receipts"]!["footerText"] = "Thank you for your support.";
        })));
        Assert.Equal(30, saved.GetProperty("transactions").GetProperty("editWindowMinutes").GetInt32());
        Assert.Equal("500000.00", Str(saved.GetProperty("transactions"), "maxAmount"));
        Assert.Equal("Reg. 123/2020", Str(saved.GetProperty("organization"), "registrationNumber"));

        // The new maximum applies at once.
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/deposit", b.Deposit("600000.00")), HttpStatusCode.BadRequest, "AMOUNT_OUT_OF_RANGE");

        var log = (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=SETTINGS_CHANGED")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(15, log.GetProperty("oldValue").GetProperty("txn.edit_window_minutes").GetInt32());
        Assert.Equal(30, log.GetProperty("newValue").GetProperty("txn.edit_window_minutes").GetInt32());
        Assert.Equal("500000.00", Str(log.GetProperty("newValue"), "txn.max_amount"));
        Assert.Equal("Reg. 123/2020", Str(log.GetProperty("newValue"), "organization.registrationNumber"));

        // Saving the same values again is a no-op and writes no second audit event.
        (await b.Admin.PutAsync("/api/v1/settings", Edit(saved, _ => { }))).EnsureSuccessStatusCode();
        Assert.Equal(1, (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=SETTINGS_CHANGED")).GetProperty("items").GetArrayLength());

        // Another organization's settings are untouched.
        var other = await (await _host.CreateOrgAsync()).AdminAsync();
        Assert.Equal(15, (await other.GetJsonAsync("/api/v1/settings")).GetProperty("transactions").GetProperty("editWindowMinutes").GetInt32());
    }
}
