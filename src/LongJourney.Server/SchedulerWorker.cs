using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Server;

public sealed class SchedulerWorker(
    MemoryEngine engine,
    MemoryScheduler scheduler,
    EngineOptions options,
    ICognition cognition,
    OpenAiApiKeySource apiKeySource,
    TimeProvider timeProvider,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (cognition is OpenAiCognition)
        {
            try
            {
                if (apiKeySource.Read() is null)
                {
                    logger.LogWarning("Set OPENAI_API_KEY or provide key.txt before using cognitive operations.");
                }
            }
            catch (InputException)
            {
                logger.LogWarning("OpenAI credentials are unreadable or invalid. Check OPENAI_API_KEY, key.txt or OpenAI:ApiKeyFile.");
            }
        }

        if (!options.SchedulerEnabled)
        {
            logger.LogInformation("Background source recovery, Dream and Meditation polling are disabled.");
            return;
        }

        if (options.MeditationBudgetUsd is null)
        {
            logger.LogWarning("Weekly Meditation is pending: set Engine:MeditationBudgetUsd to the desired N-dollar run budget. Daily Dream remains enabled without a budget limit.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverSourcesAndConsolidateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Background processing failed ({ErrorType}); saved work will be retried on the next poll. Check OpenAI credentials, access, and connectivity.",
                    exception.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.SchedulerPollSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverSourcesAndConsolidateAsync(CancellationToken cancellationToken)
    {
        // Individual failed sources remain retryable; they must not block consolidation of existing memories.
        var failures = await engine.ResumePendingAsync(cancellationToken);
        SourceRecoveryLog.WriteIfIncomplete(logger, failures);

        var results = await scheduler.TickAsync(cancellationToken);
        foreach (var result in results)
        {
            logger.LogInformation(
                "Consolidation run {RunId}: {Status}, {CompletedItems} completed items, {RejectedProposals} rejected proposals, USD {AccountedUsd} accounted.",
                result.RunId, result.Status, result.CompletedItems, result.RejectedProposals, result.AccountedUsd);
        }
    }
}
