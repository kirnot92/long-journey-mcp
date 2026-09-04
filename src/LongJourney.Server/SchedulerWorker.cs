using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Server;

public sealed class SchedulerWorker(
    MemoryEngine engine, MemoryScheduler scheduler, EngineOptions options, ICognition cognition,
    TimeProvider timeProvider, ILogger<SchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (cognition is OpenAiCognition && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            logger.LogWarning("OPENAI_API_KEY is not set. Cognitive operations will return a configuration error until it is available.");
        if (!options.SchedulerEnabled)
        {
            logger.LogInformation("Background source recovery, Dream and Meditation polling are disabled.");
            return;
        }
        if (options.MeditationBudgetUsd is null)
            logger.LogWarning("Weekly Meditation is pending: set Engine:MeditationBudgetUsd to the desired N-dollar run budget. Daily Dream remains enabled without a budget limit.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A failed Source must not starve other ingestion or already-created memories.
                var failures = await engine.ResumePendingAsync(stoppingToken);
                if (failures.Count > 0)
                    logger.LogWarning("Source recovery left {FailureCount} inputs for retry ({ErrorTypes}). Continuing consolidation for existing memories.",
                        failures.Count, string.Join(", ", failures.Select(x => x.ErrorType).Distinct(StringComparer.Ordinal)));
                foreach (var result in await scheduler.TickAsync(stoppingToken))
                    logger.LogInformation("Consolidation run {RunId}: {Status}, {CompletedItems} completed items, {RejectedProposals} rejected proposals, USD {AccountedUsd} accounted.",
                        result.RunId, result.Status, result.CompletedItems, result.RejectedProposals, result.AccountedUsd);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Background processing failed ({ErrorType}); saved work will be retried on the next poll. Check OpenAI credentials, access, and connectivity.", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.SchedulerPollSeconds), timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
