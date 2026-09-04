namespace LongJourney.Core;

public sealed class MemoryEngine(
    IMemoryStore store,
    ICognition cognition,
    IMemorySearch search,
    EngineOptions options,
    TimeProvider timeProvider)
{
    public async Task<RememberResult> RememberAsync(string raw, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length > options.MaxRawCharacters)
        {
            throw new InputException(
                $"raw exceeds {options.MaxRawCharacters} characters; split it into individual observations.");
        }

        var artifact = store.SaveSource(raw, timeProvider.GetUtcNow());
        var sourceId = artifact.Source.Id;
        if (!store.ClaimSource(sourceId))
        {
            return store.ReadRememberResult(sourceId, true);
        }

        try
        {
            return await ExtractAndSaveObservationsAsync(artifact, cancellationToken);
        }
        catch
        {
            store.FailSource(sourceId);
            throw;
        }
    }

    public async Task<IReadOnlyList<IngestionFailure>> ResumePendingAsync(
        CancellationToken cancellationToken = default)
    {
        var failures = new List<IngestionFailure>();
        foreach (var source in store.GetIncompleteSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!store.ClaimSource(source.Id))
            {
                continue;
            }

            try
            {
                var artifact = store.ReadSource(source.Id);
                await ExtractAndSaveObservationsAsync(artifact, cancellationToken);
            }
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

    public async Task<RecallResult> RecallAsync(
        string query,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InputException("query must not be empty.");
        }

        if (query.Length > options.MaxRawCharacters || context?.Length > options.MaxRawCharacters)
        {
            throw new InputException("Recall query or context exceeds the configured input bound.");
        }

        var candidates = await search.SearchAsync(query, new CallContext(), cancellationToken);
        if (candidates.Count == 0)
        {
            return new RecallResult([]);
        }

        var selection = await cognition.SelectAsync(
            query, context, candidates, new CallContext(), cancellationToken);

        var candidatesById = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidatesById.Add(candidate.Id, candidate);
        }

        // Validate the full response, including IDs beyond the result limit.
        foreach (var memoryId in selection.Value)
        {
            if (!candidatesById.ContainsKey(memoryId))
            {
                throw new InvariantException("Recall selection contains an ID outside the candidate set.");
            }
        }

        var selectedIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memoryId in selection.Value)
        {
            if (selectedIds.Count >= options.RecallLimit)
            {
                break;
            }

            if (seenIds.Add(memoryId))
            {
                selectedIds.Add(memoryId);
            }
        }

        var recalledAt = timeProvider.GetUtcNow();
        store.RecordRecall(selectedIds, recalledAt);

        var recalledMemories = new List<MemoryRecord>();
        foreach (var memoryId in selectedIds)
        {
            var memory = candidatesById[memoryId];
            recalledMemories.Add(memory with
            {
                LastRecalledAt = recalledAt
            });
        }

        return new RecallResult(recalledMemories);
    }

    public TraceResult Trace(string memoryId)
    {
        var memoriesById = store.ReadSnapshot().ById;
        if (!memoriesById.ContainsKey(memoryId))
        {
            throw new InputException("Memory not found.");
        }

        var visitedIds = new HashSet<string>(StringComparer.Ordinal);
        var sourceIds = new SortedSet<string>(StringComparer.Ordinal);
        var memories = new List<MemoryRecord>();
        var pendingIds = new Stack<string>();
        pendingIds.Push(memoryId);

        while (pendingIds.TryPop(out var currentId))
        {
            if (!visitedIds.Add(currentId))
            {
                continue;
            }

            if (!memoriesById.TryGetValue(currentId, out var memory))
            {
                throw new InvariantException("Broken provenance reference.");
            }

            memories.Add(memory);
            if (memory.SourceRef is not null)
            {
                sourceIds.Add(memory.SourceRef);
            }

            // Push in reverse order so the stack visits parent IDs in ordinal order.
            var parentIds = new List<string>(memory.DerivedFrom);
            parentIds.Sort(StringComparer.Ordinal);
            for (var index = parentIds.Count - 1; index >= 0; index--)
            {
                pendingIds.Push(parentIds[index]);
            }
        }

        var sources = new List<SourceArtifact>();
        foreach (var sourceId in sourceIds)
        {
            sources.Add(store.ReadSource(sourceId));
        }

        return new TraceResult(memoryId, memories, sources);
    }

    private async Task<RememberResult> ExtractAndSaveObservationsAsync(
        SourceArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifact.Raw))
        {
            store.CompleteSource(artifact.Source.Id, [], timeProvider.GetUtcNow());
            return new RememberResult(artifact.Source.Id, false, []);
        }

        var proposals = await cognition.ExtractAsync(artifact.Raw, new CallContext(), cancellationToken);
        if (proposals.Value.Count > options.MaxObservations)
        {
            throw new InvariantException("Provider returned too many observations.");
        }

        var observations = new List<NewObservation>();
        foreach (var proposal in proposals.Value)
        {
            if (string.IsNullOrWhiteSpace(proposal.Content) ||
                proposal.Content.Length > options.MaxMemoryCharacters)
            {
                throw new InvariantException("Provider returned invalid observation content.");
            }

            var embedding = await cognition.EmbedAsync(proposal.Content, new CallContext(), cancellationToken);
            if (embedding.Space != cognition.EmbeddingSpace)
            {
                throw new InvariantException("Embedding model space mismatch.");
            }

            observations.Add(new NewObservation(proposal.Content, proposals.Model, embedding));
        }

        store.CompleteSource(artifact.Source.Id, observations, timeProvider.GetUtcNow());
        return store.ReadRememberResult(artifact.Source.Id, false);
    }
}
