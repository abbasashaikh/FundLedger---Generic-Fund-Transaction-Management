using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Admin user management (PRD §21.1, BR-001..003) and fund access (§6.4).</summary>
[Collection(PostgresTests.Name)]
public sealed class UserAdminTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    [Fact]
    public async Task Admin_created_member_signs_in_with_the_one_time_pin()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin, "Ahmed");

        var s = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        Assert.Equal("MEMBER", s.LastAuthBody.GetProperty("user").GetProperty("role").GetString());
        Assert.True(s.LastAuthBody.GetProperty("user").GetProperty("pinMustChange").GetBoolean());

        var list = await admin.GetJsonAsync("/api/v1/users");
        Assert.Equal(2, list.GetProperty("activeCount").GetInt32());
        Assert.Equal(50, list.GetProperty("maxActiveUsers").GetInt32());
        Assert.Contains(list.GetProperty("items").EnumerateArray(), u => u.GetProperty("fullName").GetString() == "Ahmed");
    }

    [Fact]
    public async Task Validation_errors_are_reported_per_field()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var response = await admin.PostAsync("/api/v1/users", new { fullName = "", mobile = "12345", role = "MEMBER" });
        var problem = await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.True(problem.GetProperty("errors").TryGetProperty("mobile", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("fullName", out _));
    }

    [Fact]
    public async Task Unknown_json_fields_are_rejected()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var response = await admin.PostAsync("/api/v1/users",
            new { fullName = "X", mobile = ApiHost.RandomMobile(), role = "ADMIN", isSuperuser = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_mobile_is_a_conflict()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        await ProblemAssert.HasCodeAsync(
            await admin.PostAsync("/api/v1/users", new { fullName = "Dup", mobile = member.Mobile, role = "MEMBER" }),
            HttpStatusCode.Conflict, "MOBILE_ALREADY_REGISTERED");
    }

    [Fact]
    public async Task Active_user_limit_is_enforced_and_inactive_users_do_not_count_BR001()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        await PostgresFixture.ExecuteAsync(db.AdminConnectionString,
            $"UPDATE fl.organizations SET max_active_users = 2 WHERE id = '{org.OrganizationId}'");
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);    // 2 of 2

        await ProblemAssert.HasCodeAsync(
            await admin.PostAsync("/api/v1/users", new { fullName = "Third", mobile = ApiHost.RandomMobile(), role = "MEMBER" }),
            HttpStatusCode.Conflict, "ACTIVE_USER_LIMIT_REACHED");

        (await admin.PatchAsync($"/api/v1/users/{member.Id}/status", new { status = "INACTIVE" })).EnsureSuccessStatusCode();
        var third = await org.CreateMemberAsync(admin);      // a slot is free again

        await ProblemAssert.HasCodeAsync(
            await admin.PatchAsync($"/api/v1/users/{member.Id}/status", new { status = "ACTIVE" }),
            HttpStatusCode.Conflict, "ACTIVE_USER_LIMIT_REACHED");
        Assert.NotEqual(Guid.Empty, third.Id);
    }

    [Fact]
    public async Task Deactivation_signs_the_user_out_immediately()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        var memberSession = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        Assert.Equal(HttpStatusCode.OK, (await memberSession.GetAsync("/api/v1/me")).StatusCode);

        (await admin.PatchAsync($"/api/v1/users/{member.Id}/status", new { status = "INACTIVE", reason = "Left the team" }))
            .EnsureSuccessStatusCode();

        // Still-valid JWT, but the next request is refused (TR-016): no 15-minute grace.
        await ProblemAssert.HasCodeAsync(await memberSession.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await ProblemAssert.HasCodeAsync(await memberSession.SendAuthAsync("/api/v1/auth/refresh"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Admins_cannot_lock_themselves_out()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var me = await admin.GetJsonAsync($"/api/v1/users/{org.AdminId}");

        await ProblemAssert.HasCodeAsync(await admin.PatchAsync($"/api/v1/users/{org.AdminId}/status", new { status = "INACTIVE" }),
            HttpStatusCode.Conflict, "CANNOT_CHANGE_SELF");
        await ProblemAssert.HasCodeAsync(await admin.PutAsync($"/api/v1/users/{org.AdminId}", new
        {
            fullName = "Test Admin", mobile = org.AdminMobile, role = "MEMBER", version = me.GetProperty("version").GetUInt32(),
        }), HttpStatusCode.Conflict, "CANNOT_CHANGE_SELF");
        await ProblemAssert.HasCodeAsync(await admin.PostAsync($"/api/v1/users/{org.AdminId}/reset-pin", null),
            HttpStatusCode.Conflict, "CANNOT_CHANGE_SELF");
    }

    [Fact]
    public async Task Demoting_an_admin_takes_effect_on_their_next_request()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var second = await org.CreateMemberAsync(admin, "Second Admin", role: "ADMIN");
        var secondSession = await Session.LoginAsync(_host, second.Mobile, second.TemporaryPin);
        await secondSession.ChangePinAsync(second.TemporaryPin, "730264");
        Assert.Equal(HttpStatusCode.OK, (await secondSession.GetAsync("/api/v1/users")).StatusCode);

        var detail = await admin.GetJsonAsync($"/api/v1/users/{second.Id}");
        (await admin.PutAsync($"/api/v1/users/{second.Id}", new
        {
            fullName = "Second Admin", mobile = second.Mobile, role = "MEMBER", version = detail.GetProperty("version").GetUInt32(),
        })).EnsureSuccessStatusCode();

        await ProblemAssert.HasCodeAsync(await secondSession.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task Stale_version_is_a_conflict_not_a_silent_overwrite()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        var v1 = (await admin.GetJsonAsync($"/api/v1/users/{member.Id}")).GetProperty("version").GetUInt32();

        (await admin.PutAsync($"/api/v1/users/{member.Id}", new { fullName = "First edit", mobile = member.Mobile, role = "MEMBER", version = v1 }))
            .EnsureSuccessStatusCode();
        await ProblemAssert.HasCodeAsync(
            await admin.PutAsync($"/api/v1/users/{member.Id}", new { fullName = "Stale edit", mobile = member.Mobile, role = "MEMBER", version = v1 }),
            HttpStatusCode.Conflict, "REVISION_CONFLICT");
    }

    [Fact]
    public async Task Pin_reset_issues_a_new_temporary_pin_and_ends_existing_sessions()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        var memberSession = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        await memberSession.ChangePinAsync(member.TemporaryPin, "730264");

        var reset = await admin.PostAsync($"/api/v1/users/{member.Id}/reset-pin", null);
        reset.EnsureSuccessStatusCode();
        var newPin = (await reset.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("temporaryPin").GetString()!;

        await ProblemAssert.HasCodeAsync(await memberSession.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(_host, member.Mobile, "730264"), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        var again = await Session.LoginAsync(_host, member.Mobile, newPin);
        Assert.True(again.LastAuthBody.GetProperty("user").GetProperty("pinMustChange").GetBoolean());
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked_individually()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        var phone = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        var laptop = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);

        var sessions = await admin.GetJsonAsync($"/api/v1/users/{member.Id}/sessions");
        Assert.Equal(2, sessions.GetArrayLength());

        var phoneFamily = sessions.EnumerateArray().Select(x => x.GetProperty("familyId").GetGuid()).First();
        (await admin.PostAsync($"/api/v1/users/{member.Id}/sessions/revoke", new { familyId = phoneFamily })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await admin.GetJsonAsync($"/api/v1/users/{member.Id}/sessions")).GetArrayLength());

        (await admin.PostAsync($"/api/v1/users/{member.Id}/sessions/revoke", new { familyId = (Guid?)null })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await admin.GetJsonAsync($"/api/v1/users/{member.Id}/sessions")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Fund_access_controls_what_a_member_sees()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var ijtema = await org.CreateFundAsync("IJT26");
        var medical = await org.CreateFundAsync("MED");
        var draft = await org.CreateFundAsync("DRF", "DRAFT");
        var member = await org.CreateMemberAsync(admin);

        (await admin.PutAsync($"/api/v1/users/{member.Id}/fund-access", new
        {
            items = new object[]
            {
                new { fundId = ijtema, canMoneyIn = true, canMoneyOut = false, canTransfer = false, canViewReports = true, canExport = false, canViewAllTxns = true },
                new { fundId = draft, canMoneyIn = true, canMoneyOut = true, canTransfer = false, canViewReports = true, canExport = false, canViewAllTxns = true },
            },
        })).EnsureSuccessStatusCode();

        var s = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        await s.ChangePinAsync(member.TemporaryPin, "730264");
        var me = await s.GetJsonAsync("/api/v1/me");
        var funds = me.GetProperty("funds").EnumerateArray().ToList();

        // Draft funds are hidden from members even when assigned; unassigned funds are invisible.
        var only = Assert.Single(funds);
        Assert.Equal(ijtema, only.GetProperty("id").GetGuid());
        Assert.True(only.GetProperty("permissions").GetProperty("moneyIn").GetBoolean());
        Assert.False(only.GetProperty("permissions").GetProperty("moneyOut").GetBoolean());
        Assert.False(only.GetProperty("permissions").GetProperty("adjust").GetBoolean());

        var adminFunds = await admin.GetJsonAsync("/api/v1/funds");
        Assert.Equal(3, adminFunds.GetArrayLength());
        Assert.Contains(adminFunds.EnumerateArray(), f => f.GetProperty("id").GetGuid() == medical);

        // Removing access takes effect at once.
        (await admin.PutAsync($"/api/v1/users/{member.Id}/fund-access", new { items = Array.Empty<object>() })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await s.GetJsonAsync("/api/v1/funds")).GetArrayLength());
    }

    [Fact]
    public async Task Admins_do_not_get_explicit_fund_grants()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        await ProblemAssert.HasCodeAsync(
            await admin.PutAsync($"/api/v1/users/{org.AdminId}/fund-access", new { items = Array.Empty<object>() }),
            HttpStatusCode.Conflict, "ADMIN_HAS_ALL_FUNDS");
    }

    [Fact]
    public async Task User_changes_are_audited_with_masked_mobile()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        (await admin.PatchAsync($"/api/v1/users/{member.Id}/status", new { status = "INACTIVE", reason = "Left" })).EnsureSuccessStatusCode();

        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            SELECT action, coalesce(new_value::text, ''), coalesce(reason, '') FROM fl.audit_logs
             WHERE organization_id = '{org.OrganizationId}' AND entity_id = '{member.Id}' ORDER BY id
            """, conn);
        var rows = new List<(string Action, string New, string Reason)>();
        await using (var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await r.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            }
        }

        Assert.Contains(rows, x => x.Action == "USER_CREATED" && x.New.Contains("******", StringComparison.Ordinal)
                                   && !x.New.Contains(member.Mobile[3..], StringComparison.Ordinal));
        Assert.Contains(rows, x => x.Action == "USER_DEACTIVATED" && x.Reason == "Left");
    }
}
