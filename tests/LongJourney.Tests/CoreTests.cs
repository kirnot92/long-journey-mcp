using Microsoft.Data.Sqlite;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RawIsBytePreservedAndDuplicateSurvivesRestart()
    {
        using var fixture = new Fixture();
        const string raw = "---\r\n한글 원문: $value\n  spaces stay  \r\n";
        var first = await fixture.Engine.RememberAsync(raw);
        Assert.Equal(raw, fixture.Store.ReadSource(first.SourceId).Raw);
        Assert.Equal(1, fixture.Cognition.ExtractCalls);
        var duplicate = await fixture.Engine.RememberAsync(raw);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.Memories[0].Id, duplicate.Memories[0].Id);
        var restarted = new SqliteMemoryStore(fixture.Options);
        var engine = new MemoryEngine(restarted, fixture.Cognition, new MemorySearch(restarted, fixture.Cognition, fixture.Options), fixture.Options, fixture.Clock);
        Assert.True((await engine.RememberAsync(raw)).Duplicate);
        Assert.Equal(1, fixture.Cognition.ExtractCalls);
        Assert.Single(restarted.ReadSnapshot().Memories);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Directory, "sources"), "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentIdenticalRequestsOnlyExtractOnce()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Cognition.BeforeExtract = async () => { entered.SetResult(); await release.Task; };
        var first = fixture.Engine.RememberAsync("one concurrent observation");
        await entered.Task;
        var second = await fixture.Engine.RememberAsync("one concurrent observation");
        Assert.True(second.Duplicate);
        Assert.Equal("processing", second.Status);
        release.SetResult();
        await first;
        var completedDuplicate = await fixture.Engine.RememberAsync("one concurrent observation");
        Assert.Equal("complete", completedDuplicate.Status);
        Assert.Single(completedDuplicate.Memories);
        Assert.Equal(1, fixture.Cognition.ExtractCalls);
        Assert.Single(fixture.Store.ReadSnapshot().Memories);
    }

    [Fact]
    public async Task FailedEmbeddingPreservesSourceAndRetryUsesIt()
    {
        using var fixture = new Fixture();
        fixture.Cognition.FailEmbedding = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.RememberAsync("retry me"));
        var failed = Assert.Single(fixture.Store.GetIncompleteSources());
        Assert.Equal("failed", failed.Status);
        Assert.Equal("retry me", fixture.Store.ReadSource(failed.Id).Raw);
        Assert.Empty(fixture.Store.ReadSnapshot().Memories);
        fixture.Cognition.FailEmbedding = false;
        var retry = await fixture.Engine.RememberAsync("retry me");
        Assert.Equal(failed.Id, retry.SourceId);
        Assert.Single(retry.Memories);
        Assert.Empty(fixture.Store.GetIncompleteSources());
    }

    [Fact]
    public async Task EmptyExtractionIsSuccessfulAndStillDeduplicated()
    {
        using var fixture = new Fixture();
        fixture.Cognition.ObservationCount = 0;
        var result = await fixture.Engine.RememberAsync("안녕");
        Assert.Empty(result.Memories);
        Assert.Equal("complete", fixture.Store.ReadSource(result.SourceId).Source.Status);
        Assert.True((await fixture.Engine.RememberAsync("안녕")).Duplicate);
        Assert.Equal(1, fixture.Cognition.ExtractCalls);
    }

    [Fact]
    public async Task OneFailedSourceDoesNotStarveOtherRecoveryWork()
    {
        using var fixture = new Fixture();
        var bad = fixture.Store.SaveSource("permanent refusal", Day);
        var good = fixture.Store.SaveSource("recoverable source", Day.AddMinutes(1));
        fixture.Cognition.FailRaw = "permanent refusal";
        var failures = await fixture.Engine.ResumePendingAsync();
        Assert.Equal(bad.Source.Id, Assert.Single(failures).SourceId);
        Assert.Equal("failed", fixture.Store.ReadSource(bad.Source.Id).Source.Status);
        Assert.Single(fixture.Store.GetSourceMemories(good.Source.Id));
    }

    [Fact]
    public async Task TooLargeInputIsRejectedBeforeArchiveOrLlm()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InputException>(() => fixture.Engine.RememberAsync(new string('x', fixture.Options.MaxRawCharacters + 1)));
        Assert.Equal(0, fixture.Cognition.ExtractCalls);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Directory, "sources"), "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MultipleObservationsOfSameSourceStillOnlySupportOneRoot()
    {
        using var fixture = new Fixture(maxObservations: 3);
        fixture.Cognition.ObservationCount = 3;
        var result = await fixture.Engine.RememberAsync("one original experience");
        Assert.Equal(3, result.Memories.Count);
        Assert.All(result.Memories, m => Assert.Equal(1, m.UniqueSourceRootCount));
        var run = fixture.Run(1);
        var ids = result.Memories.Select(m => m.Id).ToArray();
        var error = Assert.Throws<InvariantException>(() => fixture.Add(run, "a", ids));
        Assert.Contains("Source roots", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootUnionStrictLayeringAndGenerationBarrierAreEnforced()
    {
        using var fixture = new Fixture();
        var roots = await fixture.Roots(9);
        var run = fixture.Run(1);
        var firstGeneration = roots.Chunk(3).Select((group, i) => fixture.Add(run, "group" + i, group.Select(m => m.Id).ToArray())).ToArray();
        Assert.All(firstGeneration, m => Assert.Equal(3, m.UniqueSourceRootCount));
        var parentIds = firstGeneration.Select(m => m.Id).ToArray();
        Assert.Throws<InvariantException>(() => fixture.Add(run, "too soon", parentIds));
        var nextRun = fixture.Run(2);
        var secondGeneration = fixture.Add(nextRun, "next", parentIds);
        Assert.Equal(2, secondGeneration.Depth);
        Assert.Equal(9, secondGeneration.UniqueSourceRootCount);
        var trace = fixture.Engine.Trace(secondGeneration.Id);
        Assert.Equal(9, trace.Sources.Count);
        Assert.Equal(13, trace.Memories.Count);
        Assert.Equal(secondGeneration.Id, trace.Memories[0].Id);
        Assert.Throws<InvariantException>(() => fixture.Add(nextRun, "mixed", [roots[0].Id, roots[1].Id, firstGeneration[0].Id]));
        Assert.Throws<InvariantException>(() => fixture.Add(nextRun, "duplicates", [roots[0].Id, roots[0].Id, roots[1].Id]));
        Assert.Throws<InvariantException>(() => fixture.Add(nextRun, "too few", [roots[0].Id, roots[1].Id]));
    }

    [Fact]
    public async Task OverlappingParentsCannotManufactureRootSupport()
    {
        using var fixture = new Fixture();
        var ids = (await fixture.Roots(3)).Select(m => m.Id).ToArray();
        var run = fixture.Run(1);
        var parents = Enumerable.Range(0, 3).Select(i => fixture.Add(run, "overlap" + i, ids)).ToArray();
        Assert.Throws<InvariantException>(() => fixture.Add(fixture.Run(2), "fake depth", parents.Select(m => m.Id).ToArray()));
    }

    [Fact]
    public async Task SnapshotExcludesLaterIngestionRelationsAndRecalls()
    {
        using var fixture = new Fixture();
        var roots = await fixture.Roots(3);
        var run = fixture.Run(1);
        var future = (await fixture.Engine.RememberAsync("arrived during dream")).Memories[0];
        fixture.Store.RecordRecall([roots[0].Id], Day.AddHours(1));
        fixture.Store.AddRelation(new RelationProposal(roots[0].Id, roots[1].Id, RelationKind.Negative), run, Day.AddHours(1));
        var snapshot = fixture.Store.ReadSnapshot(run);
        Assert.Equal(3, snapshot.Memories.Count);
        Assert.Empty(snapshot.RecallEvents);
        Assert.All(snapshot.Memories, m => { Assert.Empty(m.Relations); Assert.Null(m.LastRecalledAt); });
        Assert.Throws<InvariantException>(() => fixture.Add(run, "new parent", [roots[0].Id, roots[1].Id, future.Id]));
    }

    [Fact]
    public async Task RelationsAreOutgoingOnlyAndRediscoveryDoesNotChangeTimestamp()
    {
        using var fixture = new Fixture();
        var roots = await fixture.Roots(3);
        var run = fixture.Run(1);
        var relation = new RelationProposal(roots[0].Id, roots[1].Id, RelationKind.Negative);
        fixture.Store.AddRelation(relation, run, Day.AddHours(1));
        fixture.Store.AddRelation(relation, run, Day.AddDays(3));
        var outgoing = Assert.Single(fixture.Store.GetMemory(roots[0].Id)!.Relations);
        Assert.Equal(Day.AddHours(1), outgoing.RelatedAt);
        Assert.Empty(fixture.Store.GetMemory(roots[1].Id)!.Relations);
        Assert.Equal(1, fixture.Store.GetMemory(roots[0].Id)!.UniqueSourceRootCount);
    }

    [Fact]
    public async Task ContentAndProvenanceCannotBeMutatedAfterBirth()
    {
        using var fixture = new Fixture();
        var roots = await fixture.Roots(4);
        var memory = fixture.Add(fixture.Run(1), "immutable", roots.Take(3).Select(m => m.Id).ToArray());
        using var db = new SqliteConnection($"Data Source={fixture.Store.DatabasePath};Foreign Keys=True;Pooling=False");
        db.Open();
        foreach (var sql in new[]
        {
            "UPDATE memories SET content='overwritten' WHERE id=$id",
            "DELETE FROM derived_from WHERE child_id=$id",
            "INSERT INTO derived_from(child_id,parent_id) VALUES($id,$parent)",
            "DELETE FROM memories WHERE id=$id"
        })
        {
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", memory.Id);
            command.Parameters.AddWithValue("$parent", roots[3].Id);
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }
        Assert.Equal(3, fixture.Store.GetMemory(memory.Id)!.DerivedFrom.Count);
    }

    [Fact]
    public async Task ReapplyingSavedProposalIsIdempotentButIndependentRunsMayRepeatIt()
    {
        using var fixture = new Fixture();
        var ids = (await fixture.Roots(3)).Select(m => m.Id).ToArray();
        var run = fixture.Run(1);
        var first = fixture.Add(run, "same work", ids);
        Assert.Equal(first.Id, fixture.Add(run, "same work", ids).Id);
        Assert.NotEqual(first.Id, fixture.Add(fixture.Run(2), "same work", ids).Id);
    }

    [Fact]
    public async Task RecallDoesNotReinforceSearchRootsOrEvidence()
    {
        using var fixture = new Fixture();
        await fixture.Roots(5);
        var before = await fixture.Search.SearchAsync("experience", new CallContext(), default);
        for (var i = 0; i < 3; i++) await fixture.Engine.RecallAsync("experience");
        var after = await fixture.Search.SearchAsync("experience", new CallContext(), default);
        Assert.Equal(before.Select(m => m.Id), after.Select(m => m.Id));
        Assert.All(fixture.Store.ReadSnapshot().Memories, m => { Assert.Equal(0, m.Depth); Assert.Equal(1, m.UniqueSourceRootCount); Assert.Empty(m.Relations); });
        Assert.NotEmpty(fixture.Store.ReadSnapshot().RecallEvents);
    }

    [Fact]
    public async Task RecallRejectsHallucinatedIdsAndFtsEscapesQuerySyntax()
    {
        using var fixture = new Fixture();
        await fixture.Roots(3);
        fixture.Cognition.BadRecallId = true;
        await Assert.ThrowsAsync<InvariantException>(() => fixture.Engine.RecallAsync("experience"));
        Assert.Empty(fixture.Store.ReadSnapshot().RecallEvents);
        _ = fixture.Store.LexicalSearch("\" OR NEAR( [x] : *", 10);
    }

    [Fact]
    public async Task NewEmbeddingSpaceReindexesWithoutChangingMemory()
    {
        using var fixture = new Fixture();
        var roots = await fixture.Roots(2);
        fixture.Cognition.EmbeddingSpace = "new-test:3";
        await fixture.Search.SearchAsync("experience", new CallContext(), default);
        Assert.Equal(2, fixture.Store.GetEmbeddings("new-test:3").Count);
        Assert.Equal(2, fixture.Store.GetEmbeddings("test:3").Count);
        Assert.Equal(roots.Select(m => m.Id), fixture.Store.ReadSnapshot().Memories.Select(m => m.Id));
    }

    [Fact]
    public void UnknownUsageRemainsReservedAfterRestartAndBudgetIsAtomic()
    {
        using var fixture = new Fixture();
        var run = fixture.Store.GetOrCreateRun(RunKind.Meditation, Day, Day.AddDays(7), Day.AddDays(7), 1m);
        var first = fixture.Store.ReserveUsage(run.Id, "model", "reason", .7m, Day);
        Assert.Throws<BudgetExceededException>(() => fixture.Store.ReserveUsage(run.Id, "model", "reason", .4m, Day));
        var restarted = new SqliteMemoryStore(fixture.Options);
        Assert.Equal(.7m, restarted.GetRunAccountedUsd(run.Id));
        restarted.CompleteUsage(first.Id, new ApiUsage(10, 0, 10, .2m), Day);
        restarted.CompleteUsage(first.Id, new ApiUsage(10, 0, 10, 0m), Day);
        Assert.Equal(.2m, restarted.GetRunAccountedUsd(run.Id));
        restarted.ReserveUsage(run.Id, "model", "reason", .8m, Day);
        Assert.Equal(1m, restarted.GetRunAccountedUsd(run.Id));
    }

    [Fact]
    public async Task OrphanSourceFileIsRecoveredAfterMissingDatabase()
    {
        using var fixture = new Fixture();
        var source = fixture.Store.SaveSource("file committed before DB crash", Day);
        // Simulates the file-first crash window using a separate corpus containing only that artifact.
        var recoveredDirectory = Path.Combine(fixture.Directory, "recovery");
        var target = Path.Combine(recoveredDirectory, source.Source.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(fixture.Directory, source.Source.RelativePath), target);
        var options = new EngineOptions { DataDirectory = recoveredDirectory };
        var recovered = new SqliteMemoryStore(options);
        Assert.Equal(source.Source.Id, Assert.Single(recovered.GetIncompleteSources()).Id);
        var engine = new MemoryEngine(recovered, fixture.Cognition, new MemorySearch(recovered, fixture.Cognition, options), options, fixture.Clock);
        await engine.ResumePendingAsync();
        Assert.Single(recovered.GetSourceMemories(source.Source.Id));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "longjourney-core-tests", Guid.NewGuid().ToString("N"));
        public EngineOptions Options { get; }
        public SqliteMemoryStore Store { get; }
        public TestCognition Cognition { get; } = new();
        public FixedClock Clock { get; } = new(Day);
        public MemorySearch Search { get; }
        public MemoryEngine Engine { get; }
        public Fixture(int maxObservations = 1)
        {
            Options = new EngineOptions { DataDirectory = Directory, MaxObservations = maxObservations };
            Store = new SqliteMemoryStore(Options);
            Search = new MemorySearch(Store, Cognition, Options);
            Engine = new MemoryEngine(Store, Cognition, Search, Options, Clock);
        }
        public async Task<MemoryRecord[]> Roots(int count)
        {
            var result = new List<MemoryRecord>();
            for (var i = 0; i < count; i++) result.AddRange((await Engine.RememberAsync("experience " + i)).Memories);
            return result.ToArray();
        }
        public RunRecord Run(int n) => Store.GetOrCreateRun(RunKind.Dream, Day.AddDays(n - 1), Day.AddDays(n), Day.AddDays(n), null);
        public MemoryRecord Add(RunRecord run, string key, string[] parents) => Store.AddAbstraction(new AbstractionProposal("provisional " + key, parents), "test-model", run, key, 0, parents, new EmbeddingVector("test:3", [1, 1, 1]), Day.AddDays(run.Id));
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class TestCognition : ICognition
    {
        public string EmbeddingSpace { get; set; } = "test:3";
        public int ExtractCalls;
        public int ObservationCount = 1;
        public bool FailEmbedding;
        public bool BadRecallId;
        public string? FailRaw;
        public Func<Task>? BeforeExtract;
        public async Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(string raw, CallContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ExtractCalls);
            if (raw == FailRaw) throw new InvalidDataException("Injected source refusal");
            if (BeforeExtract is not null) await BeforeExtract();
            return new(Enumerable.Range(0, ObservationCount).Select(i => new ObservationProposal(raw + " observation " + i)).ToArray(), "test-model");
        }
        public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken) => FailEmbedding
            ? Task.FromException<EmbeddingVector>(new IOException("Injected embedding failure"))
            : Task.FromResult(new EmbeddingVector(EmbeddingSpace, [1, text.Length % 5 + 1, 1]));
        public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context, IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken)
            => Task.FromResult(new CognitiveResult<IReadOnlyList<string>>(BadRecallId ? ["hallucinated"] : candidates.Take(2).Select(m => m.Id).ToArray(), "test-model"));
        public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken)
            => Task.FromResult(new CognitiveResult<IReadOnlyList<RelationProposal>>([], "test-model"));
        public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources, CognitionRole role, CallContext context, CancellationToken cancellationToken)
            => Task.FromResult(new CognitiveResult<IReadOnlyList<AbstractionProposal>>([], "test-model"));
    }
}
