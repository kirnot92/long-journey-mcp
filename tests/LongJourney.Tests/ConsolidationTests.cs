using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class ConsolidationTests
{
    [Fact]
    public async Task DreamSeedsCreatedObservationsAndAllDepthRecallsAndFreezesRelationsAndGeneration()
    {
        using var fixture = new ConsolidationFixture();
        var today = fixture.Clock.Now.Date;
        var start = new DateTimeOffset(today, TimeSpan.Zero);
        var old = fixture.Observations(3, start.AddDays(-10));
        var recalled = fixture.Abstraction(old, start.AddDays(-8));
        var fresh = fixture.Observations(3, start.AddHours(1));
        var createdAbstraction = fixture.Abstraction(fresh, start.AddHours(2));
        fixture.Store.RecordRecall([recalled.Id], start.AddHours(3));
        fixture.Cognition.Assimilate = (observation, _) => [new RelationProposal(recalled.Id, observation.Id, RelationKind.Negative)];
        fixture.Cognition.Abstract = (neighborhood, _, _) =>
        {
            var parents = neighborhood.Where(x => x.Depth == 0).Take(3).Select(x => x.Id).ToArray();
            return parents.Length == 3 ? [new AbstractionProposal("A provisional shared pattern.", parents)] : [];
        };

        var summary = await fixture.Engine.DreamAsync(start, start.AddDays(1));
        var run = fixture.Store.GetRuns().Single(x => x.Id == summary.RunId);
        var work = fixture.Store.GetWorkItems(run.Id);
        Assert.Equal("complete", summary.Status);
        Assert.Equal(fresh.Select(x => x.Id).Order(), work.Where(x => x.Phase == "assimilation").Select(x => x.MemoryId).Order());
        Assert.Equal(fresh.Select(x => x.Id).Append(recalled.Id).Order(), work.Where(x => x.Phase == "consolidation").Select(x => x.MemoryId).Order());
        Assert.DoesNotContain(work, x => x.MemoryId == createdAbstraction.Id);
        Assert.Equal(["assimilation", "assimilation", "assimilation"], fixture.Cognition.Calls.Take(3));
        Assert.All(fixture.Cognition.Neighborhoods.SelectMany(x => x), x => Assert.True(x.Sequence <= run.MemoryHighWater));
        Assert.All(fixture.Cognition.Neighborhoods.SelectMany(x => x).Where(x => x.Id == recalled.Id), x => Assert.Empty(x.Relations));
        Assert.Equal(3, fixture.Store.GetMemory(recalled.Id)!.Relations.Count);
        Assert.All(fresh, x => Assert.Empty(fixture.Store.GetMemory(x.Id)!.Relations));
        Assert.All(fixture.Cognition.Contexts, x => Assert.Equal(run.Id, x.RunId));
    }

    [Fact]
    public async Task InvalidParentsAndReversedAssimilationAreRejectedBeforeEmbedding()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        fixture.Cognition.Assimilate = (observation, candidates) =>
            [new RelationProposal(observation.Id, candidates[0].Id, RelationKind.Positive)];
        fixture.Cognition.Abstract = (_, _, _) => [new AbstractionProposal("Invented evidence", [observations[0].Id, observations[1].Id, "unknown"])];

        var result = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        Assert.Equal(6, result.RejectedProposals);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
        Assert.All(fixture.Store.ReadSnapshot().Memories, x => Assert.Empty(x.Relations));
        Assert.Equal(3, fixture.Store.ReadSnapshot().Memories.Count);
    }

    [Fact]
    public async Task DirectEvidenceSurvivesFullSemanticPageAndMixedDepthProposalIsRejected()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var old = fixture.Observations(25, start.AddDays(-10));
        var higher = fixture.Abstraction(old.Take(3).ToArray(), start.AddDays(-8));
        var seed = fixture.Observations(1, start.AddHours(1))[0];
        fixture.Relate(seed, higher, RelationKind.Negative, start.AddHours(2));
        fixture.Cognition.Abstract = (_, _, _) => [new AbstractionProposal("Mixed depth is invalid", [seed.Id, higher.Id, old[0].Id])];

        var summary = await fixture.Engine.DreamAsync(start, start.AddDays(1));

        var neighborhood = Assert.Single(fixture.Cognition.Neighborhoods);
        Assert.Contains(neighborhood, x => x.Id == higher.Id);
        Assert.True(neighborhood.Count(x => x.Depth == 0) >= fixture.Options.RootBase);
        Assert.Equal(fixture.Options.NeighborhoodSize, neighborhood.Count);
        Assert.Equal(1, summary.RejectedProposals);
        Assert.Equal(0, fixture.Cognition.EmbeddingCalls);
    }

    [Fact]
    public async Task SavedProposalsResumeAfterPartialApplicationWithoutRepeatingReasoningOrMemories()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-1);
        var observations = fixture.Observations(3, start.AddHours(1));
        var parents = observations.Select(x => x.Id).ToArray();
        var abstractCalls = 0;
        fixture.Cognition.Abstract = (_, _, _) => ++abstractCalls == 1
            ? [new AbstractionProposal("First possible pattern", parents), new AbstractionProposal("Second possible pattern", parents)] : [];
        var embeds = 0;
        fixture.Cognition.Embedding = (_, _) => ++embeds == 2 ? throw new IOException("simulated crash") : ConsolidationFixture.Vector;

        await Assert.ThrowsAsync<IOException>(() => fixture.Engine.DreamAsync(start, start.AddDays(1)));
        var interrupted = fixture.Store.GetRuns().Single();
        Assert.Equal("running", interrupted.Status);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, x => x.Depth == 1);
        Assert.Contains(fixture.Store.GetWorkItems(interrupted.Id), x => x.Status == "pending" && x.ProposalJson is not null);

        fixture.Cognition.Embedding = (_, _) => ConsolidationFixture.Vector;
        var resumed = await fixture.Engine.DreamAsync(start, start.AddDays(1));
        Assert.Equal("complete", resumed.Status);
        Assert.Equal(3, abstractCalls);
        Assert.Equal(2, fixture.Store.ReadSnapshot().Memories.Count(x => x.Depth == 1));
        var callsBeforeTerminalRetry = fixture.Cognition.Calls.Count;
        await fixture.Engine.DreamAsync(start, start.AddDays(1));
        Assert.Equal(callsBeforeTerminalRetry, fixture.Cognition.Calls.Count);
    }

    [Fact]
    public async Task WeeklyChangesIncludeOldOwnerOfNewRelationToObservationAndPrioritizeNegativeChanges()
    {
        using var fixture = new ConsolidationFixture();
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var first = fixture.Abstraction(roots.Take(3).ToArray(), start.AddDays(-8));
        var second = fixture.Abstraction(roots.Skip(3).Take(3).ToArray(), start.AddDays(-8));
        var recent = fixture.Abstraction(roots.Skip(6).ToArray(), start.AddDays(1));
        var evidence = fixture.Observations(1, start.AddDays(2))[0];
        fixture.Relate(first, evidence, RelationKind.Negative, start.AddDays(3));
        fixture.Relate(second, evidence, RelationKind.Positive, start.AddDays(4));

        var summary = await fixture.Engine.MeditateAsync(start, start.AddDays(7));

        var work = fixture.Store.GetWorkItems(summary.RunId);
        Assert.Equal(new[] { first.Id, second.Id, recent.Id }.Order(), work.Select(x => x.MemoryId).Order());
        Assert.Equal(first.Id, work.OrderBy(x => x.Ordinal).First().MemoryId);
        Assert.DoesNotContain(work, x => x.MemoryId == evidence.Id);
        Assert.All(fixture.Cognition.Roles, x => Assert.Equal(CognitionRole.Meditation, x));
        Assert.All(fixture.Cognition.SourceBatches, x => Assert.NotEmpty(x));
        Assert.All(fixture.Cognition.Contexts, x => Assert.Equal(summary.RunId, x.RunId));
    }

    [Fact]
    public async Task WeeklyBudgetCarriesPartialProposalWithoutPayingForAlreadyAppliedOutputAgain()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MeditationBudgetUsd = 0.3m;
        var start = fixture.Clock.Now.AddDays(-7);
        var roots = fixture.Observations(9, start.AddDays(-10));
        var parents = Enumerable.Range(0, 3).Select(index => fixture.Abstraction(roots.Skip(index * 3).Take(3).ToArray(), start.AddDays(1))).ToArray();
        var parentIds = parents.Select(x => x.Id).ToArray();
        var abstractCalls = 0;
        fixture.Cognition.Abstract = (_, _, _) => ++abstractCalls == 1
            ? [new AbstractionProposal("Possible higher pattern one", parentIds), new AbstractionProposal("Possible higher pattern two", parentIds)] : [];
        fixture.Cognition.Embedding = (_, context) =>
        {
            var reservation = fixture.Store.ReserveUsage(context.RunId, "fake", "embedding", 0.2m, fixture.Clock.Now);
            fixture.Store.CompleteUsage(reservation.Id, new ApiUsage(1, 0, 0, 0.2m), fixture.Clock.Now);
            return ConsolidationFixture.Vector;
        };

        var exhausted = await fixture.Engine.MeditateAsync(start, start.AddDays(7));
        Assert.Equal("budget_exhausted", exhausted.Status);
        Assert.Equal(0.2m, exhausted.AccountedUsd);
        Assert.Single(fixture.Store.ReadSnapshot().Memories, x => x.Depth == 2);
        var originWork = fixture.Store.GetWorkItems(exhausted.RunId).First(x => x.ProposalJson is not null);
        Assert.Equal("pending", originWork.Status);

        fixture.Clock.Now = fixture.Clock.Now.AddDays(7);
        var next = await fixture.Engine.MeditateAsync(start.AddDays(7), start.AddDays(14));
        Assert.Equal("complete", next.Status);
        Assert.Equal(0.2m, next.AccountedUsd);
        Assert.Equal(2, fixture.Store.ReadSnapshot().Memories.Count(x => x.Depth == 2));
        Assert.Equal("complete", fixture.Store.GetWorkItems(exhausted.RunId).Single(x => x.Key == originWork.Key).Status);
        Assert.Contains(fixture.Store.GetWorkItems(next.RunId), x => x.Key.StartsWith("carry:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnconfiguredWeeklyBudgetDoesNotCreateRun()
    {
        using var fixture = new ConsolidationFixture();
        fixture.Options.MeditationBudgetUsd = null;
        await Assert.ThrowsAsync<InputException>(() => fixture.Engine.MeditateAsync(fixture.Clock.Now.AddDays(-7), fixture.Clock.Now));
        Assert.Empty(fixture.Store.GetRuns());
    }
}

internal sealed class ConsolidationFixture : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "long-journey-consolidation-tests", Guid.NewGuid().ToString("N"));
    private int serial;
    public static EmbeddingVector Vector => new("test:3", [1, 0.5f, 0.25f]);
    public EngineOptions Options { get; }
    public SqliteMemoryStore Store { get; }
    public ConsolidationClock Clock { get; } = new() { Now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero) };
    public ConsolidationCognition Cognition { get; } = new();
    public ConsolidationEngine Engine { get; }
    public ConsolidationFixture()
    {
        Options = new EngineOptions { DataDirectory = directory, TimeZoneId = "UTC", MeditationBudgetUsd = 1m };
        Store = new SqliteMemoryStore(Options);
        Engine = new ConsolidationEngine(Store, Cognition, new ConsolidationSearch(), Options, Clock);
    }

    public MemoryRecord[] Observations(int count, DateTimeOffset at) => Enumerable.Range(0, count).Select(_ =>
    {
        var raw = $"Observation {++serial}";
        var source = Store.SaveSource(raw, at);
        Assert.True(Store.ClaimSource(source.Source.Id));
        Store.CompleteSource(source.Source.Id, [new NewObservation(raw, "fake", Vector)], at);
        return Store.GetSourceMemories(source.Source.Id).Single();
    }).ToArray();

    public MemoryRecord Abstraction(IReadOnlyList<MemoryRecord> parents, DateTimeOffset at)
    {
        var run = SetupRun(at);
        var ids = parents.Select(x => x.Id).ToArray();
        var result = Store.AddAbstraction(new AbstractionProposal($"Possible pattern {serial}", ids), "fake", run, $"setup:{serial}", 0, ids, Vector, at);
        Store.FinishRun(run.Id, "complete", at);
        return result;
    }

    public void Relate(MemoryRecord owner, MemoryRecord evidence, RelationKind kind, DateTimeOffset at)
    {
        var run = SetupRun(at);
        Store.AddRelation(new RelationProposal(owner.Id, evidence.Id, kind), run, at);
        Store.FinishRun(run.Id, "complete", at);
    }

    private RunRecord SetupRun(DateTimeOffset at)
    {
        var period = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++serial);
        return Store.GetOrCreateRun(RunKind.Dream, period, period.AddSeconds(1), at, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

internal sealed class ConsolidationClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class ConsolidationSearch : IMemorySearch
{
    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, CallContext context, CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null, int? depth = null, int? limit = null)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>((snapshot?.Memories ?? []).Where(x => depth is null || x.Depth == depth).Take(limit ?? 30).ToArray());

    public Task<IReadOnlyList<MemoryRecord>> NearestAsync(MemoryRecord seed, GraphSnapshot snapshot, CallContext context,
        CancellationToken cancellationToken, int? depth = null, int? limit = null)
        => Task.FromResult<IReadOnlyList<MemoryRecord>>(snapshot.Memories.Where(x => x.Id != seed.Id && (depth is null || x.Depth == depth)).Take(limit ?? 30).ToArray());
}

