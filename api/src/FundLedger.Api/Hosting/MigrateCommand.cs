using FundLedger.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FundLedger.Api.Hosting;

/// <summary>
/// <c>dotnet FundLedger.Api.dll migrate [--list]</c>
///
/// Applies pending EF migrations using the schema-owner connection in
/// <c>ConnectionStrings__Migrations</c> (never the runtime role). Run by CI/CD
/// before deploying a new image (TRD §15.2). Prints migration ids only — never
/// the connection string.
/// </summary>
internal static class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Migrations");
        if (string.IsNullOrWhiteSpace(connection))
        {
            await Console.Error.WriteLineAsync("ConnectionStrings__Migrations is not set.").ConfigureAwait(false);
            return 2;
        }

        await using var db = DependencyInjection.CreateMigrationContext(connection);

        var applied = (await db.Database.GetAppliedMigrationsAsync().ConfigureAwait(false)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync().ConfigureAwait(false)).ToList();
        Console.WriteLine($"Applied: {applied.Count}. Pending: {pending.Count}.");
        foreach (var id in pending)
        {
            Console.WriteLine($"  pending  {id}");
        }

        if (args.Contains("--list") || pending.Count == 0)
        {
            return 0;
        }

        await db.Database.MigrateAsync().ConfigureAwait(false);
        Console.WriteLine("Migrations applied.");
        return 0;
    }
}
