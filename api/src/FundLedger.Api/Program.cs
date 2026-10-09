using FundLedger.Api.Hosting;
using FundLedger.Infrastructure;
using Serilog;

// `dotnet FundLedger.Api.dll migrate` applies EF migrations as the schema owner
// (ConnectionStrings__Migrations) and exits. Used by CI/CD before each deploy.
if (args.Length > 0 && args[0] == "migrate")
{
    return await MigrateCommand.RunAsync(args[1..]).ConfigureAwait(false);
}

Log.Logger = LoggingSetup.CreateBootstrapLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddFundLedgerApi();
    builder.Services.AddFundLedgerInfrastructure(builder.Configuration);

    var app = builder.Build();
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
