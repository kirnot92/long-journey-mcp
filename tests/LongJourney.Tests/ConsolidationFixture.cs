using LongJourney.Core;

namespace LongJourney.Tests;

internal sealed class ConsolidationFixture : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "long-journey-consolidation-tests", Guid.NewGuid().ToString("N"));
    private int setupSequence;
    public static EmbeddingVector Vector => new("test:3", [1, 0.5f, 0.25f]);
    public EngineOptions Options { get; }
    public SqliteMemoryStore Store { get; }
    public ConsolidationClock Clock { get; } = new() { Now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero) };
    public ConsolidationCognition Cognition { get; } = new();
    public ConsolidationSearch Search { get; } = new();
    public ConsolidationEngine Engine { get; }
    public ConsolidationFixture()
    {
        Options = new EngineOptions
        {
            DataDirectory = directory,
            TimeZoneId = "UTC",
            MeditationBudgetUsd = 1m
        };
        Store = new SqliteMemoryStore(Options);
        Engine = new ConsolidationEngine(Store, Cognition, Search, Options, Clock);
    }

    public MemoryRecord[] Observations(int count, DateTimeOffset at)
    {
        var observations = new MemoryRecord[count];
        for (var index = 0; index < count; index++)
        {
            setupSequence++;
            var raw = $"Observation {setupSequence}";
            var source = Store.SaveSource(raw, at);
            Assert.True(Store.ClaimSource(source.Source.Id));
            Store.CompleteSource(source.Source.Id, [new NewObservation(raw, "fake", Vector)], at);
            observations[index] = Assert.Single(Store.GetSourceMemories(source.Source.Id));
        }

        return observations;
    }

    public MemoryRecord Abstraction(IReadOnlyList<MemoryRecord> parents, DateTimeOffset at)
    {
        var run = StartSetupRun(at);
        var parentIds = MemoryTestData.Ids(parents);
        var proposal = new AbstractionProposal($"Possible pattern {setupSequence}", parentIds);
        var workKey = $"setup:{setupSequence}";
        var abstraction = Store.AddAbstraction(
            proposal, "fake", run, workKey, 0, parentIds, Vector, at);
        Store.FinishRun(run.Id, "complete", at);
        return abstraction;
    }

    public void Relate(MemoryRecord owner, MemoryRecord evidence, RelationKind kind, DateTimeOffset at)
    {
        var run = StartSetupRun(at);
        Store.AddRelation(new RelationProposal(owner.Id, evidence.Id, kind), run, at);
        Store.FinishRun(run.Id, "complete", at);
    }

    private RunRecord StartSetupRun(DateTimeOffset at)
    {
        // Fixture mutations use distinct completed runs outside the scheduler periods under test.
        setupSequence++;
        var period = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(setupSequence);
        return Store.GetOrCreateRun(RunKind.Dream, period, period.AddSeconds(1), at, null);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal sealed class ConsolidationClock : TimeProvider
{
    public DateTimeOffset Now
    {
        get; set;
    }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class ConsolidationSearch : IMemorySearch
{
    public Func<MemoryRecord, GraphSnapshot, IReadOnlyList<MemoryRecord>>? Nearest { get; set; }

    public Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string query, CallContext context, CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null, int? depth = null, int? limit = null)
    {
        var candidates = SelectCandidates(snapshot?.Memories ?? [], depth, limit ?? 30);
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(candidates);
    }

    public Task<IReadOnlyList<MemoryRecord>> NearestAsync(
        MemoryRecord seed, GraphSnapshot snapshot, CallContext context,
        CancellationToken cancellationToken, int? depth = null, int? limit = null)
    {
        var candidates = Nearest is null
            ? SelectCandidates(snapshot.Memories, depth, limit ?? 30, seed.Id)
            : Nearest(seed, snapshot);
        return Task.FromResult<IReadOnlyList<MemoryRecord>>(candidates);
    }

    private static IReadOnlyList<MemoryRecord> SelectCandidates(
        IReadOnlyList<MemoryRecord> memories, int? depth, int limit, string? excludedId = null)
    {
        var candidates = new List<MemoryRecord>();
        foreach (var memory in memories)
        {
            if (candidates.Count >= limit)
            {
                break;
            }

            if (memory.Id == excludedId || (depth is not null && memory.Depth != depth))
            {
                continue;
            }

            candidates.Add(memory);
        }

        return candidates;
    }
}

internal sealed class ConsolidationCognition : ICognition
{
    public string EmbeddingSpace => "test:3";
    public Func<MemoryRecord, IReadOnlyList<MemoryRecord>, IReadOnlyList<RelationProposal>> Assimilate { get; set; } = (_, _) => [];
    public Func<IReadOnlyList<MemoryRecord>, IReadOnlyList<SourceArtifact>, CognitionRole, IReadOnlyList<AbstractionProposal>> Abstract { get; set; } = (_, _, _) => [];
    public Func<string, CallContext, EmbeddingVector> Embedding { get; set; } = (_, _) => ConsolidationFixture.Vector;
    public Func<IReadOnlyList<MeditationPriorityCandidate>, CallContext, IReadOnlyList<string>>? Prioritize { get; set; }
    public List<IReadOnlyList<MeditationPriorityCandidate>> PriorityBatches { get; } = [];
    public List<string> Calls { get; } = [];
    public List<CallContext> Contexts { get; } = [];
    public List<IReadOnlyList<MemoryRecord>> Neighborhoods { get; } = [];
    public List<IReadOnlyList<SourceArtifact>> SourceBatches { get; } = [];
    public List<CognitionRole> Roles { get; } = [];
    public int EmbeddingCalls
    {
        get; private set;
    }

    public Task<CognitiveResult<IReadOnlyList<string>>> PrioritizeMeditationAsync(
        IReadOnlyList<MeditationPriorityCandidate> candidates,
        CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("priority");
        Contexts.Add(context);
        PriorityBatches.Add(candidates);
        IReadOnlyList<string> orderedKeys;
        if (Prioritize is not null)
        {
            orderedKeys = Prioritize(candidates, context);
        }
        else
        {
            var keys = new List<string>();
            foreach (var candidate in candidates)
            {
                keys.Add(candidate.WorkKey);
            }
            orderedKeys = keys;
        }

        return Task.FromResult(new CognitiveResult<IReadOnlyList<string>>(orderedKeys, "fake"));
    }

    public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EmbeddingCalls++;
        Contexts.Add(context);
        return Task.FromResult(Embedding(text, context));
    }
    public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(
        MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates,
        CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("assimilation");
        Contexts.Add(context);
        var proposals = Assimilate(observation, candidates);
        var result = new CognitiveResult<IReadOnlyList<RelationProposal>>(proposals, "fake");
        return Task.FromResult(result);
    }
    public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
        IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources,
        CognitionRole role, CallContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("abstraction");
        Contexts.Add(context);
        Neighborhoods.Add(neighborhood);
        SourceBatches.Add(sources);
        Roles.Add(role);
        var proposals = Abstract(neighborhood, sources, role);
        var result = new CognitiveResult<IReadOnlyList<AbstractionProposal>>(proposals, "fake");
        return Task.FromResult(result);
    }
    public Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
        string raw, CallContext context, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(
        string query, string? context, IReadOnlyList<MemoryRecord> candidates,
        CallContext call, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}
