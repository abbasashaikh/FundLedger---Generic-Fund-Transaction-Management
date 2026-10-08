using FundLedger.Api.Hosting;
using Serilog;

// Operator commands (run and exit): `migrate` (schema owner, CI/CD), `bootstrap`
// (first organization + Admin), `generate-jwt-key` (ES256 signing key).
switch (args.FirstOrDefault())
{
    case "migrate":
        return await MigrateCommand.RunAsync(args[1..]).ConfigureAwait(false);
    case "bootstrap":
        return await OperatorCommands.BootstrapAsync(args[1..]).ConfigureAwait(false);
    case "generate-jwt-key":
        return OperatorCommands.GenerateJwtKey();
    default:
        break;
}

Log.Logger = LoggingSetup.CreateBootstrapLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddFundLedgerApi();

    var app = builder.Build();

    // Fail fast: a missing/invalid signing key must stop startup, not break every request.
    _ = app.Services.GetRequiredService<FundLedger.Infrastructure.Security.JwtKeyRing>();
    app.UseFundLedgerPipeline();
    app.MapFundLedgerEndpoints();

    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "FundLedger API terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
