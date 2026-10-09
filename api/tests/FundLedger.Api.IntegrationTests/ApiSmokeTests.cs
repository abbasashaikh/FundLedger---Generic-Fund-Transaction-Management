using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Pipeline behaviour that needs no database.</summary>
public sealed class ApiSmokeTests(ApiSmokeTests.Factory factory) : IClassFixture<ApiSmokeTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Unreachable on purpose: these tests must not depend on a database.
            builder.UseSetting("ConnectionStrings:FundLedger", "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");
            builder.UseSetting("Cors:AllowedOrigins:0", "https://app.example.test");
        }
    }

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Liveness_is_ok_without_database()
    {
        var response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_reports_unavailable_when_database_is_down()
    {
        var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Version_endpoint_returns_build_identity()
    {
        var version = await _client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/version", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("Testing", version.GetProperty("environment").GetString());
        Assert.False(string.IsNullOrEmpty(version.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Generates_request_id_when_missing()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/version", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out var ids));
        Assert.Matches("^[0-9a-f]{32}$", ids.Single());
    }

    [Fact]
    public async Task Echoes_safe_request_id_and_replaces_unsafe_one()
    {
        using var safe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/version");
        safe.Headers.Add("X-Request-Id", "pwa-123.abc");
        var r1 = await _client.SendAsync(safe, TestContext.Current.CancellationToken);
        Assert.Equal("pwa-123.abc", r1.Headers.GetValues("X-Request-Id").Single());

        using var unsafeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/version");
        unsafeReq.Headers.TryAddWithoutValidation("X-Request-Id", "evil\nINFO forged log line");
        var r2 = await _client.SendAsync(unsafeReq, TestContext.Current.CancellationToken);
        Assert.NotEqual("evil\nINFO forged log line", r2.Headers.GetValues("X-Request-Id").Single());
    }

    [Fact]
    public async Task Sets_security_headers_and_no_store_on_api()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/version", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details_without_internals()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/does-not-exist", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("NOT_FOUND", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
        Assert.False(problem.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task Cors_allows_only_configured_origin()
    {
        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/version");
        allowed.Headers.Add("Origin", "https://app.example.test");
        allowed.Headers.Add("Access-Control-Request-Method", "GET");
        var ok = await _client.SendAsync(allowed, TestContext.Current.CancellationToken);
        Assert.Equal("https://app.example.test", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/version");
        denied.Headers.Add("Origin", "https://evil.example");
        denied.Headers.Add("Access-Control-Request-Method", "GET");
        var no = await _client.SendAsync(denied, TestContext.Current.CancellationToken);
        Assert.False(no.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OpenApi_document_is_served_outside_production()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/openapi.json", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
