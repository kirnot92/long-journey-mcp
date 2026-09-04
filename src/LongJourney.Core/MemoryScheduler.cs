using System.Globalization;

namespace LongJourney.Core;

/// <summary>Advances persisted, closed local-calendar periods; the host controls polling.</summary>
public sealed class MemoryScheduler(
    IMemoryStore store, ConsolidationEngine consolidation,
    EngineOptions options, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string AnchorKey = "scheduler.anchor_date";
    private const string DreamKey = "scheduler.next_dream_date";
    private const string MeditationKey = "scheduler.next_meditation_date";
    private const string TimeZoneKey = "scheduler.timezone";

    public async Task<IReadOnlyList<RunSummary>> TickAsync(CancellationToken cancellationToken = default)
    {
        if (!options.SchedulerEnabled) return [];
        await gate.WaitAsync(cancellationToken);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
            var today = LocalDate(timeProvider.GetUtcNow(), zone);
            var savedZone = store.GetState(TimeZoneKey);
            if (savedZone is not null && savedZone != options.TimeZoneId)
                throw new InputException("Scheduler timezone differs from the persisted calendar. Migrate scheduler state before changing it.");
            store.SetState(TimeZoneKey, options.TimeZoneId);
            var anchor = ReadDate(AnchorKey) ?? FindAnchor(today, zone);
            store.SetState(AnchorKey, Format(anchor));
            var nextDream = ReadDate(DreamKey) ?? anchor;
            var nextMeditation = ReadDate(MeditationKey) ?? anchor;
            store.SetState(DreamKey, Format(nextDream));
            store.SetState(MeditationKey, Format(nextMeditation));
            var results = new List<RunSummary>();

            async Task RunDueWeeksAsync()
            {
                if (options.MeditationBudgetUsd is null) return;
                while (nextMeditation.AddDays(7) <= nextDream && nextMeditation.AddDays(7) <= today)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var end = nextMeditation.AddDays(7);
                    var result = await consolidation.MeditateAsync(Boundary(nextMeditation, zone), Boundary(end, zone), cancellationToken);
                    if (result.Status is not ("complete" or "budget_exhausted"))
                        throw new InvariantException("A weekly scheduler period did not reach a terminal state.");
                    results.Add(result);
                    nextMeditation = end;
                    store.SetState(MeditationKey, Format(nextMeditation));
                }
            }

            // Resume a weekly run if a prior process stopped after its seventh daily completion.
            await RunDueWeeksAsync();
            while (nextDream < today)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = nextDream.AddDays(1);
                var result = await consolidation.DreamAsync(Boundary(nextDream, zone), Boundary(end, zone), cancellationToken);
                if (result.Status != "complete") throw new InvariantException("A daily scheduler period did not complete.");
                results.Add(result);
                nextDream = end;
                store.SetState(DreamKey, Format(nextDream));
                await RunDueWeeksAsync();
            }
            return results;
        }
        finally { gate.Release(); }
    }

    private DateOnly FindAnchor(DateOnly today, TimeZoneInfo zone)
    {
        var instants = store.ReadSnapshot().Memories.Select(x => x.CreatedAt)
            .Concat(store.GetIncompleteSources().Select(x => x.CreatedAt)).ToList();
        if (store.GetState("corpus.first_source_at") is { } earliest)
            instants.Add(DateTimeOffset.Parse(earliest, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        return instants.Select(x => LocalDate(x, zone)).Append(today).Min();
    }

    private DateOnly? ReadDate(string key) => store.GetState(key) is { } value
        ? DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
    private static DateTimeOffset Boundary(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones move their clocks at midnight. Use the first valid instant of that day.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
    }
}
