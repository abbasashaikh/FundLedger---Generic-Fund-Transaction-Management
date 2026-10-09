using FundLedger.Api.Hosting;

namespace FundLedger.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1").WithTags("System");

        v1.MapGet("/version", (IHostEnvironment env) => new VersionResponse(
                AppVersion.Current.Version, AppVersion.Current.Commit, env.EnvironmentName))
            .WithName("GetVersion")
            .WithSummary("Build version of the running API (used by deploy smoke tests).");
    }
}

/// <summary>Build identity of the running API.</summary>
public sealed record VersionResponse(string Version, string Commit, string Environment);
