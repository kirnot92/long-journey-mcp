namespace LongJourney.Core;

public sealed record NewObservation(string Content, string Model, EmbeddingVector Embedding);
public sealed record WorkSeed(string Key, string Phase, string MemoryId, int Ordinal);
public sealed record StoredEmbedding(string MemoryId, EmbeddingVector Embedding);

public interface IMemoryStore : IUsageLedger
{
    SourceArtifact SaveSource(string raw, DateTimeOffset now);
    bool ClaimSource(string sourceId);
    void CompleteSource(string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now);
    void FailSource(string sourceId);
    IReadOnlyList<SourceRecord> GetIncompleteSources();
    SourceArtifact ReadSource(string sourceId);
    RememberResult ReadRememberResult(string sourceId, bool duplicate);
    IReadOnlyList<MemoryRecord> GetSourceMemories(string sourceId);
    MemoryRecord? GetMemory(string id);
    GraphSnapshot ReadSnapshot(RunRecord? run = null);
    IReadOnlyList<string> LexicalSearch(string query, int limit, int? depth = null, long? memoryHighWater = null);
    EmbeddingVector? GetEmbedding(string memoryId, string space);
    IReadOnlyList<StoredEmbedding> GetEmbeddings(string space);
    void SaveEmbedding(string memoryId, EmbeddingVector embedding);
    void RecordRecall(IReadOnlyList<string> ids, DateTimeOffset now);
    RunRecord GetOrCreateRun(RunKind kind, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, decimal? budgetUsd);
    IReadOnlyList<RunRecord> GetRuns();
    void EnsureWorkItems(long runId, IReadOnlyList<WorkSeed> items);
    IReadOnlyList<RunWorkItem> GetWorkItems(long runId);
    void SaveWorkProposal(long runId, string key, string proposalJson, string model);
    void CompleteWork(long runId, string key);
    void RejectProposal(long runId, string key, int index, string reason);
    int GetRejectedProposalCount(long runId);
    void ValidateAbstraction(AbstractionProposal proposal, RunRecord run, IReadOnlyCollection<string> allowedParents);
    MemoryRecord? GetAppliedAbstraction(long runId, string workKey, int proposalIndex);
    MemoryRecord AddAbstraction(AbstractionProposal proposal, string model, RunRecord run, string workKey, int proposalIndex,
        IReadOnlyCollection<string> allowedParents, EmbeddingVector embedding, DateTimeOffset now);
    void AddRelation(RelationProposal proposal, RunRecord run, DateTimeOffset now);
    void FinishRun(long runId, string status, DateTimeOffset now);
    decimal GetRunAccountedUsd(long runId);
    string? GetState(string key);
    void SetState(string key, string value);
}
