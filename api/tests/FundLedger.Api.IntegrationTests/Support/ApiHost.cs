using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FundLedger.Application.Bootstrap;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FundLedger.Api.IntegrationTests.Support;

/// <summary>A TimeProvider the tests can move forward (lockout windows, refresh race window).</summary>
public sealed class ShiftableTimeProvider : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
}

/// <summary>The real API over the test database, connected as the runtime (RLS) role.</summary>
public sealed class ApiHost(PostgresFixture db, int loginPerIpLimit = 100_000) : WebApplicationFactory<Program>
{
    public ShiftableTimeProvider Clock { get; } = new();

    public PostgresFixture Db => db;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:FundLedger", db.AppConnectionString);
        builder.UseSetting("RateLimiting:PermitsPerMinute", "100000");
        builder.UseSetting("RateLimiting:LoginPerIpPer15Min", loginPerIpLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>Creates an isolated organization with its first Admin (random code and mobile).</summary>
    public async Task<TestOrg> CreateOrgAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var mobile = RandomMobile();
        var result = await scope.ServiceProvider.GetRequiredService<BootstrapService>().RunAsync(
            new BootstrapRequest("Test Org", "T" + RandomNumberGenerator.GetHexString(7), "Test Admin", mobile),
            CancellationToken.None);
        return new TestOrg(this, result.OrganizationId, result.AdminUserId, result.AdminMobile, result.TemporaryPin);
    }

    public static string RandomMobile() => "9" + RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record TestOrg(ApiHost Host, Guid OrganizationId, Guid AdminId, string AdminMobile, string AdminTemporaryPin)
{
    public const string AdminPin = "482915";

    /// <summary>Signs the Admin in and replaces the temporary PIN, returning a full session.</summary>
    public async Task<Session> AdminAsync()
    {
        var s = await Session.LoginAsync(Host, AdminMobile, AdminTemporaryPin);
        await s.ChangePinAsync(AdminTemporaryPin, AdminPin);
        return s;
    }

    /// <summary>Creates a Member (via the API as Admin) and returns their mobile + temporary PIN.</summary>
    public async Task<(Guid Id, string Mobile, string TemporaryPin)> CreateMemberAsync(Session admin, string? name = null, string role = "MEMBER")
    {
        var mobile = ApiHost.RandomMobile();
        var response = await admin.PostAsync("/api/v1/users", new { fullName = name ?? "Member " + mobile[^4..], mobile, role });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("user").GetProperty("id").GetGuid(), "+91" + mobile, body.GetProperty("temporaryPin").GetString()!);
    }

    public async Task<Guid> CreateFundAsync(string code, string status = "ACTIVE")
    {
        var id = Guid.CreateVersion7();
        await PostgresFixture.ExecuteAsync(Host.Db.AdminConnectionString, $"""
            INSERT INTO fl.funds (id, organization_id, fund_type_id, code, name, status, created_by)
            SELECT '{id}', '{OrganizationId}', ft.id, '{code}', 'Fund {code}', '{status}'::fl.fund_status, '{AdminId}'
              FROM fl.fund_types ft WHERE ft.organization_id = '{OrganizationId}' ORDER BY ft.sort_order LIMIT 1
            """);
        return id;
    }
}

/// <summary>An API client holding an access token and (manually managed) refresh cookie.</summary>
public sealed class Session(ApiHost host, HttpClient client)
{
    public HttpClient Http { get; } = client;

    public string? AccessToken { get; set; }

    public string? RefreshCookie { get; set; }

    public JsonElement LastAuthBody { get; private set; }

    public static async Task<Session> LoginAsync(ApiHost host, string mobile, string pin)
    {
        var session = new Session(host, host.Client());
        var response = await session.SendAuthAsync("/api/v1/auth/login", new { mobile, pin });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return session;
    }

    public static async Task<HttpResponseMessage> TryLoginAsync(ApiHost host, string mobile, string pin) =>
        await new Session(host, host.Client()).SendAuthAsync("/api/v1/auth/login", new { mobile, pin });

    /// <summary>POST to /auth/*: adds the client header and current cookie; captures new tokens.</summary>
    public async Task<HttpResponseMessage> SendAuthAsync(string path, object? body = null, string? cookieOverride = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Add("X-FundLedger-Client", "pwa");
        var cookie = cookieOverride ?? RefreshCookie;
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"fl_rt={cookie}");
        }

        if (AccessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        var response = await Http.SendAsync(request);
        if (response.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.OK)
        {
            LastAuthBody = await response.Content.ReadFromJsonAsync<JsonElement>();
            AccessToken = LastAuthBody.GetProperty("accessToken").GetString();
            RefreshCookie = ExtractCookie(response) ?? RefreshCookie;
        }

        return response;
    }

    public async Task ChangePinAsync(string current, string next)
    {
        var response = await SendAuthAsync("/api/v1/auth/pin/change", new { currentPin = current, newPin = next });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    /// <param name="ifMatch">Sent as <c>If-Match: "n"</c> (the revision being edited); null sends no header.</param>
    public Task<HttpResponseMessage> PostAsync(string path, object? body, int? ifMatch = null) => SendAsync(HttpMethod.Post, path, body, ifMatch);

    public Task<HttpResponseMessage> PutAsync(string path, object? body, int? ifMatch = null) => SendAsync(HttpMethod.Put, path, body, ifMatch);

    public Task<HttpResponseMessage> PatchAsync(string path, object? body) => SendAsync(HttpMethod.Patch, path, body);

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"{path} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, int? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (ifMatch is { } rev)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{rev}\"");
        }

        if (AccessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        return await Http.SendAsync(request);
    }

    public static string? ExtractCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith("fl_rt=", StringComparison.Ordinal))
                .Select(v => v["fl_rt=".Length..]).FirstOrDefault(v => v.Length > 0)
            : null;

    public ApiHost Host => host;
}

public static class ProblemAssert
{
    public static async Task<JsonElement> HasCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {(int)status} {code}, got {(int)response.StatusCode}: {text}");
        var problem = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }
}
