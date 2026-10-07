using System.Globalization;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace FundLedger.Api.Hosting;

/// <summary>
/// Structured JSON logs (TRD TR-095): timestamp, level, requestId, route, status,
/// elapsed. Request/response bodies are never logged, so PINs and tokens can't leak.
/// </summary>
internal static class LoggingSetup
{
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(new RenderedCompactJsonFormatter())
            .CreateBootstrapLogger();

    public static void Configure(HostBuilderContext context, LoggerConfiguration logger)
    {
        logger
            .ReadFrom.Configuration(context.Configuration)
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("app", "fundledger-api")
            .Enrich.WithProperty("env", context.HostingEnvironment.EnvironmentName);

        if (context.HostingEnvironment.IsDevelopment())
        {
            logger.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {RequestId}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture);
        }
        else
        {
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        }
    }
}
