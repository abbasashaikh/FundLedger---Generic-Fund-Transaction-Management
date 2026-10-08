using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Editing, cancelling and correcting entries, with their history (PRD §11, §14; BR-012..016).</summary>
[Collection(PostgresTests.Name)]
public sealed class AccountabilityTests(PostgresFixture db) : IAsyncLifetime
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

    // ---- edit ------------------------------------------------------------------------------
    [Fact]
    public async Task Member_edits_their_own_entry_in_the_window_and_the_change_is_in_history_audit_and_balance()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync(name: "Imran");
        var (id, rev, txn) = await Books.RecordAsync(member.Session, "deposit", b.Deposit("100.00"));

        var response = await member.Session.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "250.00"; d["purpose"] = "Corrected collection"; }), rev);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var result = await Books.JsonAsync(response);
        Assert.Equal(2, result.GetProperty("transaction").GetProperty("revision").GetInt32());
        Assert.Equal("250.00", Str(result, "fundClosingBalance"));

        var history = await member.Session.GetJsonAsync($"/api/v1/transactions/{id}/history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("EDITED", history[0].GetProperty("kind").GetString());
        Assert.Equal("Imran", history[0].GetProperty("changedBy").GetProperty("name").GetString());
        var changes = history[0].GetProperty("changes").EnumerateArray().ToDictionary(c => Str(c, "field"));
        Assert.Equal("100.00", Str(changes["amount"], "old"));
        Assert.Equal("250.00", Str(changes["amount"], "new"));
        Assert.Equal("Corrected collection", Str(changes["purpose"], "new"));
        Assert.Equal("CREATED", history[1].GetProperty("kind").GetString());

        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT old_value->>'amount', new_value->>'amount' FROM fl.audit_logs WHERE entity_id = '{id}' AND action = 'TXN_UPDATED'", conn);
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("100.00", "250.00"), (r.GetString(0), r.GetString(1)));
    }

    [Fact]
    public async Task Edits_must_name_the_revision_they_saw_and_a_stale_one_is_refused_without_changing_anything()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("100.00"));

        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["reason"] = "why")),
            (HttpStatusCode)428, "PRECONDITION_REQUIRED");

        (await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "110.00"; d["reason"] = "typo"; }), rev)).EnsureSuccessStatusCode();
        // A second screen still holding revision 1 tries to save its own change.
        await ProblemAssert.HasCodeAsync(
            await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "999.00"; d["reason"] = "stale"; }), rev),
            HttpStatusCode.PreconditionFailed, "REVISION_CONFLICT");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "stale cancel" }, rev),
            HttpStatusCode.PreconditionFailed, "REVISION_CONFLICT");

        var now = await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}");
        Assert.Equal("110.00", Str(now.GetProperty("transaction"), "amount"));
        Assert.Equal("ACTIVE", Str(now.GetProperty("transaction"), "status"));
    }

    [Fact]
    public async Task Two_simultaneous_edits_of_the_same_revision_let_exactly_one_win()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("100.00"));

        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = $"{200 + i}.00"; d["reason"] = $"racer {i}"; }), rev)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
        var history = await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}/history");
        Assert.Equal(2, history.GetArrayLength());          // created + exactly one edit: no lost update, no gap
    }

    [Fact]
    public async Task Member_edit_window_closes_but_an_admin_can_still_correct_with_a_reason()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();
        var (id, rev, txn) = await Books.RecordAsync(member.Session, "expense", b.Expense("40.00"));

        var inside = await member.Session.GetJsonAsync($"/api/v1/transactions/{id}");
        Assert.True(inside.GetProperty("canEdit").GetBoolean());
        Assert.False(inside.GetProperty("canCancel").GetBoolean());
        Assert.False(inside.GetProperty("editRequiresReason").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, inside.GetProperty("editableUntil").ValueKind);

        _host.Clock.Offset = TimeSpan.FromMinutes(16);
        try
        {
            var late = await member.Session.GetJsonAsync($"/api/v1/transactions/{id}");
            Assert.False(late.GetProperty("canEdit").GetBoolean());
            await ProblemAssert.HasCodeAsync(await member.Session.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["amount"] = "41.00"), rev),
                HttpStatusCode.Forbidden, "EDIT_WINDOW_EXPIRED");

            await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["amount"] = "41.00"), rev),
                HttpStatusCode.BadRequest, "REASON_REQUIRED");
            Assert.Equal(HttpStatusCode.OK,
                (await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "41.00"; d["reason"] = "Bill said 41"; }), rev)).StatusCode);
        }
        finally
        {
            _host.Clock.Offset = TimeSpan.Zero;
        }

        var history = await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}/history");
        Assert.Equal("Bill said 41", Str(history[0], "reason"));
    }

    [Fact]
    public async Task Members_cannot_edit_other_peoples_entries_or_adjustments_or_without_the_capability()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var mine = await b.MemberAsync(name: "Owner");
        var other = await b.MemberAsync(name: "Other");
        var (id, rev, txn) = await Books.RecordAsync(mine.Session, "deposit", b.Deposit());

        await ProblemAssert.HasCodeAsync(await other.Session.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["amount"] = "5.00"), rev),
            HttpStatusCode.Forbidden, "FORBIDDEN");
        var seen = await other.Session.GetJsonAsync($"/api/v1/transactions/{id}");
        Assert.False(seen.GetProperty("canEdit").GetBoolean());

        var adj = await Books.RecordAsync(b.Admin, "adjustment", new
        {
            fundId = b.FundId, amount = "10.00", txnDate = Books.Today().ToString("yyyy-MM-dd"), txnTime = "00:00", accountId = b.Cash,
            direction = "INCREASE", reason = "Cash count difference", remarks = (string?)null, clientTxnId = (Guid?)null,
        });
        await ProblemAssert.HasCodeAsync(await mine.Session.PutAsync($"/api/v1/transactions/{adj.Id}", Books.EditBody(adj.Txn, d => d["amount"] = "11.00"), adj.Revision),
            HttpStatusCode.Forbidden, "FORBIDDEN");

        // Capability revoked after recording: the window no longer helps.
        (await b.Admin.PutAsync($"/api/v1/users/{mine.Id}/fund-access", new
        {
            items = new[] { new { fundId = b.FundId, canMoneyIn = false, canMoneyOut = true, canTransfer = false, canViewReports = true, canExport = false, canViewAllTxns = true } },
        })).EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(await mine.Session.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["amount"] = "5.00"), rev),
            HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task Edits_follow_the_same_rules_as_new_entries_and_may_keep_a_category_turned_off_since()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("100.00"));

        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "0"; d["reason"] = "x"; }), rev),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["categoryId"] = b.CatOut; d["reason"] = "x"; }), rev),
            HttpStatusCode.BadRequest, "CATEGORY_DIRECTION_MISMATCH");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["txnDate"] = Books.Today().AddDays(1).ToString("yyyy-MM-dd"); d["reason"] = "x"; }), rev),
            HttpStatusCode.BadRequest, "DATE_IN_FUTURE");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["purpose"] = " "; d["reason"] = "x"; }), rev),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        // The category is turned off later; fixing the amount on this entry must still work.
        var food = (await b.Admin.GetJsonAsync("/api/v1/categories?direction=MONEY_IN")).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == b.CatIn);
        (await b.Admin.PutAsync($"/api/v1/categories/{b.CatIn}", new { name = Str(food, "name"), icon = (string?)null, fundId = (Guid?)null, isActive = false, sortOrder = 0 })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "120.00"; d["reason"] = "typo"; }), rev)).StatusCode);
    }

    [Fact]
    public async Task Saving_without_changing_anything_creates_no_new_revision()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("100.00"));
        var same = await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["reason"] = "nothing"), rev);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(1, (await Books.JsonAsync(same)).GetProperty("transaction").GetProperty("revision").GetInt32());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}/history")).GetArrayLength());
    }

    // ---- cancel ----------------------------------------------------------------------------
    [Fact]
    public async Task Admin_cancels_with_a_reason_and_the_entry_leaves_the_balance_but_stays_in_history()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, openingCash: 1000);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("500.00"));
        Assert.Equal("1500.00", Str(await b.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}"), "balance"));

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "no" }, rev), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "Entered twice by mistake" }), (HttpStatusCode)428, "PRECONDITION_REQUIRED");

        var cancel = await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "Entered twice by mistake" }, rev);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var done = (await Books.JsonAsync(cancel));
        Assert.Equal("1000.00", Str(done, "fundClosingBalance"));
        var t = done.GetProperty("transaction");
        Assert.Equal("CANCELLED", Str(t, "status"));
        Assert.Equal(2, t.GetProperty("revision").GetInt32());
        Assert.Equal("Entered twice by mistake", Str(t, "cancellationReason"));
        Assert.Equal("Test Admin", t.GetProperty("cancelledBy").GetProperty("name").GetString());

        Assert.Equal("1000.00", Str(await b.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}"), "balance"));
        var list = await b.Admin.GetJsonAsync($"/api/v1/transactions?fundId={b.FundId}&status=CANCELLED");
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());           // still visible (BR-012)
        Assert.Equal("0.00", Str(list.GetProperty("totals"), "moneyIn"));       // but not counted

        var history = await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}/history");
        Assert.Equal("CANCELLED", history[0].GetProperty("kind").GetString());
        Assert.Equal("Entered twice by mistake", Str(history[0], "reason"));

        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "again please" }, 2), HttpStatusCode.Conflict, "TXN_CANCELLED_IMMUTABLE");
        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "9.00"; d["reason"] = "x"; }), 2),
            HttpStatusCode.Conflict, "TXN_CANCELLED_IMMUTABLE");
        var detail = await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}");
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        Assert.False(detail.GetProperty("canCancel").GetBoolean());
    }

    [Fact]
    public async Task Members_cannot_cancel_even_their_own_entries()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();
        var (id, rev, _) = await Books.RecordAsync(member.Session, "deposit", b.Deposit());
        await ProblemAssert.HasCodeAsync(await member.Session.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "I made a mistake" }, rev),
            HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task Closed_funds_refuse_edits_and_cancellations_until_reopened()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (id, rev, txn) = await Books.RecordAsync(b.Admin, "deposit", b.Deposit("100.00"));
        (await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/close", new { reason = "done" })).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await b.Admin.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "1.00"; d["reason"] = "x"; }), rev),
            HttpStatusCode.Conflict, "FUND_NOT_ACTIVE");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "fund closed" }, rev), HttpStatusCode.Conflict, "FUND_NOT_ACTIVE");
        Assert.False((await b.Admin.GetJsonAsync($"/api/v1/transactions/{id}")).GetProperty("canEdit").GetBoolean());
    }

    // ---- adjustment ----------------------------------------------------------------------
    [Fact]
    public async Task Adjustments_are_admin_only_need_a_reason_and_move_the_account_balance_in_either_direction_BR016()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, openingCash: 1000);
        object Adj(string direction, string amount, string reason) => new
        {
            fundId = b.FundId, amount, txnDate = Books.Today().ToString("yyyy-MM-dd"), txnTime = "00:00", accountId = b.Cash, direction, reason,
            remarks = (string?)null, clientTxnId = (Guid?)null,
        };

        var member = await b.MemberAsync(transfer: true);
        await ProblemAssert.HasCodeAsync(await member.Session.PostAsync("/api/v1/transactions/adjustment", Adj("DECREASE", "100.00", "Cash count shortfall")), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/transactions/adjustment", Adj("DECREASE", "100.00", "short")), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var down = await b.Admin.PostAsync("/api/v1/transactions/adjustment", Adj("DECREASE", "100.00", "Cash count shortfall after reconciliation"));
        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        Assert.Equal("900.00", Str(await Books.JsonAsync(down), "fundClosingBalance"));
        var up = await Books.JsonAsync(await b.Admin.PostAsync("/api/v1/transactions/adjustment", Adj("INCREASE", "30.00", "Found loose notes in the box")));
        Assert.Equal("930.00", Str(up, "fundClosingBalance"));
        Assert.Equal("ADJUSTMENT", Str(up.GetProperty("transaction"), "type"));
        Assert.Equal("INCREASE", Str(up.GetProperty("transaction"), "adjustmentDirection"));

        var accounts = await b.Admin.GetJsonAsync($"/api/v1/accounts/balances?fundId={b.FundId}");
        Assert.Equal("930.00", Str(accounts.EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == b.Cash), "closing"));
        Assert.Equal("-70.00", Str(accounts.EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == b.Cash), "adjustments"));
    }

    // ---- isolation ---------------------------------------------------------------------------
    [Fact]
    public async Task Another_organizations_admin_cannot_edit_cancel_or_read_history()
    {
        SkipIfNoDb();
        var a = await Books.CreateAsync(_host, "AAA");
        var other = await _host.CreateOrgAsync();
        var intruder = await other.AdminAsync();
        var (id, rev, txn) = await Books.RecordAsync(a.Admin, "deposit", a.Deposit("100.00"));

        await ProblemAssert.HasCodeAsync(await intruder.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => { d["amount"] = "1.00"; d["reason"] = "x"; }), rev), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await intruder.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "not yours" }, rev), HttpStatusCode.NotFound, "NOT_FOUND");
        await ProblemAssert.HasCodeAsync(await intruder.GetAsync($"/api/v1/transactions/{id}/history"), HttpStatusCode.NotFound, "NOT_FOUND");
        Assert.Equal("100.00", Str((await a.Admin.GetJsonAsync($"/api/v1/transactions/{id}")).GetProperty("transaction"), "amount"));
    }

    // ---- no hard deletes (BR-012) ---------------------------------------------------------------
    [Fact]
    public void No_endpoint_can_delete_entries_revisions_attachments_or_audit_events()
    {
        SkipIfNoDb();
        var endpoints = _host.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        string[] protectedWords = ["transaction", "revision", "attachment", "audit"];
        var deletes = endpoints
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods.Contains("DELETE") == true)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(route => protectedWords.Any(w => route.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(deletes);
    }

    // ---- audit log ------------------------------------------------------------------------------
    [Fact]
    public async Task Audit_log_is_admin_only_filterable_and_tenant_scoped()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync(name: "Imran");
        var (id, rev, txn) = await Books.RecordAsync(member.Session, "deposit", b.Deposit("100.00"));
        (await member.Session.PutAsync($"/api/v1/transactions/{id}", Books.EditBody(txn, d => d["amount"] = "120.00"), rev)).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await member.Session.GetAsync("/api/v1/audit-logs"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await ProblemAssert.HasCodeAsync(await member.Session.GetAsync("/api/v1/audit-logs/export"), HttpStatusCode.Forbidden, "FORBIDDEN");

        var all = await b.Admin.GetJsonAsync("/api/v1/audit-logs?limit=200");
        var actions = all.GetProperty("items").EnumerateArray().Select(i => Str(i, "action")).ToList();
        Assert.Contains("TXN_UPDATED", actions);
        Assert.Contains("FUND_ACTIVATED", actions);
        Assert.Contains("LOGIN", actions);

        var tx = await b.Admin.GetJsonAsync("/api/v1/audit-logs?group=TRANSACTIONS");
        Assert.All(tx.GetProperty("items").EnumerateArray(), i => Assert.StartsWith("TXN_", Str(i, "action"), StringComparison.Ordinal));
        var edit = tx.GetProperty("items").EnumerateArray().First(i => Str(i, "action") == "TXN_UPDATED");
        Assert.Equal("Imran", edit.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal("100.00", Str(edit.GetProperty("oldValue"), "amount"));
        Assert.Equal("120.00", Str(edit.GetProperty("newValue"), "amount"));
        Assert.Equal(b.FundId, edit.GetProperty("fundId").GetGuid());

        Assert.Equal(1, (await b.Admin.GetJsonAsync($"/api/v1/audit-logs?action=txn_updated&userId={member.Id}")).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await b.Admin.GetJsonAsync($"/api/v1/audit-logs?q={id}&group=TRANSACTIONS&action=TXN_UPDATED")).GetProperty("items").GetArrayLength());
        Assert.Equal(0, (await b.Admin.GetJsonAsync($"/api/v1/audit-logs?from={Books.Today().AddDays(1):yyyy-MM-dd}")).GetProperty("items").GetArrayLength());
        await ProblemAssert.HasCodeAsync(await b.Admin.GetAsync("/api/v1/audit-logs?group=NOPE"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var page1 = await b.Admin.GetJsonAsync("/api/v1/audit-logs?limit=3");
        Assert.Equal(3, page1.GetProperty("items").GetArrayLength());
        var page2 = await b.Admin.GetJsonAsync($"/api/v1/audit-logs?limit=3&cursor={Uri.EscapeDataString(page1.GetProperty("nextCursor").GetString()!)}");
        var ids = page1.GetProperty("items").EnumerateArray().Concat(page2.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var other = await _host.CreateOrgAsync();
        var intruder = await other.AdminAsync();
        var theirs = await intruder.GetJsonAsync("/api/v1/audit-logs?limit=200");
        Assert.DoesNotContain(theirs.GetProperty("items").EnumerateArray(), i => i.TryGetProperty("fundId", out var f) && f.ValueKind != JsonValueKind.Null && f.GetGuid() == b.FundId);
    }

    [Fact]
    public async Task Audit_csv_export_neutralises_formulas_and_is_itself_audited()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var evil = b.Expense("5.00", purpose: "=HYPERLINK(\"http://evil.test\",\"click\")");
        var (id, _, _) = await Books.RecordAsync(b.Admin, "expense", evil);
        (await b.Admin.PostAsync($"/api/v1/transactions/{id}/cancel", new { reason = "+cmd|' /C calc'!A0" }, 1)).EnsureSuccessStatusCode();

        var response = await b.Admin.GetAsync("/api/v1/audit-logs/export?group=TRANSACTIONS");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);                                 // BOM so Excel reads UTF-8
        var csv = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Time,User,Action", csv, StringComparison.Ordinal);
        Assert.Contains("TXN_CANCELLED", csv, StringComparison.Ordinal);
        Assert.Contains("\"'+cmd|' /C calc'!A0\"", csv, StringComparison.Ordinal);                // reason cell neutralised
        Assert.DoesNotContain(",\"=HYPERLINK", csv, StringComparison.Ordinal);                      // never a bare formula cell

        var log = await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=AUDIT_EXPORTED");
        Assert.Equal(1, log.GetProperty("items").GetArrayLength());
        Assert.Equal("CSV", Str(log.GetProperty("items")[0].GetProperty("newValue"), "format"));
    }
}
