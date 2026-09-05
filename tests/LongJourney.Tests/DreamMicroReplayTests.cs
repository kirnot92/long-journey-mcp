using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class DreamMicroReplayTests
{
    [Fact]
    public async Task SparseReplaySharesExactD0WithoutFutureLeakageMeditationOrUnclosedFinalDay()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MaxObservations = 2;
        var start = new DateTimeOffset(2023, 5, 20, 10, 0, 0, TimeSpan.Zero);
        BenchmarkSession[] sessions =
        [
            AddShared(fixture, "first", start, 2),
            AddShared(fixture, "later", start.AddDays(8), 2),
            AddShared(fixture, "after-question", start.AddDays(8).AddHours(1), 2)
        ];
        var options = new EngineOptions
        {
            DataDirectory = Path.Combine(fixture.Options.DataDirectory, "dream"),
            MaxObservations = 2,
            TimeZoneId = "UTC"
        };
        var dream = new SqliteMemoryStore(options);
        var clock = new BenchmarkClock();
        var cognition = new ConsolidationCognition();
        cognition.Assimilate = (_, _) =>
        {
            Assert.Equal(start.UtcDateTime.Date.AddDays(1), clock.UtcNow.UtcDateTime);
            Assert.Equal(2, dream.ReadSnapshot().Memories.Count);
            return [];
        };
        var consolidation = new ConsolidationEngine(dream, cognition, new ConsolidationSearch(), options, clock);

        var mapping = await DreamMicroReplay.ReplayAsync(sessions, start.AddDays(8),
            fixture.Store, dream, consolidation, clock, cognition.EmbeddingSpace, CancellationToken.None);

        var run = Assert.Single(dream.GetRuns());
        Assert.Equal(RunKind.Dream, run.Kind);
        Assert.Equal(start.UtcDateTime.Date, run.PeriodStart.UtcDateTime);
        Assert.Equal(start.AddDays(8).AddHours(1), clock.UtcNow);
        Assert.Equal(3, mapping.Count);
        Assert.Empty(fixture.Store.GetRuns());
        Assert.DoesNotContain("priority", cognition.Calls);
        foreach (var original in fixture.Store.ReadSnapshot().Memories)
        {
            var imported = dream.ReadSnapshot().ById[original.Id];
            Assert.Equal(original.Content, imported.Content);
            Assert.Equal(original.SourceRef, imported.SourceRef);
            Assert.Equal(original.CreatedAt, imported.CreatedAt);
            Assert.Equal(fixture.Store.GetEmbedding(original.Id, cognition.EmbeddingSpace)!.Values,
                dream.GetEmbedding(imported.Id, cognition.EmbeddingSpace)!.Values);
        }
        var calls = cognition.Calls.Count;
        await DreamMicroReplay.ReplayAsync(sessions, start.AddDays(8),
            fixture.Store, dream, consolidation, clock, cognition.EmbeddingSpace, CancellationToken.None);
        Assert.Equal(calls, cognition.Calls.Count);
        Assert.Equal(6, dream.ReadSnapshot().Memories.Count);
    }

    [Fact]
    public async Task InterruptedDreamResumesBeforeImportingLaterSessionsAndSkipsEmptyDays()
    {
        using var fixture = new ConsolidationFixture();
        var start = new DateTimeOffset(2023, 5, 20, 10, 0, 0, TimeSpan.Zero);
        BenchmarkSession[] sessions =
        [
            AddShared(fixture, "empty", start, 0),
            AddShared(fixture, "first", start.AddDays(2), 1),
            AddShared(fixture, "later", start.AddDays(4), 1)
        ];
        var options = new EngineOptions { DataDirectory = Path.Combine(fixture.Options.DataDirectory, "dream"), TimeZoneId = "UTC" };
        var dream = new SqliteMemoryStore(options);
        var clock = new BenchmarkClock();
        var cognition = new ConsolidationCognition();
        cognition.Assimilate = (_, _) => throw new IOException("Interrupted before later import.");
        var consolidation = new ConsolidationEngine(dream, cognition, new ConsolidationSearch(), options, clock);
        await Assert.ThrowsAsync<IOException>(() => DreamMicroReplay.ReplayAsync(sessions, start.AddDays(5),
            fixture.Store, dream, consolidation, clock, cognition.EmbeddingSpace, CancellationToken.None));
        Assert.Single(dream.ReadSnapshot().Memories);
        Assert.Equal(start.AddDays(2).UtcDateTime.Date, Assert.Single(dream.GetRuns()).PeriodStart.UtcDateTime);

        cognition.Assimilate = (_, _) => [];
        await DreamMicroReplay.ReplayAsync(sessions, start.AddDays(5),
            fixture.Store, dream, consolidation, clock, cognition.EmbeddingSpace, CancellationToken.None);
        Assert.Equal(2, dream.ReadSnapshot().Memories.Count);
        Assert.Equal(2, dream.GetRuns().Count);
        Assert.All(dream.GetRuns(), run => Assert.Equal("complete", run.Status));
    }

    private static BenchmarkSession AddShared(ConsolidationFixture fixture, string id, DateTimeOffset timestamp, int count)
    {
        var raw = "opaque session: " + id;
        var source = fixture.Store.SaveSource(raw, timestamp);
        Assert.True(fixture.Store.ClaimSource(source.Source.Id));
        var observations = new List<NewObservation>();
        for (var index = 0; index < count; index++)
        {
            observations.Add(new NewObservation($"{id} fact {index}", "fake", ConsolidationFixture.Vector));
        }
        fixture.Store.CompleteSource(source.Source.Id, observations, timestamp);
        return new BenchmarkSession(id, timestamp, raw);
    }
}
