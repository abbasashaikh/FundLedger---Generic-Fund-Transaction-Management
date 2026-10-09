using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>The offline outbox endpoint (TRD §8.3, ADR-0005, PRD §29: "the same client transaction cannot be inserted twice").</summary>
[Collection(PostgresTests.Name)]
public sealed class SyncTests(PostgresFixture db) : IAsyncLifetime
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

    private static string Today(Books b, int daysBack = 0) => Books.Today().AddDays(-daysBack).ToString("yyyy-MM-dd");

    private static Dictionary<string, object?> Deposit(Books b, Guid id, string amount = "100.00", string? purpose = "Collection", DateTimeOffset? at = null) => new()
    {
        ["clientTxnId"] = id, ["type"] = "DEPOSIT", ["clientCreatedAt"] = at ?? DateTimeOffset.UtcNow.AddMinutes(-20), ["fundId"] = b.FundId, ["amount"] = amount,
        ["txnDate"] = Today(b), ["txnTime"] = "00:00", ["categoryId"] = b.CatIn, ["accountId"] = b.Cash, ["fromAccountId"] = null, ["toAccountId"] = null,
        ["paymentModeId"] = b.ModeCash, ["receivedFrom"] = "Area 4", ["paidTo"] = null, ["purpose"] = purpose, ["referenceNumber"] = null, ["remarks"] = null,
    };

    private static Dictionary<string, object?> Expense(Books b, Guid id, string amount = "40.00") => new()
    {
        ["clientTxnId"] = id, ["type"] = "EXPENSE", ["clientCreatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-15), ["fundId"] = b.FundId, ["amount"] = amount,
        ["txnDate"] = Today(b), ["txnTime"] = "00:00", ["categoryId"] = b.CatOut, ["accountId"] = b.Cash, ["fromAccountId"] = null, ["toAccountId"] = null,
        ["paymentModeId"] = b.ModeCash, ["receivedFrom"] = null, ["paidTo"] = "Caterer", ["purpose"] = "Lunch", ["referenceNumber"] = null, ["remarks"] = null,
    };

    private static Dictionary<string, object?> Transfer(Books b, Guid id) => new()
    {
        ["clientTxnId"] = id, ["type"] = "TRANSFER", ["clientCreatedAt"] = DateTimeOffset.UtcNow.AddMinutes(-10), ["fundId"] = b.FundId, ["amount"] = "25.00",
        ["txnDate"] = Today(b), ["txnTime"] = "00:00", ["categoryId"] = null, ["accountId"] = null, ["fromAccountId"] = b.Cash, ["toAccountId"] = b.Bank,
        ["paymentModeId"] = null, ["receivedFrom"] = null, ["paidTo"] = null, ["purpose"] = "Cash to bank", ["referenceNumber"] = null, ["remarks"] = null,
    };

    private static object Batch(params Dictionary<string, object?>[] items) => new { items };

    private static async Task<List<JsonElement>> SyncAsync(Session s, params Dictionary<string, object?>[] items)
    {
        var response = await s.PostAsync("/api/v1/sync/transactions", Batch(items));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Books.JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private async Task<long> CountAsync(Guid fundId, string? where = null)
    {
        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM fl.transactions WHERE fund_id = '{fundId}' {where}", conn);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task A_queued_batch_is_recorded_with_server_numbers_in_order_and_the_device_clock_kept_for_audit_only()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host, openingCash: 1000);
        var (d, e, t) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var deviceClock = DateTimeOffset.UtcNow.AddHours(-30);
        var dep = Deposit(b, d, "500.00", at: deviceClock);

        var results = await SyncAsync(b.Admin, dep, Expense(b, e), Transfer(b, t));
        Assert.All(results, r => Assert.Equal("CREATED", Str(r, "result")));
        Assert.Equal([d, e, t], results.Select(r => r.GetProperty("clientTxnId").GetGuid()).ToArray());
        var numbers = results.Select(r => Str(r.GetProperty("transaction"), "txnNumber")).ToList();
        Assert.EndsWith("-000001", numbers[0], StringComparison.Ordinal);
        Assert.EndsWith("-000003", numbers[2], StringComparison.Ordinal);                 // assigned by the server, in queue order

        // created_at is the server's clock (TR-042); the device's is kept separately and the entry is marked OFFLINE.
        var created = results[0].GetProperty("transaction").GetProperty("createdAt").GetDateTimeOffset();
        Assert.True(DateTimeOffset.UtcNow - created < TimeSpan.FromMinutes(2));
        Assert.Equal("OFFLINE", Str(results[0].GetProperty("transaction"), "source"));
        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT client_created_at FROM fl.transactions WHERE client_txn_id = '{d}'", conn);
        var stored = (DateTime)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        Assert.True(Math.Abs((new DateTimeOffset(stored, TimeSpan.Zero) - deviceClock).TotalSeconds) < 2);

        // The books are right: 1000 + 500 - 40 (transfer moves money between accounts).
        Assert.Equal("1460.00", Str(await b.Admin.GetJsonAsync($"/api/v1/dashboard?fundId={b.FundId}"), "balance"));
        Assert.Equal(3, await CountAsync(b.FundId));
    }

    [Fact]
    public async Task Sending_the_same_batch_again_creates_nothing_and_returns_the_existing_entries()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var (d, e) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var first = await SyncAsync(b.Admin, Deposit(b, d), Expense(b, e));
        var again = await SyncAsync(b.Admin, Deposit(b, d), Expense(b, e));

        Assert.All(again, r => Assert.Equal("DUPLICATE", Str(r, "result")));
        Assert.Equal(Str(first[0].GetProperty("transaction"), "id"), Str(again[0].GetProperty("transaction"), "id"));
        Assert.Equal(2, await CountAsync(b.FundId));
    }

    [Fact]
    public async Task The_same_batch_sent_four_times_in_parallel_yields_one_entry_per_client_id_PRD_29()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        Dictionary<string, object?>[] Items() => ids.Select(i => Deposit(b, i, "10.00")).ToArray();

        var calls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SyncAsync(b.Admin, Items())));

        var all = calls.SelectMany(c => c).ToList();
        Assert.Equal(20, all.Count);
        Assert.All(all, r => Assert.Contains(Str(r, "result"), new[] { "CREATED", "DUPLICATE" }));        // a lost race is DUPLICATE, never an error
        Assert.Equal(5, all.Count(r => Str(r, "result") == "CREATED"));
        Assert.Equal(5, await CountAsync(b.FundId));
        Assert.Equal(5, all.Select(r => Str(r.GetProperty("transaction"), "id")).Distinct().Count());
        // Numbers are gap-free: the rows that lost a race did not burn a number.
        var numbers = all.Select(r => Str(r.GetProperty("transaction"), "txnNumber")).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(5, numbers.Count);
        Assert.EndsWith("-000005", numbers[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_are_checked_again_at_sync_time_and_one_rejection_does_not_hold_back_the_others()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var good = Guid.CreateVersion7();
        var future = Deposit(b, Guid.CreateVersion7());
        future["txnDate"] = Today(b, -3);
        var zero = Deposit(b, Guid.CreateVersion7(), "0");
        var noPurpose = Deposit(b, Guid.CreateVersion7(), purpose: " ");
        var adjustment = Deposit(b, Guid.CreateVersion7());
        adjustment["type"] = "ADJUSTMENT";

        var results = await SyncAsync(b.Admin, future, Deposit(b, good), zero, noPurpose, adjustment);
        Assert.Equal("REJECTED", Str(results[0], "result"));
        Assert.Equal("DATE_IN_FUTURE", Str(results[0], "errorCode"));
        Assert.Equal("CREATED", Str(results[1], "result"));
        Assert.Equal("REJECTED", Str(results[2], "result"));
        Assert.True(results[2].GetProperty("fieldErrors").TryGetProperty("amount", out _));
        Assert.True(results[3].GetProperty("fieldErrors").TryGetProperty("purpose", out _));
        Assert.Equal("REJECTED", Str(results[4], "result"));
        Assert.Equal(1, await CountAsync(b.FundId));
    }

    [Fact]
    public async Task A_fund_closed_or_a_category_turned_off_or_a_permission_withdrawn_while_offline_rejects_the_entry_TR041()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync(moneyIn: true, moneyOut: false);

        // The member lost Money Out; their queued expense is rejected, their deposit is fine.
        var r1 = await SyncAsync(member.Session, Expense(b, Guid.CreateVersion7()), Deposit(b, Guid.CreateVersion7()));
        Assert.Equal(("REJECTED", "FORBIDDEN"), (Str(r1[0], "result"), Str(r1[0], "errorCode")));
        Assert.Equal("CREATED", Str(r1[1], "result"));

        // Category switched off.
        var cat = (await b.Admin.GetJsonAsync("/api/v1/categories?direction=MONEY_IN")).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == b.CatIn);
        (await b.Admin.PutAsync($"/api/v1/categories/{b.CatIn}", new { name = Str(cat, "name"), icon = (string?)null, fundId = (Guid?)null, isActive = false, sortOrder = 0 })).EnsureSuccessStatusCode();
        var r2 = await SyncAsync(b.Admin, Deposit(b, Guid.CreateVersion7()));
        Assert.Equal(("REJECTED", "CATEGORY_INACTIVE"), (Str(r2[0], "result"), Str(r2[0], "errorCode")));

        // Fund closed: nothing is forced in.
        (await b.Admin.PostAsync($"/api/v1/funds/{b.FundId}/close", new { reason = "done" })).EnsureSuccessStatusCode();
        var r3 = await SyncAsync(b.Admin, Expense(b, Guid.CreateVersion7()));
        Assert.Equal(("REJECTED", "FUND_NOT_ACTIVE"), (Str(r3[0], "result"), Str(r3[0], "errorCode")));
        Assert.Equal(1, await CountAsync(b.FundId));
    }

    [Fact]
    public async Task A_client_id_cannot_be_borrowed_by_another_user_or_organization()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();
        var id = Guid.CreateVersion7();
        Assert.Equal("CREATED", Str((await SyncAsync(b.Admin, Deposit(b, id)))[0], "result"));

        var theirs = await SyncAsync(member.Session, Deposit(b, id));
        Assert.Equal(("REJECTED", "DUPLICATE_CLIENT_TXN"), (Str(theirs[0], "result"), Str(theirs[0], "errorCode")));
        Assert.False(theirs[0].TryGetProperty("transaction", out var t) && t.ValueKind != JsonValueKind.Null);   // nothing of the other entry leaks

        // Another organization submitting the same id and a fund it can't see.
        var intruder = await (await _host.CreateOrgAsync()).AdminAsync();
        var r = await SyncAsync(intruder, Deposit(b, id));
        Assert.Equal("REJECTED", Str(r[0], "result"));
        Assert.Equal(1, await CountAsync(b.FundId));
    }

    [Fact]
    public async Task An_entry_that_waited_longer_than_the_limit_still_syncs_but_is_flagged_for_the_admin_TR043()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var stale = Guid.CreateVersion7();
        var r = await SyncAsync(b.Admin, Deposit(b, stale, at: DateTimeOffset.UtcNow.AddHours(-80)), Deposit(b, Guid.CreateVersion7(), at: DateTimeOffset.UtcNow.AddHours(-2)));
        Assert.All(r, x => Assert.Equal("CREATED", Str(x, "result")));

        var log = (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=OFFLINE_ENTRY_STALE")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(Str(r[0].GetProperty("transaction"), "id"), Str(log, "entityId"));
        Assert.True(log.GetProperty("newValue").GetProperty("waitedHours").GetInt32() >= 79);
    }

    [Fact]
    public async Task Batches_are_limited_to_50_and_need_sign_in_and_a_discard_is_audited()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/sync/transactions", Batch()), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var tooMany = Enumerable.Range(0, 51).Select(_ => Deposit(b, Guid.CreateVersion7())).ToArray();
        await ProblemAssert.HasCodeAsync(await b.Admin.PostAsync("/api/v1/sync/transactions", Batch(tooMany)), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, await CountAsync(b.FundId));

        using var anonymous = _host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/sync/transactions", Batch(Deposit(b, Guid.NewGuid())), TestContext.Current.CancellationToken)).StatusCode);

        var gone = Guid.CreateVersion7();
        Assert.Equal(HttpStatusCode.NoContent, (await b.Admin.PostAsync("/api/v1/sync/discard", new { clientTxnId = gone, summary = "Money In ₹500 for Area 4" })).StatusCode);
        var log = (await b.Admin.GetJsonAsync("/api/v1/audit-logs?action=OFFLINE_ENTRY_DISCARDED")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(gone.ToString(), Str(log, "entityId"));
    }
}
