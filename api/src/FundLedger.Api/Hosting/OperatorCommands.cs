using System.Security.Cryptography;
using FundLedger.Application;
using FundLedger.Application.Abstractions;
using FundLedger.Application.Bootstrap;
using FundLedger.Application.Errors;
using FundLedger.Domain;
using FundLedger.Infrastructure;

namespace FundLedger.Api.Hosting;

/// <summary>
/// One-off operator commands (run against an environment, never from the PWA):
/// <code>
///   dotnet FundLedger.Api.dll bootstrap --org-name "Al Madad" --org-code ALM --admin-name "Abbas" --admin-mobile 98xxxxxxxx
///   dotnet FundLedger.Api.dll generate-jwt-key
/// </code>
/// </summary>
internal static class OperatorCommands
{
    /// <summary>
    /// Creates the first organization + Admin (App Flow §3.4). Uses the RUNTIME role
    /// (<c>ConnectionStrings__FundLedger</c>) so RLS applies exactly as in production.
    /// Prints the Admin's one-time temporary PIN — hand it over in person.
    /// </summary>
    public static async Task<int> BootstrapAsync(string[] args)
    {
        var options = ParseOptions(args);
        string? Get(string key) => options.TryGetValue(key, out var v) ? v : null;
        if (Get("org-name") is not { } orgName || Get("org-code") is not { } orgCode ||
            Get("admin-name") is not { } adminName || Get("admin-mobile") is not { } adminMobile)
        {
            await Console.Error.WriteLineAsync(
                "usage: bootstrap --org-name <name> --org-code <CODE> --admin-name <name> --admin-mobile <mobile>").ConfigureAwait(false);
            return 64;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddFundLedgerApplication()
            .AddFundLedgerInfrastructure(configuration, _ => true)   // no tokens are issued here
            .AddScoped<IRequestContext>(_ => new SystemRequestContext { RequestId = "bootstrap" });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<BootstrapService>()
                .RunAsync(new BootstrapRequest(orgName, orgCode, adminName, adminMobile), CancellationToken.None)
                .ConfigureAwait(false);

            Console.WriteLine($"Organization created: {orgName} ({orgCode.ToUpperInvariant()}) id={result.OrganizationId}");
            Console.WriteLine($"Admin: {adminName} {Domain.Users.MobileNumber.Mask(result.AdminMobile)}");
            Console.WriteLine($"Temporary PIN (shown once; the Admin must change it at first sign-in): {result.TemporaryPin}");
            return 0;
        }
        catch (Exception ex) when (ex is AppException or DomainException)
        {
            await Console.Error.WriteLineAsync($"Bootstrap failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Prints a new ES256 key pair for <c>Auth__Jwt__SigningKeyPem</c> (store it as a secret).</summary>
    public static int GenerateJwtKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Console.WriteLine("# Auth__Jwt__SigningKeyPem — PRIVATE. Store only in the secrets manager / server .env.");
        Console.WriteLine(key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine();
        Console.WriteLine("# Public key — keep for Auth__Jwt__PreviousKeys__N__PublicKeyPem after the next rotation.");
        Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        return 0;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                options[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return options;
    }
}
