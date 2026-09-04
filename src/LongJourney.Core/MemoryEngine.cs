namespace LongJourney.Core;

public sealed class MemoryEngine(IMemoryStore store, ICognition cognition, IMemorySearch search, EngineOptions options, TimeProvider timeProvider)
{
    public async Task<RememberResult> RememberAsync(string raw, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > options.MaxRawCharacters) throw new InputException($"raw exceeds {options.MaxRawCharacters} characters; split it into individual observations.");
        var source = store.SaveSource(raw, timeProvider.GetUtcNow());
        if (!store.ClaimSource(source.Source.Id))
            return store.ReadRememberResult(source.Source.Id, true);
        return await ExtractSourceAsync(source, cancellationToken);
    }

    private async Task<RememberResult> ExtractSourceAsync(SourceArtifact artifact, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(artifact.Raw))
            {
                store.CompleteSource(artifact.Source.Id, [], timeProvider.GetUtcNow());
                return new RememberResult(artifact.Source.Id, false, []);
            }
            var proposals = await cognition.ExtractAsync(artifact.Raw, new CallContext(), cancellationToken);
            if (proposals.Value.Count > options.MaxObservations) throw new InvariantException("Provider returned too many observations.");
            var observations = new List<NewObservation>();
            foreach (var proposal in proposals.Value)
            {
                if (string.IsNullOrWhiteSpace(proposal.Content) || proposal.Content.Length > options.MaxMemoryCharacters)
                    throw new InvariantException("Provider returned invalid observation content.");
                var embedding = await cognition.EmbedAsync(proposal.Content, new CallContext(), cancellationToken);
                if (embedding.Space != cognition.EmbeddingSpace) throw new InvariantException("Embedding model space mismatch.");
                observations.Add(new NewObservation(proposal.Content, proposals.Model, embedding));
            }
            store.CompleteSource(artifact.Source.Id, observations, timeProvider.GetUtcNow());
            return store.ReadRememberResult(artifact.Source.Id, false);
        }
        catch
        {
            store.FailSource(artifact.Source.Id);
            throw;
        }
    }

    public async Task<IReadOnlyList<IngestionFailure>> ResumePendingAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<IngestionFailure>();
        foreach (var source in store.GetIncompleteSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!store.ClaimSource(source.Id)) continue;
            try { await ExtractSourceAsync(store.ReadSource(source.Id), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                store.FailSource(source.Id);
                throw;
            }
            catch (Exception error)
            {
                store.FailSource(source.Id);
                failures.Add(new IngestionFailure(source.Id, error.GetType().Name));
            }
        }
        return failures;
    }

    public async Task<RecallResult> RecallAsync(string query, string? context = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InputException("query must not be empty.");
        if (query.Length > options.MaxRawCharacters || context?.Length > options.MaxRawCharacters)
            throw new InputException("Recall query or context exceeds the configured input bound.");
        var candidates = await search.SearchAsync(query, new CallContext(), cancellationToken);
        if (candidates.Count == 0) return new RecallResult([]);
        var selection = await cognition.SelectAsync(query, context, candidates, new CallContext(), cancellationToken);
        var byId = candidates.ToDictionary(m => m.Id, StringComparer.Ordinal);
        if (selection.Value.Any(id => !byId.ContainsKey(id))) throw new InvariantException("Recall selection contains an ID outside the candidate set.");
        var selected = selection.Value.Distinct(StringComparer.Ordinal).Take(options.RecallLimit).ToArray();
        var now = timeProvider.GetUtcNow();
        store.RecordRecall(selected, now);
        return new RecallResult(selected.Select(id => byId[id] with { LastRecalledAt = now }).ToArray());
    }

    public TraceResult Trace(string memoryId)
    {
        var graph = store.ReadSnapshot().ById;
        if (!graph.ContainsKey(memoryId)) throw new InputException("Memory not found.");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var memories = new List<MemoryRecord>();
        var pending = new Stack<string>();
        pending.Push(memoryId);
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id)) continue;
            if (!graph.TryGetValue(id, out var memory)) throw new InvariantException("Broken provenance reference.");
            memories.Add(memory);
            if (memory.SourceRef is not null) sources.Add(memory.SourceRef);
            foreach (var parent in memory.DerivedFrom.OrderByDescending(x => x, StringComparer.Ordinal)) pending.Push(parent);
        }
        return new TraceResult(memoryId, memories, sources.Select(store.ReadSource).ToArray());
    }
}
