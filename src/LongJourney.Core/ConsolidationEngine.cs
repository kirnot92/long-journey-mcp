using System.Globalization;
using System.Text.Json;

namespace LongJourney.Core;

/// <summary>Runs durable consolidation jobs over a graph frozen at the start of each run.</summary>
public sealed class ConsolidationEngine(
    IMemoryStore store, ICognition cognition, IMemorySearch search,
    EngineOptions options, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<RunSummary> DreamAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
        => ExecuteAsync(RunKind.Dream, start, end, cancellationToken);

    public Task<RunSummary> MeditateAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
        => ExecuteAsync(RunKind.Meditation, start, end, cancellationToken);

    private async Task<RunSummary> ExecuteAsync(RunKind kind, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        if (start >= end) throw new InputException("A consolidation period must have start before end.");
        if (kind == RunKind.Meditation && options.MeditationBudgetUsd is null)
            throw new InputException("Weekly Meditation is deferred until MeditationBudgetUsd is configured.");

        await gate.WaitAsync(ct);
        try
        {
            var run = store.GetOrCreateRun(kind, start, end, timeProvider.GetUtcNow(),
                kind == RunKind.Meditation ? options.MeditationBudgetUsd : null);
            if (IsTerminal(run.Status)) return Summary(run.Id, run.Status);
            var snapshot = store.ReadSnapshot(run);
            if (kind == RunKind.Dream) SeedDream(run, snapshot);
            else SeedMeditation(run, snapshot);

            try
            {
                foreach (var item in store.GetWorkItems(run.Id).OrderBy(x => x.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Status == "complete") continue;
                    if (TryGetCarryOrigin(item, out var originRunId, out var originKey))
                    {
                        var originRun = store.GetRuns().Single(x => x.Id == originRunId);
                        var original = store.GetWorkItems(originRunId).Single(x => x.Key == originKey);
                        if (original.Status != "complete")
                            await ProcessItemAsync(originRun, original, store.ReadSnapshot(originRun), new CallContext(run.Id), ct);
                        store.CompleteWork(run.Id, item.Key);
                    }
                    else await ProcessItemAsync(run, item, snapshot, new CallContext(run.Id), ct);
                }
                store.FinishRun(run.Id, "complete", timeProvider.GetUtcNow());
                return Summary(run.Id, "complete");
            }
            catch (BudgetExceededException) when (kind == RunKind.Meditation)
            {
                // Pending original work remains discoverable by a later weekly run.
                store.FinishRun(run.Id, "budget_exhausted", timeProvider.GetUtcNow());
                return Summary(run.Id, "budget_exhausted");
            }
        }
        finally { gate.Release(); }
    }

    private void SeedDream(RunRecord run, GraphSnapshot snapshot)
    {
        var created = snapshot.Memories.Where(x => x.Depth == 0 && InPeriod(x.CreatedAt, run))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var recalledIds = snapshot.RecallEvents.Where(x => InPeriod(x.RecalledAt, run))
            .Select(x => x.MemoryId).ToHashSet(StringComparer.Ordinal);
        var seeds = created.Concat(snapshot.Memories.Where(x => recalledIds.Contains(x.Id)))
            .DistinctBy(x => x.Id).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var ordinal = 0;
        var work = created.Select(x => new WorkSeed($"assimilate:{x.Id}", "assimilation", x.Id, ordinal++)).ToList();
        work.AddRange(seeds.Select(x => new WorkSeed($"abstract:{x.Id}", "consolidation", x.Id, ordinal++)));
        store.EnsureWorkItems(run.Id, work);
    }

    private void SeedMeditation(RunRecord run, GraphSnapshot snapshot)
    {
        var candidates = snapshot.Memories.Where(x => x.Depth >= 1 &&
            (InPeriod(x.CreatedAt, run) || x.Relations.Any(r => InPeriod(r.RelatedAt, run))))
            .Select(x => (Seed: new WorkSeed($"abstract:{x.Id}", "consolidation", x.Id, 0), Memory: x, Period: run)).ToList();

        foreach (var previous in store.GetRuns().Where(x => x.Kind == RunKind.Meditation &&
                     x.Id < run.Id && x.Status == "budget_exhausted"))
        {
            var originalGraph = store.ReadSnapshot(previous).ById;
            foreach (var pending in store.GetWorkItems(previous.Id).Where(x => x.Status != "complete" && !x.Key.StartsWith("carry:", StringComparison.Ordinal)))
                if (originalGraph.TryGetValue(pending.MemoryId, out var memory))
                    candidates.Add((new WorkSeed($"carry:{previous.Id}:{pending.Key}", "carry", pending.MemoryId, 0), memory, previous));
        }

        // Priority is an ephemeral ordering of changed regions, never a stored importance/truth score.
        var ordered = candidates
            .OrderByDescending(x => x.Memory.Relations.Count(r => r.Kind == RelationKind.Negative && InPeriod(r.RelatedAt, x.Period)))
            .ThenByDescending(x => x.Memory.Relations.Count(r => r.Kind == RelationKind.Negative))
            .ThenByDescending(x => x.Memory.Relations.Select(r => r.RelatedAt).Append(x.Memory.CreatedAt).Max())
            .ThenBy(x => x.Seed.Key, StringComparer.Ordinal)
            .Select((x, index) => x.Seed with { Ordinal = index }).ToArray();
        store.EnsureWorkItems(run.Id, ordered);
    }

    private async Task ProcessItemAsync(RunRecord run, RunWorkItem item, GraphSnapshot snapshot, CallContext context, CancellationToken ct)
    {
        var byId = snapshot.ById;
        if (!byId.TryGetValue(item.MemoryId, out var seed))
            throw new InvariantException($"Work seed {item.MemoryId} is absent from its frozen run snapshot.");
        SavedProposal envelope;
        var model = item.Model;
        if (item.ProposalJson is not null)
        {
            envelope = JsonSerializer.Deserialize<SavedProposal>(item.ProposalJson, JsonDefaults.Options)
                ?? throw new InvariantException("Stored consolidation proposal is invalid.");
        }
        else if (item.Phase == "assimilation")
        {
            var candidates = await AssimilationCandidatesAsync(seed, snapshot, context, ct);
            var result = await cognition.AssimilateAsync(seed, candidates, context, ct);
            envelope = new SavedProposal(candidates.Select(x => x.Id).ToArray(), result.Value.ToArray(), []);
            model = result.Model;
            store.SaveWorkProposal(run.Id, item.Key, JsonSerializer.Serialize(envelope, JsonDefaults.Options), model);
        }
        else
        {
            var neighborhood = await NeighborhoodAsync(seed, snapshot, run.Kind, context, ct);
            var sources = run.Kind == RunKind.Meditation ? ReadSources(neighborhood, byId) : [];
            var result = await cognition.AbstractAsync(neighborhood, sources,
                run.Kind == RunKind.Dream ? CognitionRole.Dream : CognitionRole.Meditation, context, ct);
            envelope = new SavedProposal(neighborhood.Select(x => x.Id).ToArray(), [], result.Value.ToArray());
            model = result.Model;
            store.SaveWorkProposal(run.Id, item.Key, JsonSerializer.Serialize(envelope, JsonDefaults.Options), model);
        }

        if (string.IsNullOrWhiteSpace(model)) throw new InvariantException("Stored proposal model is missing.");
        if (item.Phase == "assimilation")
        {
            var allowed = envelope.AllowedCandidateIds.ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < envelope.Relations.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var proposal = envelope.Relations[index];
                try
                {
                    if (!allowed.Contains(proposal.MemoryId) || !byId.ContainsKey(proposal.MemoryId) ||
                        proposal.RelatedMemoryId != seed.Id || proposal.MemoryId == seed.Id || !Enum.IsDefined(proposal.Kind))
                        throw new InvariantException("Assimilation must relate a supplied candidate to the observation, with no self edge.");
                    store.AddRelation(proposal, run, timeProvider.GetUtcNow());
                }
                catch (InvariantException error) { store.RejectProposal(run.Id, item.Key, index, error.Message); }
            }
        }
        else
        {
            for (var index = 0; index < envelope.Abstractions.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var proposal = envelope.Abstractions[index];
                try
                {
                    // A previous attempt may have committed this output before interruption.
                    // Do not spend the next week's budget embedding it again.
                    if (store.GetAppliedAbstraction(run.Id, item.Key, index) is not null) continue;
                    // Validate before paying for embedding, and validate again in the atomic store mutation.
                    store.ValidateAbstraction(proposal, run, envelope.AllowedCandidateIds);
                    var embedding = await cognition.EmbedAsync(proposal.Content, context, ct);
                    store.AddAbstraction(proposal, model, run, item.Key, index,
                        envelope.AllowedCandidateIds, embedding, timeProvider.GetUtcNow());
                }
                catch (InvariantException error) { store.RejectProposal(run.Id, item.Key, index, error.Message); }
            }
        }
        store.CompleteWork(run.Id, item.Key);
    }

    private async Task<IReadOnlyList<MemoryRecord>> AssimilationCandidatesAsync(MemoryRecord seed, GraphSnapshot snapshot, CallContext context, CancellationToken ct)
    {
        var byId = snapshot.ById;
        var nearest = await search.SearchAsync(seed.Content, context, ct, snapshot: snapshot, limit: options.SearchCandidates);
        return seed.Relations.Select(x => x.RelatedMemoryId).Concat(nearest.Select(x => x.Id))
            .Where(x => x != seed.Id && byId.ContainsKey(x)).Distinct(StringComparer.Ordinal)
            .Take(options.SearchCandidates).Select(x => byId[x]).ToArray();
    }

    private async Task<IReadOnlyList<MemoryRecord>> NeighborhoodAsync(MemoryRecord seed, GraphSnapshot snapshot, RunKind kind, CallContext context, CancellationToken ct)
    {
        var byId = snapshot.ById;
        var limit = kind == RunKind.Dream ? options.NeighborhoodSize : options.MeditationGraphLimit;
        var nearest = await search.NearestAsync(seed, snapshot, context, ct, seed.Depth, options.NeighborhoodSize);
        var selected = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal) { [seed.Id] = seed };
        void Add(string id)
        {
            if (selected.Count < limit && byId.TryGetValue(id, out var memory)) selected.TryAdd(id, memory);
        }

        // Keep direct evidence visible even when semantic retrieval returns a full page.
        // First reserve enough same-depth candidates for a possible valid abstraction.
        foreach (var relation in seed.Relations)
            if (byId.TryGetValue(relation.RelatedMemoryId, out var target) && target.Depth == seed.Depth) Add(target.Id);
        foreach (var memory in nearest.Where(x => x.Depth == seed.Depth))
        {
            if (selected.Values.Count(x => x.Depth == seed.Depth) >= options.RootBase) break;
            Add(memory.Id);
        }
        foreach (var relation in seed.Relations) Add(relation.RelatedMemoryId);
        foreach (var memory in nearest) Add(memory.Id);
        if (kind == RunKind.Meditation)
        {
            // Only outgoing semantic edges and the explicit derived_from ancestry are traversed.
            var queue = new Queue<MemoryRecord>(selected.Values);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (queue.TryDequeue(out var memory) && selected.Count < limit)
            {
                if (!visited.Add(memory.Id)) continue;
                foreach (var id in memory.DerivedFrom.Concat(memory.Relations.Select(x => x.RelatedMemoryId)))
                {
                    var count = selected.Count;
                    Add(id);
                    if (selected.Count > count) queue.Enqueue(selected[id]);
                }
            }
        }
        return selected.Values.ToArray();
    }

    private IReadOnlyList<SourceArtifact> ReadSources(IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyDictionary<string, MemoryRecord> byId)
    {
        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<MemoryRecord>(neighborhood);
        while (queue.TryDequeue(out var memory) && sourceIds.Count < options.MeditationSourceLimit)
        {
            if (!visited.Add(memory.Id)) continue;
            if (memory.SourceRef is not null) sourceIds.Add(memory.SourceRef);
            foreach (var parent in memory.DerivedFrom)
                if (byId.TryGetValue(parent, out var record)) queue.Enqueue(record);
        }
        return sourceIds.Select(store.ReadSource).ToArray();
    }

    private RunSummary Summary(long runId, string status) => new(runId, status,
        store.GetWorkItems(runId).Count(x => x.Status == "complete"), store.GetRejectedProposalCount(runId), store.GetRunAccountedUsd(runId));

    private static bool InPeriod(DateTimeOffset timestamp, RunRecord run) => timestamp >= run.PeriodStart && timestamp < run.PeriodEnd;
    private static bool IsTerminal(string status) => status is "complete" or "budget_exhausted";

    private static bool TryGetCarryOrigin(RunWorkItem item, out long runId, out string workKey)
    {
        runId = 0;
        workKey = "";
        if (!item.Key.StartsWith("carry:", StringComparison.Ordinal)) return false;
        var parts = item.Key.Split(':', 3);
        if (parts.Length != 3 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out runId))
            throw new InvariantException("Malformed carry work reference.");
        workKey = parts[2];
        return true;
    }

    private sealed record SavedProposal(string[] AllowedCandidateIds, RelationProposal[] Relations, AbstractionProposal[] Abstractions);
}
