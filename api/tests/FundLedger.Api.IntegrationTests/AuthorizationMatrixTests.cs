using System.Net;
using System.Text.RegularExpressions;
using FundLedger.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FundLedger.Api.IntegrationTests;

/// <summary>
/// Authorization matrix (plan P5-07): every route the API exposes is accounted for. The list of public routes is written
/// out below, so a new route can't become public by accident; every other route refuses an anonymous caller, and every
/// Admin-only route refuses a Member. New routes are covered automatically.
/// </summary>
[Collection(PostgresTests.Name)]
public sealed partial class AuthorizationMatrixTests(PostgresFixture db) : IAsyncLifetime
{
    /// <summary>The only routes reachable without signing in (login, token refresh, health probes, version, API description).</summary>
    private static readonly string[] PublicRoutes =
    [
        "GET /api/v1/version",
        "ANY /health/live",
        "ANY /health/ready",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/logout",
        "GET /api/{documentName}/openapi.json",
    ];

    private ApiHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ApiHost(db);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private sealed record Route(string Method, string Pattern, bool Public, bool AdminOnly)
    {
        public string Key => $"{Method} {Pattern}";

        /// <summary>The pattern with every <c>{parameter}</c> replaced by a plausible value.</summary>
        public string Url => Placeholder().Replace(Pattern, m => m.Value.Contains("guid", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("id", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString() : "x");
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex Placeholder();

    private List<Route> Routes() =>
        _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m ? m : ["ANY"];   // health probes accept any method
                var authorize = e.Metadata.GetOrderedMetadata<IAuthorizeData>();
                var anonymous = e.Metadata.GetMetadata<IAllowAnonymous>() is not null || authorize.Count == 0;
                var admin = authorize.Any(a => a.Policy == "admin");
                return methods.Select(method => new Route(method, "/" + e.RoutePattern.RawText!.TrimStart('/'), anonymous, admin));
            })
            .Where(r => r.Method != "OPTIONS" && r.Method != "HEAD")
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_public_routes_are_exactly_the_ones_listed()
    {
        SkipIfNoDb();
        var actual = Routes().Where(r => r.Public).Select(r => r.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(PublicRoutes.Order(StringComparer.Ordinal).ToList(), actual);
    }

    [Fact]
    public async Task Every_other_route_refuses_an_anonymous_caller()
    {
        SkipIfNoDb();
        using var anonymous = _host.Client();
        var routes = Routes().Where(r => !r.Public).ToList();
        Assert.True(routes.Count > 40, $"only {routes.Count} protected routes found: the scan is not seeing the API");

        var failures = new List<string>();
        foreach (var r in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(r.Method), r.Url);
            if (r.Method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            }

            var status = (await anonymous.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
            if (status != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{r.Key} -> {(int)status}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Every_admin_only_route_refuses_a_member()
    {
        SkipIfNoDb();
        var b = await Books.CreateAsync(_host);
        var member = await b.MemberAsync();
        var routes = Routes().Where(r => r.AdminOnly).ToList();
        Assert.True(routes.Count > 20, $"only {routes.Count} admin routes found");

        var failures = new List<string>();
        foreach (var r in routes)
        {
            var body = r.Method is "POST" or "PUT" or "PATCH" ? new { } : null;
            HttpResponseMessage response = r.Method switch
            {
                "GET" => await member.Session.GetAsync(r.Url),
                "POST" => await member.Session.PostAsync(r.Url, body),
                "PUT" => await member.Session.PutAsync(r.Url, body),
                "PATCH" => await member.Session.PatchAsync(r.Url, body),
                _ => throw new InvalidOperationException($"{r.Method} is not expected on this API"),
            };
            // 403 (or 404 where the API hides the resource) is a refusal; anything that did the work, or crashed, is not.
            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound))
            {
                failures.Add($"{r.Key} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers_and_no_server_banner()
    {
        SkipIfNoDb();
        using var client = _host.Client();
        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.True(headers.ContainsKey("Referrer-Policy"));
        Assert.True(headers.ContainsKey("Permissions-Policy"));
        Assert.True(headers.ContainsKey("Content-Security-Policy") || headers.ContainsKey("X-Frame-Options"));
        Assert.False(headers.ContainsKey("Server") && headers["Server"].Contains("Kestrel", StringComparison.OrdinalIgnoreCase), "the server banner should not be sent");
        Assert.False(headers.ContainsKey("X-Powered-By"));
    }
}
