using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkReplayTests
{
    private static readonly DateTimeOffset Start = new(2023, 5, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReplaySharesObservationIdsContentTimestampsAndVectorsWithoutFutureLeakage()
    {
        using var fixture = new ReplayFixture();
        var snapshots = new List<(DateTimeOffset At, int Memories)>();
        fixture.Consolidation.Assimilate = (_, candidates) =>
        {
            var snapshot = fixture.Full.ReadSnapshot();
            foreach (var memory in snapshot.Memories)
            {
                Assert.True(memory.CreatedAt <= fixture.FullClock.UtcNow);
            }

            snapshots.Add((fixture.FullClock.UtcNow, snapshot.Memories.Count));
            return [];
        };
        var later = new BenchmarkSession("session-later", Start.AddDays(2), "opaque ordinal 1: later");
        var first = new BenchmarkSession("session-first", Start, "opaque ordinal 0: first");
        var questionDate = Start.AddDays(3);

        var mapping = await fixture.Replay([later, first], questionDate);

        Assert.Equal(new[] { first.Raw, later.Raw }, fixture.Extraction.RawInputs);
        Assert.Equal(2, mapping.Count);
        var original = fixture.Baseline.ReadSnapshot().Memories;
        var imported = fixture.Full.ReadSnapshot().Memories;
        Assert.Equal(4, original.Count);
        Assert.Equal(4, imported.Count);
        for (var index = 0; index < original.Count; index++)
        {
            Assert.Equal(original[index].Id, imported[index].Id);
            Assert.Equal(original[index].Content, imported[index].Content);
            Assert.Equal(original[index].CreatedAt, imported[index].CreatedAt);
            Assert.Equal(original[index].SourceRef, imported[index].SourceRef);
            Assert.Equal(
                fixture.Baseline.GetEmbedding(original[index].Id, fixture.Extraction.EmbeddingSpace)!.Values,
                fixture.Full.GetEmbedding(imported[index].Id, fixture.Extraction.EmbeddingSpace)!.Values);
        }

        var runs = fixture.Full.GetRuns();
        Assert.Equal(3, runs.Count);
        Assert.Equal(2, runs[0].MemoryHighWater);
        Assert.Equal(2, runs[1].MemoryHighWater);
        Assert.Equal(4, runs[2].MemoryHighWater);
        Assert.All(runs, run => Assert.True(run.PeriodEnd <= questionDate));
        Assert.Contains(snapshots, snapshot => snapshot.At.Date == Start.AddDays(1).Date && snapshot.Memories == 2);
        Assert.DoesNotContain(runs, run => run.PeriodStart.Date == questionDate.Date);
        Assert.Equal(questionDate, fixture.BaselineClock.UtcNow);
        Assert.Equal(questionDate, fixture.FullClock.UtcNow);
        Assert.Empty(fixture.Baseline.GetRuns());
    }

    [Fact]
    public async Task InterruptedImportResumesSharedExtractionWithoutReextractingOrClosingDayEarly()
    {
        using var fixture = new ReplayFixture();
        fixture.FullOptions.MaxObservations = 1;
        BenchmarkSession[] sessions =
        [
            new("first", Start, "opaque 0: first"),
            new("second", Start.AddHours(1), "opaque 1: second")
        ];

        await Assert.ThrowsAsync<InvariantException>(() => fixture.Replay(sessions, Start.AddHours(2)));
        Assert.Single(fixture.Extraction.RawInputs);
        Assert.Equal(2, fixture.Baseline.ReadSnapshot().Memories.Count);
        Assert.Empty(fixture.Full.ReadSnapshot().Memories);
        Assert.Empty(fixture.Full.GetRuns());
        fixture.FullOptions.MaxObservations = 3;
        fixture.Restart();

        await fixture.Replay(sessions, Start.AddHours(2));

        Assert.Equal(2, fixture.Extraction.RawInputs.Count);
        Assert.Equal(4, fixture.Full.ReadSnapshot().Memories.Count);
        Assert.Empty(fixture.Full.GetRuns());
    }

    [Fact]
    public async Task DuplicateDatasetSessionLabelsMapDistinctSourcesWithoutDeduplication()
    {
        using var fixture = new ReplayFixture();
        BenchmarkSession[] sessions =
        [
            new("07b7a667_1", Start, "opaque ordinal 0: identical transcript"),
            new("07b7a667_1", Start, "opaque ordinal 1: identical transcript")
        ];

        var mapping = await fixture.Replay(sessions, Start.AddHours(1));

        Assert.Equal(2, mapping.Count);
        Assert.All(mapping.Values, id => Assert.Equal("07b7a667_1", id));
        Assert.Equal(2, fixture.Extraction.RawInputs.Count);
        Assert.Equal(4, fixture.Full.ReadSnapshot().Memories.Count);
    }

    [Fact]
    public async Task SameDayLateHistoryAdvancesClockWithoutDroppingSessionsOrAddingClosedDay()
    {
        using var fixture = new ReplayFixture();
        var questionDate = Start.AddHours(1);
        BenchmarkSession[] sessions =
        [
            new("earlier", Start.AddDays(-1), "opaque ordinal 0: earlier"),
            new("late", Start.AddHours(11), "opaque ordinal 1: late same-day history")
        ];

        var mapping = await fixture.Replay(sessions, questionDate);

        Assert.Equal(2, mapping.Count);
        Assert.Equal(2, fixture.Extraction.RawInputs.Count);
        Assert.Equal(sessions[1].Timestamp, fixture.FullClock.UtcNow);
        Assert.Equal(sessions[1].Timestamp, fixture.BaselineClock.UtcNow);
        Assert.Equal(sessions[1].Timestamp, BenchmarkReplay.EvaluationTime(sessions, questionDate));
        var run = Assert.Single(fixture.Full.GetRuns());
        Assert.Equal(Start.AddDays(-1).Date, run.PeriodStart.Date);
        Assert.Equal(Start.Date, run.PeriodEnd.Date);
        Assert.Contains(fixture.Full.ReadSnapshot().Memories, memory => memory.CreatedAt == sessions[1].Timestamp);
        await fixture.Replay(sessions, questionDate);
        Assert.Equal(2, fixture.Extraction.RawInputs.Count);
        Assert.Single(fixture.Full.GetRuns());
    }

    [Fact]
    public async Task InterruptedSchedulerResumesAtSameBoundaryBeforeImportingFutureSessions()
    {
        using var fixture = new ReplayFixture();
        BenchmarkSession[] sessions =
        [
            new("first", Start, "opaque 0: first"),
            new("second", Start.AddDays(1), "opaque 1: second"),
            new("third", Start.AddDays(2), "opaque 2: third")
        ];
        fixture.Consolidation.Assimilate = (_, _) => throw new IOException("injected scheduler interruption");
        await Assert.ThrowsAsync<IOException>(() => fixture.Replay(sessions, Start.AddDays(3)));
        var interrupted = Assert.Single(fixture.Full.GetRuns());
        Assert.Equal("running", interrupted.Status);
        Assert.Equal(2, fixture.Full.ReadSnapshot().Memories.Count);
        Assert.Single(fixture.Extraction.RawInputs);
        var boundary = fixture.FullClock.UtcNow;
        fixture.Consolidation.Assimilate = (_, candidates) =>
        {
            Assert.True(fixture.FullClock.UtcNow >= boundary);
            Assert.All(candidates, candidate => Assert.True(candidate.CreatedAt <= fixture.FullClock.UtcNow));
            return [];
        };
        fixture.Restart();

        await fixture.Replay(sessions, Start.AddDays(3));

        Assert.Equal(3, fixture.Extraction.RawInputs.Count);
        Assert.Equal(3, fixture.Full.GetRuns().Count);
        var resumed = Assert.Single(fixture.Full.GetRuns(), run => run.Id == interrupted.Id);
        Assert.Equal("complete", resumed.Status);
        Assert.Equal(2, resumed.MemoryHighWater);
        var callsAfterCompletion = fixture.Consolidation.Calls.Count;
        await fixture.Replay(sessions, Start.AddDays(3));
        Assert.Equal(callsAfterCompletion, fixture.Consolidation.Calls.Count);
        Assert.Equal(3, fixture.Extraction.RawInputs.Count);
    }

    [Fact]
    public async Task SeventhClosedDayRunsMeditationWithHardFiveDollarLimit()
    {
        using var fixture = new ReplayFixture();
        fixture.Consolidation.Abstract = (neighborhood, _, role) =>
        {
            if (role == CognitionRole.Meditation)
            {
                var weeklyRun = Assert.Single(fixture.Full.GetRuns(), run => run.Kind == RunKind.Meditation);
                fixture.Full.ReserveUsage(weeklyRun.Id, "fake", "would-exceed-budget", 0.01m, fixture.FullClock.UtcNow);
                return [];
            }

            var roots = new HashSet<string>(StringComparer.Ordinal);
            var parentIds = new List<string>();
            foreach (var memory in neighborhood)
            {
                if (memory.Depth == 0 && memory.SourceRef is not null && roots.Add(memory.SourceRef))
                {
                    parentIds.Add(memory.Id);
                }
            }

            return parentIds.Count >= 3 ? [new AbstractionProposal("shared pattern", parentIds)] : [];
        };
        fixture.Consolidation.Prioritize = (candidates, context) =>
        {
            var charge = fixture.Full.ReserveUsage(context.RunId, "fake", "priority", 5m, fixture.FullClock.UtcNow);
            fixture.Full.CompleteUsage(charge.Id, new ApiUsage(1, 0, 0, 5m), fixture.FullClock.UtcNow);
            var keys = new List<string>();
            foreach (var candidate in candidates)
            {
                keys.Add(candidate.WorkKey);
            }

            return keys;
        };
        BenchmarkSession[] sessions =
        [
            new("one", Start, "opaque 0: one"),
            new("two", Start.AddHours(1), "opaque 1: two"),
            new("three", Start.AddHours(2), "opaque 2: three")
        ];

        await fixture.Replay(sessions, Start.AddDays(7));

        var runs = fixture.Full.GetRuns();
        Assert.Equal(8, runs.Count);
        var meditation = Assert.Single(runs, run => run.Kind == RunKind.Meditation);
        Assert.Equal(5m, meditation.BudgetUsd);
        Assert.Equal(5m, fixture.Full.GetRunAccountedUsd(meditation.Id));
        Assert.Equal("budget_exhausted", meditation.Status);
        Assert.Equal(meditation.PeriodStart.AddDays(7), meditation.PeriodEnd);
        Assert.Equal(RunKind.Meditation, runs[7].Kind);
        Assert.Contains(fixture.Full.ReadSnapshot().Memories, memory => memory.Depth == 1);
    }

    [Fact]
    public void SharedImporterRejectsMismatchedDuplicateIdsAndRollsBackInvalidVectors()
    {
        using var fixture = new ReplayFixture();
        var source = fixture.Full.SaveSource("import test", Start);
        Assert.True(fixture.Full.ClaimSource(source.Source.Id));
        NewObservation[] valid =
        [
            new("first", "fake", ConsolidationFixture.Vector),
            new("second", "fake", ConsolidationFixture.Vector)
        ];
        Assert.Throws<InvariantException>(() => fixture.Full.CompleteSource(source.Source.Id, valid, Start, ["only-one"]));
        Assert.Throws<InvariantException>(() => fixture.Full.CompleteSource(source.Source.Id, valid, Start, ["same", "same"]));
        NewObservation[] invalid =
        [valid[0], new("second", "fake", new EmbeddingVector("test:3", [0, 0, 0]))];
        Assert.Throws<InvariantException>(() => fixture.Full.CompleteSource(source.Source.Id, invalid, Start, ["first", "second"]));
        Assert.Empty(fixture.Full.ReadSnapshot().Memories);
        fixture.Full.CompleteSource(source.Source.Id, valid, Start, ["first", "second"]);
        Assert.Equal(2, fixture.Full.GetSourceMemories(source.Source.Id).Count);
    }

    private sealed class ReplayFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "long-journey-replay-" + Guid.NewGuid().ToString("N"));
        private readonly EngineOptions baselineOptions;
        private MemoryEngine baselineEngine = null!;
        private MemoryScheduler scheduler = null!;
        public EngineOptions FullOptions { get; }
        public SqliteMemoryStore Baseline { get; private set; } = null!;
        public SqliteMemoryStore Full { get; private set; } = null!;
        public ExtractionCognition Extraction { get; } = new();
        public ConsolidationCognition Consolidation { get; } = new();
        public BenchmarkClock BaselineClock { get; } = new();
        public BenchmarkClock FullClock { get; } = new();

        public ReplayFixture()
        {
            baselineOptions = Options(Path.Combine(directory, "baseline"));
            FullOptions = Options(Path.Combine(directory, "full"));
            Restart();
        }

        public void Restart()
        {
            Baseline = new SqliteMemoryStore(baselineOptions);
            Full = new SqliteMemoryStore(FullOptions);
            baselineEngine = new MemoryEngine(Baseline, Extraction,
                new MemorySearch(Baseline, Extraction, baselineOptions), baselineOptions, BaselineClock);
            var consolidation = new ConsolidationEngine(Full, Consolidation,
                new MemorySearch(Full, Consolidation, FullOptions), FullOptions, FullClock);
            scheduler = new MemoryScheduler(Full, consolidation, FullOptions, FullClock);
        }

        public Task<IReadOnlyDictionary<string, string>> Replay(IReadOnlyList<BenchmarkSession> sessions, DateTimeOffset questionDate)
        {
            return BenchmarkReplay.ReplayAsync(sessions, questionDate, Baseline, Full, baselineEngine,
                scheduler, BaselineClock, FullClock, Extraction.EmbeddingSpace, CancellationToken.None);
        }

        private static EngineOptions Options(string path)
        {
            return new EngineOptions
            {
                DataDirectory = path,
                MaxRawCharacters = 100_000,
                MaxObservations = 3,
                TimeZoneId = "UTC",
                MeditationBudgetUsd = 5m
            };
        }

        public void Dispose()
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ExtractionCognition : ICognition
    {
        public string EmbeddingSpace => "test:3";
        public List<string> RawInputs { get; } = [];

        public Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
            string raw, CallContext context, CancellationToken cancellationToken)
        {
            RawInputs.Add(raw);
            IReadOnlyList<ObservationProposal> observations =
                [new($"observation {RawInputs.Count} first"), new($"observation {RawInputs.Count} second")];
            return Task.FromResult(new CognitiveResult<IReadOnlyList<ObservationProposal>>(observations, "fake"));
        }

        public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(new EmbeddingVector(EmbeddingSpace, [1, text.Length, 0.25f]));
        }

        public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(
            string query, string? context, IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(
            MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
            IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources,
            CognitionRole role, CallContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<CognitiveResult<IReadOnlyList<string>>> PrioritizeMeditationAsync(
            IReadOnlyList<MeditationPriorityCandidate> candidates, CallContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
