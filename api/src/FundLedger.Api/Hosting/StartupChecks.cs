using FundLedger.Infrastructure.Security;

namespace FundLedger.Api.Hosting;

/// <summary>
/// Fails the process at startup (not on the first request) when required configuration
/// is missing — currently the JWT signing key. Implemented as a hosted service so it runs
/// when the host really starts, but not while build-time tooling (OpenAPI document
/// generation, EF design-time) only builds the service provider.
/// </summary>
internal sealed class StartupChecks(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The build-time OpenAPI generator starts the host without any configuration; it must not be blocked.
        if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider")
        {
            return Task.CompletedTask;
        }

        _ = services.GetRequiredService<JwtKeyRing>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
