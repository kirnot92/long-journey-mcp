namespace LongJourney.Core;

public sealed class MemorySearch(
    IMemoryStore store,
    ICognition cognition,
    EngineOptions options) : IMemorySearch
{
    private readonly SemaphoreSlim embeddingGenerationGate = new(1, 1);

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string query,
        CallContext context,
        CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null,
        int? depth = null,
        int? limit = null)
    {
        snapshot ??= store.ReadSnapshot();
        var candidates = FindCandidates(snapshot, depth);
        if (candidates.Count == 0)
        {
            return [];
        }

        await GenerateAndSaveMissingEmbeddingsAsync(candidates, context, cancellationToken);
        var queryVector = await cognition.EmbedAsync(query, context, cancellationToken);
        ValidateEmbeddingSpace(queryVector);

        var resultLimit = limit ?? options.SearchCandidates;
        var lexicalRanking = ReadLexicalRanking(query, candidates, snapshot, depth, resultLimit);
        var semanticRanking = ReadSemanticRanking(queryVector, candidates, resultLimit);
        return MergeRankings(lexicalRanking, semanticRanking, candidates, resultLimit);
    }

    public async Task<IReadOnlyList<MemoryRecord>> NearestAsync(
        MemoryRecord seed,
        GraphSnapshot snapshot,
        CallContext context,
        CancellationToken cancellationToken,
        int? depth = null,
        int? limit = null)
    {
        var candidates = FindCandidates(snapshot, depth, seed.Id);
        if (candidates.Count == 0)
        {
            return [];
        }

        var memoriesToEmbed = new List<MemoryRecord>(candidates);
        memoriesToEmbed.Add(seed);
        await GenerateAndSaveMissingEmbeddingsAsync(memoriesToEmbed, context, cancellationToken);

        var seedVector = store.GetEmbedding(seed.Id, cognition.EmbeddingSpace)!;
        var candidatesById = BuildCandidateIndex(candidates);
        var rankedIds = ReadSemanticRanking(seedVector, candidates, limit ?? options.NeighborhoodSize);
        var results = new List<MemoryRecord>();
        foreach (var memoryId in rankedIds)
        {
            results.Add(candidatesById[memoryId]);
        }

        return results.ToArray();
    }

    public Task ReindexAsync(CallContext context, CancellationToken cancellationToken)
    {
        var snapshot = store.ReadSnapshot();
        return GenerateAndSaveMissingEmbeddingsAsync(snapshot.Memories, context, cancellationToken);
    }

    private async Task GenerateAndSaveMissingEmbeddingsAsync(
        IReadOnlyList<MemoryRecord> memories,
        CallContext context,
        CancellationToken cancellationToken)
    {
        await embeddingGenerationGate.WaitAsync(cancellationToken);
        try
        {
            var embeddedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var storedEmbedding in store.GetEmbeddings(cognition.EmbeddingSpace))
            {
                embeddedIds.Add(storedEmbedding.MemoryId);
            }

            foreach (var memory in memories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (embeddedIds.Contains(memory.Id))
                {
                    continue;
                }

                // Generation can spend the current run's budget; persist each vector before moving on.
                var vector = await cognition.EmbedAsync(memory.Content, context, cancellationToken);
                ValidateEmbeddingSpace(vector);
                store.SaveEmbedding(memory.Id, vector);
                embeddedIds.Add(memory.Id);
            }
        }
        finally
        {
            embeddingGenerationGate.Release();
        }
    }

    private List<string> ReadLexicalRanking(
        string query,
        IReadOnlyList<MemoryRecord> candidates,
        GraphSnapshot snapshot,
        int? depth,
        int limit)
    {
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidateIds.Add(candidate.Id);
        }

        var memoryHighWater = snapshot.Memories[0].Sequence;
        foreach (var memory in snapshot.Memories)
        {
            if (memory.Sequence > memoryHighWater)
            {
                memoryHighWater = memory.Sequence;
            }
        }

        // Limit the database query first, then exclude IDs outside the frozen candidate set.
        // Moving the limit after this filter would change the resulting lexical ranks.
        var lexicalMatches = store.LexicalSearch(query, limit, depth, memoryHighWater);
        var rankedIds = new List<string>();
        foreach (var memoryId in lexicalMatches)
        {
            if (candidateIds.Contains(memoryId))
            {
                rankedIds.Add(memoryId);
            }
        }

        return rankedIds;
    }

    private string[] ReadSemanticRanking(
        EmbeddingVector query,
        IReadOnlyList<MemoryRecord> candidates,
        int limit)
    {
        var candidateIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidateIds.Add(candidate.Id);
        }

        var storedEmbeddings = store.GetEmbeddings(cognition.EmbeddingSpace);
        if (limit <= 0)
        {
            return [];
        }

        var scores = new List<MemoryScore>();
        foreach (var storedEmbedding in storedEmbeddings)
        {
            if (!candidateIds.Contains(storedEmbedding.MemoryId))
            {
                continue;
            }

            var similarity = Cosine(query.Values, storedEmbedding.Embedding.Values);
            scores.Add(new MemoryScore(storedEmbedding.MemoryId, similarity));
        }
        scores.Sort(CompareScores);

        var rankedIds = new List<string>();
        foreach (var score in scores)
        {
            if (rankedIds.Count >= limit)
            {
                break;
            }

            rankedIds.Add(score.MemoryId);
        }

        return rankedIds.ToArray();
    }

    private static IReadOnlyList<MemoryRecord> MergeRankings(
        IReadOnlyList<string> lexicalRanking,
        IReadOnlyList<string> semanticRanking,
        IReadOnlyList<MemoryRecord> candidates,
        int limit)
    {
        var combinedScores = new Dictionary<string, double>(StringComparer.Ordinal);
        AddReciprocalRankScores(lexicalRanking, combinedScores);
        AddReciprocalRankScores(semanticRanking, combinedScores);

        var candidatesById = BuildCandidateIndex(candidates);
        var rankedScores = new List<MemoryScore>();
        foreach (var entry in combinedScores)
        {
            rankedScores.Add(new MemoryScore(entry.Key, entry.Value));
        }
        rankedScores.Sort(CompareScores);

        var results = new List<MemoryRecord>();
        foreach (var rankedMemory in rankedScores)
        {
            if (results.Count >= limit)
            {
                break;
            }

            results.Add(candidatesById[rankedMemory.MemoryId]);
        }

        return results.ToArray();
    }

    private static void AddReciprocalRankScores(
        IReadOnlyList<string> ranking,
        Dictionary<string, double> scores)
    {
        const int rankOffset = 60;
        for (var index = 0; index < ranking.Count; index++)
        {
            var memoryId = ranking[index];
            var rankContribution = 1d / (rankOffset + index + 1);
            scores[memoryId] = scores.GetValueOrDefault(memoryId) + rankContribution;
        }
    }

    private static int CompareScores(MemoryScore left, MemoryScore right)
    {
        var scoreComparison = right.Score.CompareTo(left.Score);
        if (scoreComparison != 0)
        {
            return scoreComparison;
        }

        return StringComparer.Ordinal.Compare(left.MemoryId, right.MemoryId);
    }

    private static List<MemoryRecord> FindCandidates(
        GraphSnapshot snapshot,
        int? depth,
        string? excludedMemoryId = null)
    {
        var candidates = new List<MemoryRecord>();
        foreach (var memory in snapshot.Memories)
        {
            if (excludedMemoryId is not null && memory.Id == excludedMemoryId)
            {
                continue;
            }

            if (depth is not null && memory.Depth != depth)
            {
                continue;
            }

            candidates.Add(memory);
        }

        return candidates;
    }

    private static Dictionary<string, MemoryRecord> BuildCandidateIndex(
        IReadOnlyList<MemoryRecord> candidates)
    {
        var candidatesById = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidatesById.Add(candidate.Id, candidate);
        }

        return candidatesById;
    }

    private void ValidateEmbeddingSpace(EmbeddingVector vector)
    {
        if (vector.Space != cognition.EmbeddingSpace)
        {
            throw new InvariantException("Embedding provider returned a different model space.");
        }
    }

    public static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            throw new InvariantException("Embedding dimension mismatch.");
        }

        double dotProduct = 0;
        double leftSquaredNorm = 0;
        double rightSquaredNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            var leftValue = left[index];
            var rightValue = right[index];
            if (!float.IsFinite(leftValue) || !float.IsFinite(rightValue))
            {
                throw new InvariantException("Non-finite embedding.");
            }

            dotProduct += (double)leftValue * rightValue;
            leftSquaredNorm += (double)leftValue * leftValue;
            rightSquaredNorm += (double)rightValue * rightValue;
        }

        if (leftSquaredNorm == 0 || rightSquaredNorm == 0)
        {
            throw new InvariantException("Zero embedding.");
        }

        return dotProduct / Math.Sqrt(leftSquaredNorm * rightSquaredNorm);
    }

    private sealed record MemoryScore(string MemoryId, double Score);
}
