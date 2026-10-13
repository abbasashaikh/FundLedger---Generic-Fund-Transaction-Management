using System.Net;
using System.Net.Http.Json;
using FundLedger.Api.IntegrationTests.Support;

namespace FundLedger.Api.IntegrationTests;

/// <summary>Behind a hosting platform's proxy each user must keep their own address and rate limit (the default, with only private-network proxies trusted, is how the VPS path works and is unchanged).</summary>
[Collection(PostgresTests.Name)]
public sealed class ProxyTests(PostgresFixture db)
{
    private void SkipIfNoDb() => Assert.SkipWhen(db.SkipReason is not null, db.SkipReason ?? string.Empty);

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { mobile = "9" + Random.Shared.Next(100_000_000, 999_999_999), pin = "123456" }) };
        request.Headers.Add("X-FundLedger-Client", "pwa");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task With_a_platform_proxy_trusted_each_forwarded_address_has_its_own_login_limit()
    {
        SkipIfNoDb();
        await using var host = new ApiHost(db, loginPerIpLimit: 2, trustAllProxies: true);
        using var client = host.Client();
        // Three different people behind the same proxy: nobody is limited by somebody else's attempts.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.3"));
        // The same address is limited after its own two attempts.
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await LoginAsync(client, "203.0.113.1"));
    }
}
