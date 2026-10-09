using System.Net;
using FundLedger.Api.IntegrationTests.Support;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Errors;
using FundLedger.Application.Funds;
using FundLedger.Domain.Funds;
using FundLedger.Domain.Users;
using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// Two-user IDOR suite (TRD TR-005, Standard §2.3): a caller changes ids in URLs to
/// reach another tenant's or another role's data. Cross-tenant → 404 (existence not
/// revealed); wrong role → 403; no token → 401.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed class IsolationTests(PostgresFixture db) : IAsyncLifetime
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
    public async Task Admin_of_one_organization_cannot_reach_users_of_another()
    {
        SkipIfNoDb();
        var orgA = await _host.CreateOrgAsync();
        var orgB = await _host.CreateOrgAsync();
        var adminA = await orgA.AdminAsync();
        var adminB = await orgB.AdminAsync();
        var victim = await orgB.CreateMemberAsync(adminB);
        var version = (await adminB.GetJsonAsync($"/api/v1/users/{victim.Id}")).GetProperty("version").GetUInt32();

        var attempts = new Func<Task<HttpResponseMessage>>[]
        {
            () => adminA.GetAsync($"/api/v1/users/{victim.Id}"),
            () => adminA.PutAsync($"/api/v1/users/{victim.Id}", new { fullName = "Hacked", mobile = victim.Mobile, role = "ADMIN", version }),
            () => adminA.PatchAsync($"/api/v1/users/{victim.Id}/status", new { status = "INACTIVE" }),
            () => adminA.PostAsync($"/api/v1/users/{victim.Id}/reset-pin", null),
            () => adminA.PutAsync($"/api/v1/users/{victim.Id}/fund-access", new { items = Array.Empty<object>() }),
            () => adminA.GetAsync($"/api/v1/users/{victim.Id}/sessions"),
            () => adminA.PostAsync($"/api/v1/users/{victim.Id}/sessions/revoke", new { familyId = (Guid?)null }),
        };

        foreach (var attempt in attempts)
        {
            await ProblemAssert.HasCodeAsync(await attempt(), HttpStatusCode.NotFound, "NOT_FOUND");
        }

        // Nothing changed for the victim, and A's list never shows B's users.
        var after = await adminB.GetJsonAsync($"/api/v1/users/{victim.Id}");
        Assert.Equal("ACTIVE", after.GetProperty("status").GetString());
        Assert.Equal(version, after.GetProperty("version").GetUInt32());
        var listA = await adminA.GetJsonAsync("/api/v1/users");
        Assert.DoesNotContain(listA.GetProperty("items").EnumerateArray(), u => u.GetProperty("id").GetGuid() == victim.Id);
    }

    [Fact]
    public async Task Admin_cannot_grant_access_to_another_organizations_fund()
    {
        SkipIfNoDb();
        var orgA = await _host.CreateOrgAsync();
        var orgB = await _host.CreateOrgAsync();
        var adminA = await orgA.AdminAsync();
        var memberA = await orgA.CreateMemberAsync(adminA);
        var fundB = await orgB.CreateFundAsync("BFUND");

        await ProblemAssert.HasCodeAsync(
            await adminA.PutAsync($"/api/v1/users/{memberA.Id}/fund-access", new { items = new[] { new { fundId = fundB } } }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(0, (await adminA.GetJsonAsync("/api/v1/funds")).GetArrayLength());
    }

    [Fact]
    public async Task Members_cannot_use_admin_endpoints()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var member = await org.CreateMemberAsync(admin);
        var s = await Session.LoginAsync(_host, member.Mobile, member.TemporaryPin);
        await s.ChangePinAsync(member.TemporaryPin, "730264");

        var attempts = new Func<Task<HttpResponseMessage>>[]
        {
            () => s.GetAsync("/api/v1/users"),
            () => s.GetAsync($"/api/v1/users/{org.AdminId}"),
            () => s.PostAsync("/api/v1/users", new { fullName = "Sneaky", mobile = ApiHost.RandomMobile(), role = "ADMIN" }),
            () => s.PatchAsync($"/api/v1/users/{org.AdminId}/status", new { status = "INACTIVE" }),
            () => s.PostAsync($"/api/v1/users/{org.AdminId}/reset-pin", null),
            () => s.PutAsync($"/api/v1/users/{member.Id}/fund-access", new { items = Array.Empty<object>() }),
        };

        foreach (var attempt in attempts)
        {
            await ProblemAssert.HasCodeAsync(await attempt(), HttpStatusCode.Forbidden, "FORBIDDEN");
        }
    }

    [Theory]
    [InlineData("/api/v1/me")]
    [InlineData("/api/v1/funds")]
    [InlineData("/api/v1/users")]
    public async Task Anonymous_callers_get_401(string path)
    {
        SkipIfNoDb();
        var response = await _host.Client().GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        await ProblemAssert.HasCodeAsync(response, HttpStatusCode.Unauthorized, "UNAUTHENTICATED");
    }

    [Fact]
    public async Task Fund_access_guard_hides_unassigned_funds_and_enforces_capabilities()
    {
        SkipIfNoDb();
        var org = await _host.CreateOrgAsync();
        var admin = await org.AdminAsync();
        var assigned = await org.CreateFundAsync("ASG");
        var other = await org.CreateFundAsync("OTH");
        var member = await org.CreateMemberAsync(admin);
        (await admin.PutAsync($"/api/v1/users/{member.Id}/fund-access", new
        {
            items = new[] { new { fundId = assigned, canMoneyIn = true, canMoneyOut = false, canTransfer = false, canViewReports = true, canExport = false, canViewAllTxns = true } },
        })).EnsureSuccessStatusCode();

        await using var scope = _host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<TenantContext>().Set(org.OrganizationId, member.Id, isAdmin: false);
        sp.GetRequiredService<CurrentUser>().Set(member.Id, org.OrganizationId, Guid.Empty, Guid.Empty, UserRole.Member, false);
        var dbx = sp.GetRequiredService<FundLedgerDbContext>();
        await using var tx = await dbx.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var guard = sp.GetRequiredService<FundAccessGuard>();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(assigned, (await guard.RequireAsync(assigned, FundCapability.MoneyIn, ct)).Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => guard.RequireAsync(assigned, FundCapability.MoneyOut, ct));
        await Assert.ThrowsAsync<NotFoundException>(() => guard.RequireAsync(other, FundCapability.View, ct));
        await Assert.ThrowsAsync<NotFoundException>(() => guard.RequireAsync(Guid.CreateVersion7(), FundCapability.View, ct));
    }

    [Fact]
    public async Task Runtime_role_cannot_read_another_tenants_sessions_or_users_even_with_raw_sql()
    {
        SkipIfNoDb();
        var orgA = await _host.CreateOrgAsync();
        var orgB = await _host.CreateOrgAsync();
        await orgB.AdminAsync();   // creates a session row in B

        await using var scope = _host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(orgA.OrganizationId, orgA.AdminId, isAdmin: true);
        var dbx = scope.ServiceProvider.GetRequiredService<FundLedgerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        await using var tx = await dbx.Database.BeginTransactionAsync(ct);

        Assert.Equal(0, await dbx.UserSessions.CountAsync(s => s.OrganizationId == orgB.OrganizationId, ct));
        Assert.Equal(0, await dbx.Users.CountAsync(u => u.OrganizationId == orgB.OrganizationId, ct));
        Assert.Equal(1, await dbx.Users.CountAsync(ct));
    }
}
