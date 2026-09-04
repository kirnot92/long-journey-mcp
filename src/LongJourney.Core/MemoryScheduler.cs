using System.Globalization;

namespace LongJourney.Core;

/// <summary>Advances persisted, closed local-calendar periods; the host controls polling.</summary>
public sealed class MemoryScheduler(
    IMemoryStore store,
    ConsolidationEngine consolidation,
    EngineOptions options,
    TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string AnchorKey = "scheduler.anchor_date";
    private const string DreamKey = "scheduler.next_dream_date";
    private const string MeditationKey = "scheduler.next_meditation_date";
    private const string TimeZoneKey = "scheduler.timezone";

    public async Task<IReadOnlyList<RunSummary>> TickAsync(CancellationToken cancellationToken = default)
    {
        if (!options.SchedulerEnabled)
        {
            return [];
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
            var today = LocalDate(timeProvider.GetUtcNow(), timeZone);
            var savedTimeZoneId = store.GetState(TimeZoneKey);
            if (savedTimeZoneId is not null && savedTimeZoneId != options.TimeZoneId)
            {
                throw new InputException(
                    "Scheduler timezone differs from the persisted calendar. Migrate scheduler state before changing it.");
            }

            if (savedTimeZoneId is null)
            {
                store.SetState(TimeZoneKey, options.TimeZoneId);
            }

            var anchor = ReadDate(AnchorKey);
            if (anchor is null)
            {
                anchor = FindAnchor(today, timeZone);
                store.SetState(AnchorKey, Format(anchor.Value));
            }

            var nextDreamDate = ReadOrInitializeDate(DreamKey, anchor.Value);
            var nextMeditationDate = ReadOrInitializeDate(MeditationKey, anchor.Value);
            var completedRuns = new List<RunSummary>();

            // A week is ready only after all its daily periods have completed.
            // This also resumes a week interrupted after its seventh Dream had finished.
            var completedDaysEnd = nextDreamDate < today ? nextDreamDate : today;
            nextMeditationDate = await RunDueMeditationsAsync(
                nextMeditationDate, completedDaysEnd, timeZone, completedRuns, cancellationToken);

            while (nextDreamDate < today)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dayEnd = nextDreamDate.AddDays(1);
                var periodStart = StartOfLocalDay(nextDreamDate, timeZone);
                var periodEnd = StartOfLocalDay(dayEnd, timeZone);
                var result = await consolidation.DreamAsync(
                    periodStart, periodEnd, cancellationToken);

                if (result.Status != "complete")
                {
                    throw new InvariantException("A daily scheduler period did not complete.");
                }

                completedRuns.Add(result);
                nextDreamDate = dayEnd;
                store.SetState(DreamKey, Format(nextDreamDate));

                nextMeditationDate = await RunDueMeditationsAsync(
                    nextMeditationDate, nextDreamDate, timeZone, completedRuns, cancellationToken);
            }

            return completedRuns;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DateOnly> RunDueMeditationsAsync(
        DateOnly nextMeditationDate,
        DateOnly completedDaysEnd,
        TimeZoneInfo timeZone,
        List<RunSummary> completedRuns,
        CancellationToken cancellationToken)
    {
        if (options.MeditationBudgetUsd is null)
        {
            return nextMeditationDate;
        }

        var weekEnd = nextMeditationDate.AddDays(7);
        while (weekEnd <= completedDaysEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var periodStart = StartOfLocalDay(nextMeditationDate, timeZone);
            var periodEnd = StartOfLocalDay(weekEnd, timeZone);
            var result = await consolidation.MeditateAsync(
                periodStart, periodEnd, cancellationToken);

            if (result.Status is not ("complete" or "budget_exhausted"))
            {
                throw new InvariantException("A weekly scheduler period did not reach a terminal state.");
            }

            completedRuns.Add(result);
            nextMeditationDate = weekEnd;
            store.SetState(MeditationKey, Format(nextMeditationDate));
            weekEnd = nextMeditationDate.AddDays(7);
        }

        return nextMeditationDate;
    }

    private DateOnly FindAnchor(DateOnly today, TimeZoneInfo timeZone)
    {
        var anchor = today;
        foreach (var memory in store.ReadSnapshot().Memories)
        {
            var createdDate = LocalDate(memory.CreatedAt, timeZone);
            if (createdDate < anchor)
            {
                anchor = createdDate;
            }
        }

        foreach (var source in store.GetIncompleteSources())
        {
            var createdDate = LocalDate(source.CreatedAt, timeZone);
            if (createdDate < anchor)
            {
                anchor = createdDate;
            }
        }

        var firstSourceTimestamp = store.GetState("corpus.first_source_at");
        if (firstSourceTimestamp is not null)
        {
            var firstSourceCreatedAt = DateTimeOffset.Parse(
                firstSourceTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var firstSourceDate = LocalDate(firstSourceCreatedAt, timeZone);
            if (firstSourceDate < anchor)
            {
                anchor = firstSourceDate;
            }
        }

        return anchor;
    }

    private DateOnly ReadOrInitializeDate(string key, DateOnly initialDate)
    {
        var savedDate = ReadDate(key);
        if (savedDate is not null)
        {
            return savedDate.Value;
        }

        store.SetState(key, Format(initialDate));
        return initialDate;
    }

    private DateOnly? ReadDate(string key)
    {
        var value = store.GetState(key);
        if (value is null)
        {
            return null;
        }

        return DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Format(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo timeZone)
    {
        var localTimestamp = TimeZoneInfo.ConvertTime(timestamp, timeZone);
        return DateOnly.FromDateTime(localTimestamp.DateTime);
    }

    private static DateTimeOffset StartOfLocalDay(DateOnly date, TimeZoneInfo timeZone)
    {
        var localTime = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Some zones move their clocks at midnight. Use the first valid instant of that day.
        while (timeZone.IsInvalidTime(localTime))
        {
            localTime = localTime.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone));
    }
}
