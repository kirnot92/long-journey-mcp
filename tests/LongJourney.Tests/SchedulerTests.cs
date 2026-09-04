using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task CatchupRunsSevenOldestClosedDaysThenWeekAndPersistsRestartPosition()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.TimeZoneId = "Asia/Seoul";
        var first = new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.FromHours(9));
        fixture.Observations(1, first);
        fixture.Clock.Now = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.FromHours(9));
        var scheduler = new MemoryScheduler(fixture.Store, fixture.Engine, fixture.Options, fixture.Clock);

        var results = await scheduler.TickAsync();
        var runs = results.Select(x => fixture.Store.GetRuns().Single(r => r.Id == x.RunId)).ToArray();
        Assert.Equal(9, results.Count);
        Assert.All(runs.Take(7), x => Assert.Equal(RunKind.Dream, x.Kind));
        Assert.Equal(RunKind.Meditation, runs[7].Kind);
        Assert.Equal(RunKind.Dream, runs[8].Kind);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 15, 0, 0, TimeSpan.Zero), runs[0].PeriodStart);
        Assert.Equal(runs[0].PeriodStart.AddDays(7), runs[7].PeriodEnd);
        Assert.Equal("2026-09-01", fixture.Store.GetState("scheduler.next_dream_date"));
        var restartedStore = new SqliteMemoryStore(fixture.Options);
        var restartedEngine = new ConsolidationEngine(restartedStore, fixture.Cognition, new ConsolidationSearch(), fixture.Options, fixture.Clock);
        var restarted = new MemoryScheduler(restartedStore, restartedEngine, fixture.Options, fixture.Clock);
        Assert.Empty(await restarted.TickAsync());
        fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        Assert.Single(await restarted.TickAsync());
    }

    [Fact]
    public async Task FailureDoesNotAdvanceDayAndRestartResumesFrozenWork()
    {
        using var fixture = new ConsolidationFixture();
        var first = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero);
        fixture.Observations(1, first);
        fixture.Clock.Now = first.AddDays(2);
        fixture.Cognition.Assimilate = (_, _) => throw new IOException("temporary provider failure");
        var scheduler = new MemoryScheduler(fixture.Store, fixture.Engine, fixture.Options, fixture.Clock);

        await Assert.ThrowsAsync<IOException>(() => scheduler.TickAsync());
        Assert.Equal("2026-09-01", fixture.Store.GetState("scheduler.next_dream_date"));
        var interrupted = fixture.Store.GetRuns().Single();
        Assert.Equal("running", interrupted.Status);
        fixture.Cognition.Assimilate = (_, _) => [];
        var results = await new MemoryScheduler(fixture.Store, fixture.Engine, fixture.Options, fixture.Clock).TickAsync();
        Assert.Equal(2, results.Count);
        Assert.Equal(interrupted.Id, results[0].RunId);
        Assert.Equal("2026-09-03", fixture.Store.GetState("scheduler.next_dream_date"));
    }

    [Fact]
    public async Task MissingBudgetDefersWeeksWithoutLosingThemAndRemembersEmptySourceDate()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MeditationBudgetUsd = null;
        var first = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);
        var source = fixture.Store.SaveSource("No useful observation.", first);
        Assert.True(fixture.Store.ClaimSource(source.Source.Id));
        fixture.Store.CompleteSource(source.Source.Id, [], first);
        fixture.Clock.Now = first.AddDays(8);
        var scheduler = new MemoryScheduler(fixture.Store, fixture.Engine, fixture.Options, fixture.Clock);

        var daily = await scheduler.TickAsync();
        Assert.Equal(8, daily.Count);
        Assert.DoesNotContain(fixture.Store.GetRuns(), x => x.Kind == RunKind.Meditation);
        Assert.Equal("2026-08-25", fixture.Store.GetState("scheduler.next_meditation_date"));
        fixture.Options.MeditationBudgetUsd = 1m;
        var weekly = await scheduler.TickAsync();
        Assert.Single(weekly);
        Assert.Equal("2026-09-01", fixture.Store.GetState("scheduler.next_meditation_date"));
    }
}
