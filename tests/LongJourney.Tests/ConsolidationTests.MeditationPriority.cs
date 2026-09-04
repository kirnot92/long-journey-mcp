using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed partial class ConsolidationTests
{
    [Fact]
    public async Task MeditationPriorityUsesOnlyChangedHigherDepthOwnersWithinTheFrozenPeriod()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var end = start.AddDays(7);
        var roots = fixture.Observations(9, start.AddDays(-20));
        var first = fixture.Abstraction(roots[..3], start);
        var second = fixture.Abstraction(roots[3..6], start.AddDays(-10));
        var third = fixture.Abstraction(roots[6..], start.AddDays(-10));
        var higher = fixture.Abstraction([first, second, third], start.AddDays(-1));
        var atEnd = fixture.Abstraction(roots[..3], end);
        var fresh = fixture.Observations(1, start.AddDays(1))[0];
        fixture.Relate(higher, fresh, RelationKind.Positive, start);
        fixture.Relate(second, fresh, RelationKind.Negative, start.AddTicks(-1));
        fixture.Relate(third, fresh, RelationKind.Negative, end);
        fixture.Store.RecordRecall([second.Id, third.Id], start.AddDays(2));

        var summary = await fixture.Engine.MeditateAsync(start, end);

        var candidates = Assert.Single(fixture.Cognition.PriorityBatches);
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, candidate => candidate.Memory.Id == first.Id);
        Assert.Contains(candidates, candidate => candidate.Memory.Id == higher.Id);
        Assert.DoesNotContain(candidates, candidate => candidate.Memory.Id == atEnd.Id);
        Assert.All(candidates, candidate =>
        {
            Assert.True(candidate.Memory.Depth >= 1);
            Assert.Equal(start, candidate.PeriodStart);
            Assert.Equal(end, candidate.PeriodEnd);
        });
        Assert.Equal("complete", summary.Status);
    }

    [Fact]
    public async Task MeditationPersistsPriorityBeforeCancellationAndReusesItAfterReopening()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var first = fixture.Abstraction(roots[..3], start.AddDays(1));
        var second = fixture.Abstraction(roots[3..6], start.AddDays(2));
        var third = fixture.Abstraction(roots[6..], start.AddDays(3));
        using var cancellation = new CancellationTokenSource();
        fixture.Cognition.Prioritize = (_, _) =>
        {
            cancellation.Cancel();
            return [$"abstract:{second.Id}", $"abstract:{first.Id}", $"abstract:{third.Id}"];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Engine.MeditateAsync(start, start.AddDays(7), cancellation.Token));
        var interrupted = Assert.Single(fixture.Store.GetRuns(), run => run.Kind == RunKind.Meditation);
        var savedWork = fixture.Store.GetWorkItems(interrupted.Id);
        Assert.True(fixture.Store.AreWorkItemsInitialized(interrupted.Id));
        Assert.Equal(3, savedWork.Count);
        Assert.Empty(fixture.Cognition.Neighborhoods);

        // These writes have timestamps in the period, but are beyond the original sequence limits.
        fixture.Abstraction(roots[..3], start.AddDays(4));
        fixture.Relate(first, roots[0], RelationKind.Negative, start.AddDays(5));
        fixture.Cognition.Prioritize = (_, _) => throw new InvalidOperationException("Priority must not run again.");
        var reopened = new SqliteMemoryStore(fixture.Options);
        var resumedEngine = new ConsolidationEngine(
            reopened, fixture.Cognition, new ConsolidationSearch(), fixture.Options, fixture.Clock);

        var result = await resumedEngine.MeditateAsync(start, start.AddDays(7));

        Assert.Equal("complete", result.Status);
        Assert.Single(fixture.Cognition.PriorityBatches);
        var resumedWork = reopened.GetWorkItems(interrupted.Id);
        Assert.Equal(savedWork.Count, resumedWork.Count);
        for (var index = 0; index < savedWork.Count; index++)
        {
            Assert.Equal(savedWork[index].Key, resumedWork[index].Key);
            Assert.Equal(index, resumedWork[index].Ordinal);
            Assert.Equal("complete", resumedWork[index].Status);
            Assert.Equal(savedWork[index].MemoryId, fixture.Cognition.Neighborhoods[index][0].Id);
        }
        Assert.Empty(fixture.Cognition.Neighborhoods[1][0].Relations);
        await resumedEngine.MeditateAsync(start, start.AddDays(7));
        Assert.Single(fixture.Cognition.PriorityBatches);
        Assert.Equal(3, fixture.Cognition.Neighborhoods.Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    public async Task InvalidMeditationOrderDoesNotInitializeOrProcessWork(string invalidOrder)
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(6, start.AddDays(-10));
        fixture.Abstraction(roots[..3], start.AddDays(1));
        fixture.Abstraction(roots[3..], start.AddDays(2));
        fixture.Cognition.Prioritize = (candidates, _) => invalidOrder switch
        {
            "duplicate" => [candidates[0].WorkKey, candidates[0].WorkKey],
            "unknown" => [candidates[0].WorkKey, "unknown"],
            _ => [candidates[0].WorkKey]
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Engine.MeditateAsync(start, start.AddDays(7)));

        var run = Assert.Single(fixture.Store.GetRuns(), candidate => candidate.Kind == RunKind.Meditation);
        Assert.Equal("running", run.Status);
        Assert.False(fixture.Store.AreWorkItemsInitialized(run.Id));
        Assert.Empty(fixture.Store.GetWorkItems(run.Id));
        Assert.Empty(fixture.Cognition.Neighborhoods);
        fixture.Cognition.Prioritize = null;
        var result = await fixture.Engine.MeditateAsync(start, start.AddDays(7));
        Assert.Equal("complete", result.Status);
        Assert.Equal(2, fixture.Store.GetWorkItems(run.Id).Count);
    }

    [Fact]
    public async Task EmptyMeditationInitializesWithoutPriorityCalls()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        fixture.Observations(1, start.AddDays(1));
        fixture.Cognition.Prioritize = (_, _) => throw new InvalidOperationException("No work to prioritize.");

        var result = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        Assert.Equal("complete", result.Status);
        Assert.True(fixture.Store.AreWorkItemsInitialized(result.RunId));
        Assert.Empty(fixture.Store.GetWorkItems(result.RunId));
        Assert.Empty(fixture.Cognition.Calls);
        Assert.Equal(0m, result.AccountedUsd);
    }

    [Fact]
    public async Task UnaffordablePriorityPreservesCarryAndRanksItWithNewChangesUnderDistinctKeys()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(3, start.AddDays(-10));
        var memory = fixture.Abstraction(roots, start.AddDays(1));
        fixture.Cognition.Prioritize = (_, context) =>
        {
            fixture.Store.ReserveUsage(context.RunId, "fake", "meditation_priority", 2m, fixture.Clock.Now);
            throw new InvalidOperationException("The reservation should have been rejected.");
        };

        var exhausted = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        Assert.Equal("budget_exhausted", exhausted.Status);
        Assert.Equal(0m, exhausted.AccountedUsd);
        var origin = Assert.Single(fixture.Store.GetRuns(), run => run.Id == exhausted.RunId);
        Assert.Equal("budget_exhausted", origin.Status);
        Assert.True(fixture.Store.AreWorkItemsInitialized(origin.Id));
        var pending = Assert.Single(fixture.Store.GetWorkItems(origin.Id));
        Assert.Equal("pending", pending.Status);
        Assert.Empty(fixture.Cognition.Neighborhoods);
        await fixture.Engine.MeditateAsync(start, start.AddDays(7));
        Assert.Single(fixture.Cognition.PriorityBatches);

        fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        fixture.Relate(memory, roots[0], RelationKind.Positive, start.AddDays(8));
        var carryKey = $"carry:{origin.Id}:{pending.Key}";
        fixture.Cognition.Prioritize = (candidates, context) =>
        {
            Assert.Equal(2, candidates.Count);
            var carried = Assert.Single(candidates, candidate => candidate.WorkKey == carryKey);
            var current = Assert.Single(candidates, candidate => candidate.WorkKey == pending.Key);
            Assert.Equal(memory.Id, carried.Memory.Id);
            Assert.Equal(memory.Id, current.Memory.Id);
            Assert.Empty(carried.Memory.Relations);
            Assert.Single(current.Memory.Relations);
            Assert.Equal(start, carried.PeriodStart);
            Assert.Equal(start.AddDays(7), current.PeriodStart);
            var reservation = fixture.Store.ReserveUsage(
                context.RunId, "fake", "meditation_priority", 0.2m, fixture.Clock.Now);
            fixture.Store.CompleteUsage(reservation.Id, new ApiUsage(1, 0, 0, 0.1m), fixture.Clock.Now);
            return [carryKey, pending.Key];
        };

        var next = await fixture.Engine.MeditateAsync(start.AddDays(7), start.AddDays(14));

        Assert.Equal("complete", next.Status);
        Assert.Equal(0.1m, next.AccountedUsd);
        Assert.Equal(0m, fixture.Store.GetRunAccountedUsd(origin.Id));
        Assert.Equal("complete", Assert.Single(fixture.Store.GetWorkItems(origin.Id)).Status);
        var nextWork = fixture.Store.GetWorkItems(next.RunId);
        Assert.Equal(carryKey, nextWork[0].Key);
        Assert.Equal(pending.Key, nextWork[1].Key);
        Assert.Empty(fixture.Cognition.Neighborhoods[0][0].Relations);
        Assert.Single(fixture.Cognition.Neighborhoods[1][0].Relations);
        Assert.All(fixture.Cognition.Contexts.GetRange(1, fixture.Cognition.Contexts.Count - 1),
            context => Assert.Equal(next.RunId, context.RunId));
    }

    [Fact]
    public void FailedUnprioritizedQueueSaveRollsBackBothWorkAndRunStatus()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(3, start.AddDays(-10));
        var memory = fixture.Abstraction(roots, start.AddDays(1));
        var run = fixture.Store.GetOrCreateRun(
            RunKind.Meditation, start, start.AddDays(7), fixture.Clock.Now, 1m);

        Assert.Throws<SqliteException>(() => fixture.Store.FinishUnprioritizedMeditation(run.Id,
            [new WorkSeed("valid", "consolidation", memory.Id, 0),
             new WorkSeed("invalid", "consolidation", "missing-memory", 1)], fixture.Clock.Now));

        Assert.Empty(fixture.Store.GetWorkItems(run.Id));
        Assert.False(fixture.Store.AreWorkItemsInitialized(run.Id));
        Assert.Equal("running", fixture.Store.InspectRun(run.Id)!.Run.Run.Status);
        Assert.Null(fixture.Store.InspectRun(run.Id)!.Run.FinishedAt);
    }
}
