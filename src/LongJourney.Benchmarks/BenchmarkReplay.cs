using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed class ReplayClock(DateTimeOffset initialTime) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = initialTime;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class BenchmarkProgress
{
    public DateTimeOffset Clock { get; set; }
    public int NextObservation { get; set; }
    public bool IngestionComplete { get; set; }
    public IReadOnlyList<string>? RecalledIds { get; set; }
    public IReadOnlyList<AnswerEvidence>? Evidence { get; set; }
    public CognitiveResult<string>? Answer { get; set; }
    public CognitiveResult<BenchmarkJudgment>? Judgment { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Status { get; set; } = "running";
    public string? ErrorType { get; set; }
}

/// <summary>Replays closed UTC days before admitting later evidence.</summary>
public sealed class BenchmarkReplay(
    IMemoryStore store, MemoryEngine memory, MemoryScheduler scheduler,
    ReplayClock clock, BenchmarkProgress progress)
{
    public const string ProgressKey = "benchmark.progress";

    public void Save()
    {
        store.SetState(ProgressKey, JsonSerializer.Serialize(progress, JsonDefaults.Options));
    }

    public async Task IngestAsync(BenchmarkHistory history, DateTimeOffset questionTime,
        CancellationToken cancellationToken)
    {
        if (progress.IngestionComplete)
        {
            return;
        }
        while (progress.NextObservation < history.Observations.Count)
        {
            var observation = history.Observations[progress.NextObservation];
            await AdvanceAsync(observation.At, cancellationToken);
            // A failed extraction is retried through remember at this event's original clock.
            // Completed sources are deduplicated if the process died before saving the cursor.
            var result = await memory.RememberAsync(observation.Raw, cancellationToken);
            if (result.Status != "complete")
            {
                throw new InvariantException("Benchmark observation did not complete.");
            }
            progress.NextObservation++;
            Save();
        }
        await AdvanceAsync(questionTime, cancellationToken);
        progress.IngestionComplete = true;
        Save();
    }

    private async Task AdvanceAsync(DateTimeOffset target, CancellationToken cancellationToken)
    {
        if (target < progress.Clock)
        {
            throw new InvariantException("Benchmark clock cannot move backwards.");
        }
        clock.Now = progress.Clock;
        // Retry a boundary interrupted after the clock checkpoint but before its runs completed.
        await scheduler.TickAsync(cancellationToken);
        var boundary = new DateTimeOffset(progress.Clock.UtcDateTime.Date, TimeSpan.Zero).AddDays(1);
        while (boundary <= target)
        {
            SetClock(boundary);
            await scheduler.TickAsync(cancellationToken);
            boundary = boundary.AddDays(1);
        }
        SetClock(target);
    }

    private void SetClock(DateTimeOffset value)
    {
        progress.Clock = value;
        clock.Now = value;
        Save();
    }
}
