namespace LongJourney.Core;

public sealed class MemorySearch(IMemoryStore store, ICognition cognition, EngineOptions options) : IMemorySearch
{
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, CallContext context, CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null, int? depth = null, int? limit = null)
    {
        snapshot ??= store.ReadSnapshot();
        var candidates = snapshot.Memories.Where(m => depth is null || m.Depth == depth).ToArray();
        if (candidates.Length == 0) return [];
        await IndexAsync(candidates, context, cancellationToken);
        var queryVector = await cognition.EmbedAsync(query, context, cancellationToken);
        ValidateSpace(queryVector);
        var count = limit ?? options.SearchCandidates;
        var candidateIds = candidates.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var lexical = store.LexicalSearch(query, count, depth, snapshot.Memories.Max(m => m.Sequence)).Where(candidateIds.Contains).ToArray();
        var semantic = Rank(queryVector, candidates, count);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var list in new[] { lexical, semantic })
            for (var index = 0; index < list.Length; index++)
                scores[list[index]] = scores.GetValueOrDefault(list[index]) + 1d / (60 + index + 1);
        var byId = candidates.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(count).Select(p => byId[p.Key]).ToArray();
    }

    public async Task<IReadOnlyList<MemoryRecord>> NearestAsync(MemoryRecord seed, GraphSnapshot snapshot, CallContext context,
        CancellationToken cancellationToken, int? depth = null, int? limit = null)
    {
        var candidates = snapshot.Memories.Where(m => m.Id != seed.Id && (depth is null || m.Depth == depth)).ToArray();
        if (candidates.Length == 0) return [];
        await IndexAsync(candidates.Append(seed).ToArray(), context, cancellationToken);
        var vector = store.GetEmbedding(seed.Id, cognition.EmbeddingSpace)!;
        var byId = candidates.ToDictionary(m => m.Id, StringComparer.Ordinal);
        return Rank(vector, candidates, limit ?? options.NeighborhoodSize).Select(id => byId[id]).ToArray();
    }

    public Task ReindexAsync(CallContext context, CancellationToken cancellationToken) => IndexAsync(store.ReadSnapshot().Memories, context, cancellationToken);

    private async Task IndexAsync(IReadOnlyList<MemoryRecord> memories, CallContext context, CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            var indexed = store.GetEmbeddings(cognition.EmbeddingSpace).Select(v => v.MemoryId).ToHashSet(StringComparer.Ordinal);
            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (indexed.Contains(memory.Id)) continue;
                var vector = await cognition.EmbedAsync(memory.Content, context, cancellationToken);
                ValidateSpace(vector);
                store.SaveEmbedding(memory.Id, vector);
                indexed.Add(memory.Id);
            }
        }
        finally { _indexGate.Release(); }
    }

    private void ValidateSpace(EmbeddingVector vector)
    {
        if (vector.Space != cognition.EmbeddingSpace) throw new InvariantException("Embedding provider returned a different model space.");
    }

    private string[] Rank(EmbeddingVector query, IReadOnlyList<MemoryRecord> candidates, int limit)
    {
        var allowed = candidates.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        return store.GetEmbeddings(cognition.EmbeddingSpace).Where(v => allowed.Contains(v.MemoryId))
            .Select(v => (v.MemoryId, Score: Cosine(query.Values, v.Embedding.Values)))
            .OrderByDescending(v => v.Score).ThenBy(v => v.MemoryId, StringComparer.Ordinal).Take(limit).Select(v => v.MemoryId).ToArray();
    }

    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) throw new InvariantException("Embedding dimension mismatch.");
        double product = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Count; i++)
        {
            if (!float.IsFinite(left[i]) || !float.IsFinite(right[i])) throw new InvariantException("Non-finite embedding.");
            product += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }
        if (leftNorm == 0 || rightNorm == 0) throw new InvariantException("Zero embedding.");
        return product / Math.Sqrt(leftNorm * rightNorm);
    }
}
