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
            // The log says what failed and where, never the connection string: a message that mentions a password is withheld.
            return HealthCheckResult.Unhealthy($"Database unreachable. {Describe(ex)}", new InvalidOperationException(ex.GetType().Name));
        }
    }

    internal static string Describe(Exception ex)
    {
        var message = ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? "(message withheld)" : ex.Message;
        var frames = (ex.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4);
        var text = $"{ex.GetType().Name}: {message} | {string.Join(" <- ", frames)}";
        return text.Length <= 700 ? text : text[..700];
    }
}
