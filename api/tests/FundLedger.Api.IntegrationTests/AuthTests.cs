using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Mobile + PIN authentication and session lifecycle (ADR-0002, TRD §6).</summary>
[Collection(PostgresTests.Name)]
public sealed class AuthTests(PostgresFixture db) : IAsyncLifetime
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
    public async Task Temporary_pin_gives_a_restricted_session_until_changed()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var s = await Session.LoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin);

        Assert.True(s.LastAuthBody.GetProperty("user").GetProperty("pinMustChange").GetBoolean());
        Assert.Equal("ADMIN", s.LastAuthBody.GetProperty("user").GetProperty("role").GetString());
        await ProblemAssert.HasCodeAsync(await s.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "PIN_CHANGE_REQUIRED");
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/v1/me")).StatusCode);

        await s.ChangePinAsync(org.AdminTemporaryPin, TestOrg.AdminPin);

        // Same access token, now unrestricted: the restriction is read from the DB per request.
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/v1/users")).StatusCode);
        var me = await s.GetJsonAsync("/api/v1/me");
        Assert.False(me.GetProperty("pinMustChange").GetBoolean());
    }

    [Fact]
    public async Task Refresh_cookie_is_httponly_secure_strict_and_scoped_to_auth_path()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var session = new Session(_host, _host.Client());
        var response = await session.SendAuthAsync("/api/v1/auth/login", new { mobile = org.AdminMobile, pin = org.AdminTemporaryPin });
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("fl_rt=", StringComparison.Ordinal));

        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.LastAuthBody.TryGetProperty("refreshToken", out _));   // never in the body
    }

    [Fact]
    public async Task Unknown_number_inactive_user_and_wrong_pin_are_indistinguishable()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        (await admin.PatchAsync($"/api/v1/users/{member.Id}/status", new { status = "INACTIVE" })).EnsureSuccessStatusCode();

        var wrongPin = await ProblemAssert.HasCodeAsync(
            await Session.TryLoginAsync(_host, org.AdminMobile, "730264"), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        var unknown = await ProblemAssert.HasCodeAsync(
            await Session.TryLoginAsync(_host, ApiHost.RandomMobile(), "730264"), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        var inactive = await ProblemAssert.HasCodeAsync(
            await Session.TryLoginAsync(_host, member.Mobile, member.TemporaryPin), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");

        Assert.Equal(wrongPin.GetProperty("title").GetString(), unknown.GetProperty("title").GetString());
        Assert.Equal(wrongPin.GetProperty("title").GetString(), inactive.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Five_failures_lock_the_number_even_for_the_correct_pin_then_unlock_after_window()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        for (var i = 0; i < 5; i++)
        {
            await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(_host, org.AdminMobile, "730264"),
                HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        }

        var locked = await Session.TryLoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin);
        await ProblemAssert.HasCodeAsync(locked, HttpStatusCode.TooManyRequests, "ACCOUNT_LOCKED");
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        _host.Clock.Offset = TimeSpan.FromMinutes(16);
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Session.TryLoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin)).StatusCode);
        }
        finally
        {
            _host.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Unknown_numbers_lock_out_the_same_way()
    {
        SkipIfNoDb();
        var mobile = ApiHost.RandomMobile();
        for (var i = 0; i < 5; i++)
        {
            await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(_host, mobile, "730264"),
                HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        }

        await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(_host, mobile, "730264"),
            HttpStatusCode.TooManyRequests, "ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Successful_login_resets_the_failure_count()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        for (var i = 0; i < 4; i++)
        {
            await Session.TryLoginAsync(_host, org.AdminMobile, "730264");
        }

        Assert.Equal(HttpStatusCode.OK, (await Session.TryLoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin)).StatusCode);
        for (var i = 0; i < 4; i++)
        {
            await Session.TryLoginAsync(_host, org.AdminMobile, "730264");
        }

        Assert.Equal(HttpStatusCode.OK, (await Session.TryLoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin)).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_cookie_and_a_late_replay_revokes_the_whole_chain()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var s = await org.AdminAsync();
        var firstCookie = s.RefreshCookie!;

        Assert.Equal(HttpStatusCode.OK, (await s.SendAuthAsync("/api/v1/auth/refresh")).StatusCode);
        Assert.NotEqual(firstCookie, s.RefreshCookie);

        // Immediate replay of the old cookie = another tab racing: refused, chain kept.
        var race = await s.SendAuthAsync("/api/v1/auth/refresh", cookieOverride: firstCookie);
        await ProblemAssert.HasCodeAsync(race, HttpStatusCode.Unauthorized, "REFRESH_RACE");
        Assert.Equal(HttpStatusCode.OK, (await s.GetAsync("/api/v1/me")).StatusCode);

        // A replay after the race window = theft: the whole chain is revoked.
        _host.Clock.Offset = TimeSpan.FromSeconds(30);
        try
        {
            var theft = await s.SendAuthAsync("/api/v1/auth/refresh", cookieOverride: firstCookie);
            await ProblemAssert.HasCodeAsync(theft, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        }
        finally
        {
            _host.Clock.Offset = TimeSpan.Zero;
        }

        await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/refresh"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        await ProblemAssert.HasCodeAsync(await s.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Logout_invalidates_access_token_and_refresh_cookie_server_side()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var s = await org.AdminAsync();
        var token = s.AccessToken;
        var cookie = s.RefreshCookie;

        Assert.Equal(HttpStatusCode.NoContent, (await s.SendAuthAsync("/api/v1/auth/logout")).StatusCode);

        s.AccessToken = token;   // replay the old access token (Standard §2.8 test)
        await ProblemAssert.HasCodeAsync(await s.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
        s.AccessToken = null;
        await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/refresh", cookieOverride: cookie),
            HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Idle_sessions_expire()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var s = await org.AdminAsync();
        _host.Clock.Offset = TimeSpan.FromHours(9);   // default idle timeout: 8 h
        try
        {
            await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/refresh"), HttpStatusCode.Unauthorized, "SESSION_EXPIRED");
        }
        finally
        {
            _host.Clock.Offset = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Change_pin_rejects_weak_and_unchanged_pins_and_wrong_current_pin_counts_towards_lockout()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var s = await Session.LoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin);

        await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/pin/change",
            new { currentPin = org.AdminTemporaryPin, newPin = "123456" }), HttpStatusCode.BadRequest, "PIN_TOO_WEAK");
        await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/pin/change",
            new { currentPin = org.AdminTemporaryPin, newPin = org.AdminTemporaryPin }), HttpStatusCode.BadRequest, "PIN_UNCHANGED");

        for (var i = 0; i < 5; i++)
        {
            await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/pin/change",
                new { currentPin = "730264", newPin = "482915" }), HttpStatusCode.BadRequest, "CURRENT_PIN_INCORRECT");
        }

        // The failures were committed despite the error responses: the number is now locked.
        await ProblemAssert.HasCodeAsync(await s.SendAuthAsync("/api/v1/auth/pin/change",
            new { currentPin = org.AdminTemporaryPin, newPin = "482915" }), HttpStatusCode.TooManyRequests, "ACCOUNT_LOCKED");
        await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(_host, org.AdminMobile, org.AdminTemporaryPin),
            HttpStatusCode.TooManyRequests, "ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Changing_pin_signs_out_other_devices_but_not_this_one()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var first = await org.AdminAsync();
        var second = await Session.LoginAsync(_host, org.AdminMobile, TestOrg.AdminPin);

        await first.ChangePinAsync(TestOrg.AdminPin, "730264");

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/v1/me")).StatusCode);
        await ProblemAssert.HasCodeAsync(await second.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Auth_endpoints_require_the_client_header()
    {
        SkipIfNoDb();
        var response = await _host.Client().PostAsJsonAsync("/api/v1/auth/login", new { mobile = "9876543210", pin = "482915" },
            TestContext.Current.CancellationToken);
        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.BadRequest, "CLIENT_HEADER_REQUIRED");
    }

    [Fact]
    public async Task Tokens_signed_by_another_key_are_rejected()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        await using var otherHost = new ApiHost(db);   // separate ephemeral signing key
        var foreign = await Session.LoginAsync(otherHost, org.AdminMobile, org.AdminTemporaryPin);

        var s = new Session(_host, _host.Client()) { AccessToken = foreign.AccessToken };
        await ProblemAssert.HasCodeAsync(await s.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Login_is_rate_limited_per_ip()
    {
        SkipIfNoDb();
        await using var strict = new ApiHost(db, loginPerIpLimit: 3);
        for (var i = 0; i < 3; i++)
        {
            await Session.TryLoginAsync(strict, ApiHost.RandomMobile(), "730264");
        }

        await ProblemAssert.HasCodeAsync(await Session.TryLoginAsync(strict, ApiHost.RandomMobile(), "730264"),
            HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task Audit_records_login_failures_and_pin_changes_without_secrets()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        await Session.TryLoginAsync(_host, org.AdminMobile, "730264");
        await org.AdminAsync();

        var actions = await AuditActionsAsync(org.OrganizationId);
        Assert.Contains("LOGIN_FAILED", actions);
        Assert.Contains("LOGIN", actions);
        Assert.Contains("PIN_CHANGED", actions);

        var leaked = await ScalarAsync($"""
            SELECT count(*) FROM fl.audit_logs WHERE organization_id = '{org.OrganizationId}'
              AND (coalesce(new_value::text,'') LIKE '%{TestOrg.AdminPin}%' OR coalesce(new_value::text,'') LIKE '%{org.AdminTemporaryPin}%'
                   OR coalesce(new_value::text,'') LIKE '%AQAAAA%')
            """);
        Assert.Equal(0L, leaked);
    }

    private async Task<List<string>> AuditActionsAsync(Guid org)
    {
        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand($"SELECT action FROM fl.audit_logs WHERE organization_id = '{org}'", conn);
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(db.AdminConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
