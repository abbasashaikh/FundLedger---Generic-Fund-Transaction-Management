namespace FundLedger.Api.Hosting;

/// <summary>Build identity, injected by the Docker build (APP_VERSION, GIT_SHA).</summary>
internal sealed record AppVersion(string Version, string Commit)
{
    public static AppVersion Current { get; } = new(
        Environment.GetEnvironmentVariable("APP_VERSION") ?? "0.0.0-dev",
        Environment.GetEnvironmentVariable("GIT_SHA") ?? "local");
}
