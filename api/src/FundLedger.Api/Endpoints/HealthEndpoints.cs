using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FundLedger.Api.Endpoints;

internal static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        // Liveness: the process is up. No dependencies, so it never flaps with the DB.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .ExcludeFromDescription();

        // Readiness: dependencies reachable. Used by deploy.sh and the uptime monitor (TR-097).
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).ExcludeFromDescription();
    }
}