internal sealed class ConsolidationCognition : ICognition
{
    public string EmbeddingSpace => "test:3";
    public Func<MemoryRecord, IReadOnlyList<MemoryRecord>, IReadOnlyList<RelationProposal>> Assimilate { get; set; } = (_, _) => [];
    public Func<IReadOnlyList<MemoryRecord>, IReadOnlyList<SourceArtifact>, CognitionRole, IReadOnlyList<AbstractionProposal>> Abstract { get; set; } = (_, _, _) => [];
    public Func<string, CallContext, EmbeddingVector> Embedding { get; set; } = (_, _) => ConsolidationFixture.Vector;
    public List<string> Calls { get; } = [];
    public List<CallContext> Contexts { get; } = [];
    public List<IReadOnlyList<MemoryRecord>> Neighborhoods { get; } = [];
    public List<IReadOnlyList<SourceArtifact>> SourceBatches { get; } = [];
    public List<CognitionRole> Roles { get; } = [];
    public int EmbeddingCalls { get; private set; }

    public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EmbeddingCalls++;
        Contexts.Add(context);
        return Task.FromResult(Embedding(text, context));
    }
    public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("assimilation");
        Contexts.Add(context);
        return Task.FromResult(new CognitiveResult<IReadOnlyList<RelationProposal>>(Assimilate(observation, candidates), "fake"));
    }
    public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources, CognitionRole role, CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("abstraction");
        Contexts.Add(context);
        Neighborhoods.Add(neighborhood);
        SourceBatches.Add(sources);
        Roles.Add(role);
        return Task.FromResult(new CognitiveResult<IReadOnlyList<AbstractionProposal>>(Abstract(neighborhood, sources, role), "fake"));
    }
    public Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(string raw, CallContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context, IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken) => throw new NotSupportedException();
}
