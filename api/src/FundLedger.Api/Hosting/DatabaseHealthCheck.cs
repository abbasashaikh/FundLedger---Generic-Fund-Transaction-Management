using FundLedger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FundLedger.Api.Hosting;

/// <summary>
/// Readiness: the API can reach PostgreSQL with its runtime role and the schema
/// exists. Runs without tenant context, so it reads no tenant data.
/// </summary>
internal sealed class DatabaseHealthCheck(FundLedgerDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var schemaExists = await db.Database
                .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'fl') AS \"Value\"")
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);

            return schemaExists
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Schema 'fl' is missing — run migrations.");
        }
#pragma warning disable CA1031 // a health check must report, not throw
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Message only: never the connection string.
            return HealthCheckResult.Unhealthy("Database unreachable.", new InvalidOperationException(ex.GetType().Name));
        }
    }
}
