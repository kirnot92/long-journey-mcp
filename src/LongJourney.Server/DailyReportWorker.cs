using LongJourney.Core;

namespace LongJourney.Server;

/// <summary>Exports local activity independently of cognitive processing and its failures.</summary>
public sealed class DailyReportWorker(
    DailyReportService reports,
    EngineOptions options,
    TimeProvider timeProvider,
    ILogger<DailyReportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.DailyReportsEnabled)
        {
            return;
        }

        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var exports = reports.ExportClosedDays();
                if (exports.Count > 0)
                {
                    logger.LogInformation("Saved {Count} daily activity reports.", exports.Count);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Report failures must not alter ingestion or consolidation state.
                logger.LogWarning("Daily report export failed ({ErrorType}); the next poll will retry.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.DailyReportPollSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
