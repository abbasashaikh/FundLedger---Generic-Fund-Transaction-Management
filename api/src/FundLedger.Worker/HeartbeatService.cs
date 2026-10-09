namespace FundLedger.Worker;

/// <summary>
/// Logs a heartbeat every few minutes so a stalled worker is visible in logs and
/// alerting. Replaced/joined by the export and cleanup jobs in later phases.
/// </summary>
internal sealed partial class HeartbeatService(ILogger<HeartbeatService> logger, IConfiguration configuration)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(configuration.GetValue("Worker:HeartbeatMinutes", 5));
        using var timer = new PeriodicTimer(interval);

        LogStarted(logger, interval.TotalMinutes);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                LogHeartbeat(logger);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker started; heartbeat every {IntervalMinutes} min")]
    private static partial void LogStarted(ILogger logger, double intervalMinutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker heartbeat")]
    private static partial void LogHeartbeat(ILogger logger);
}
