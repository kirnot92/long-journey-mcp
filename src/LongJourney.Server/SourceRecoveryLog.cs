using LongJourney.Core;

namespace LongJourney.Server;

internal static class SourceRecoveryLog
{
    public static void WriteIfIncomplete(ILogger logger, IReadOnlyList<IngestionFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        // Both startup reindex and background recovery report types, never raw input or exception text.
        var seenErrorTypes = new HashSet<string>(StringComparer.Ordinal);
        var errorTypes = new List<string>();
        foreach (var failure in failures)
        {
            if (seenErrorTypes.Add(failure.ErrorType))
            {
                errorTypes.Add(failure.ErrorType);
            }
        }

        logger.LogWarning(
            "Source recovery left {FailureCount} inputs for retry ({ErrorTypes}). Continuing with existing memories.",
            failures.Count, string.Join(", ", errorTypes));
    }
}
