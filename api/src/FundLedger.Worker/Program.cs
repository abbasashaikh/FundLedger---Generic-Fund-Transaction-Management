using FundLedger.Infrastructure;
using FundLedger.Worker;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Background worker (TRD §3.2): export jobs (Phase 4) and session/login-attempt
// cleanup (Phase 1). Phase 0 ships the host, logging, Sentry and a heartbeat so
// deployment, health and log shipping can be verified end to end.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("app", "fundledger-worker")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    var sentryDsn = builder.Configuration["Sentry:Dsn"];
    if (!string.IsNullOrWhiteSpace(sentryDsn))
    {
        builder.Logging.AddSentry(o =>
        {
            o.Dsn = sentryDsn;
            o.SendDefaultPii = false;
            o.Environment = builder.Environment.EnvironmentName;
        });
    }

    builder.Services.AddFundLedgerInfrastructure(builder.Configuration);
    builder.Services.AddHostedService<HeartbeatService>();

    await builder.Build().RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "FundLedger worker terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
