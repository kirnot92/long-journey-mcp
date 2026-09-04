namespace LongJourney.Core;

public sealed record NewObservation(string Content, string Model, EmbeddingVector Embedding);
public sealed record WorkSeed(string Key, string Phase, string MemoryId, int Ordinal);
public sealed record StoredEmbedding(string MemoryId, EmbeddingVector Embedding);

public interface IMemoryStore : IUsageLedger
{
    // Source ingestion: exact raw identity, retryable extraction, atomic completion.
    SourceArtifact SaveSource(string raw, DateTimeOffset now);
    bool ClaimSource(string sourceId);
    void CompleteSource(string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now);
    void FailSource(string sourceId);
    IReadOnlyList<SourceRecord> GetIncompleteSources();
    SourceArtifact ReadSource(string sourceId);

    /// <summary>Reads source status and its observations from the same database snapshot.</summary>
    RememberResult ReadRememberResult(string sourceId, bool duplicate);

    // Graph reads expose outgoing relations and explicit provenance only.
    IReadOnlyList<MemoryRecord> GetSourceMemories(string sourceId);
    MemoryRecord? GetMemory(string id);

    /// <summary>With a run, bounds memory, relation and recall history by its frozen sequence limits.</summary>
    GraphSnapshot ReadSnapshot(RunRecord? run = null);

    IReadOnlyList<string> LexicalSearch(
        string query,
        int limit,
        int? depth = null,
        long? memoryHighWater = null);

    EmbeddingVector? GetEmbedding(string memoryId, string space);
    IReadOnlyList<StoredEmbedding> GetEmbeddings(string space);
    void SaveEmbedding(string memoryId, EmbeddingVector embedding);

    /// <summary>Records recall time without changing content, provenance or retrieval weight.</summary>
    void RecordRecall(IReadOnlyList<string> ids, DateTimeOffset now);

    // Durable consolidation: identify a period once, then resume its saved work and proposals.
    RunRecord GetOrCreateRun(
        RunKind kind,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset now,
        decimal? budgetUsd);

    IReadOnlyList<RunRecord> GetRuns();
    void EnsureWorkItems(long runId, IReadOnlyList<WorkSeed> items);
    IReadOnlyList<RunWorkItem> GetWorkItems(long runId);
    void SaveWorkProposal(long runId, string key, string proposalJson, string model);
    void CompleteWork(long runId, string key);
    void RejectProposal(long runId, string key, int index, string reason);
    int GetRejectedProposalCount(long runId);

    /// <summary>Checks supplied parents, frozen evidence and geometric source roots before embedding expense.</summary>
    void ValidateAbstraction(
        AbstractionProposal proposal,
        RunRecord run,
        IReadOnlyCollection<string> allowedParents);

    MemoryRecord? GetAppliedAbstraction(long runId, string workKey, int proposalIndex);

    /// <summary>Revalidates and atomically commits an output, or returns the already-applied proposal.</summary>
    MemoryRecord AddAbstraction(
        AbstractionProposal proposal,
        string model,
        RunRecord run,
        string workKey,
        int proposalIndex,
        IReadOnlyCollection<string> allowedParents,
        EmbeddingVector embedding,
        DateTimeOffset now);

    /// <summary>Adds a directed relation once; rediscovery preserves its original related_at.</summary>
    void AddRelation(RelationProposal proposal, RunRecord run, DateTimeOffset now);
    void FinishRun(long runId, string status, DateTimeOffset now);
    decimal GetRunAccountedUsd(long runId);

    string? GetState(string key);
    void SetState(string key, string value);
}
