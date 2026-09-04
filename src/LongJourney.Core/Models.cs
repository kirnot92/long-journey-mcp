using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LongJourney.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}

public enum RelationKind
{
    Positive,
    Negative
}

public enum RunKind
{
    Dream,
    Meditation
}

public enum CognitionRole
{
    Remember,
    Recall,
    Dream,
    Meditation
}

/// <summary>An outgoing edge; reading it never implies a reverse relation.</summary>
public sealed record MemoryRelation(string RelatedMemoryId, RelationKind Kind, DateTimeOffset RelatedAt, long Sequence);

/// <summary>Owns fixed provenance and outgoing relations. Recall-only copies share those immutable collections.</summary>
public sealed record MemoryRecord
{
    public string Id { get; }
    public int Depth { get; }
    public string Content { get; }
    public string? SourceRef { get; }
    public IReadOnlyList<string> DerivedFrom { get; }
    public IReadOnlyList<MemoryRelation> Relations { get; }
    public DateTimeOffset CreatedAt { get; }
    public long DreamRevision { get; }
    public DateTimeOffset? LastRecalledAt { get; init; }
    public string CreatedByModel { get; }
    public int UniqueSourceRootCount { get; }
    public long Sequence { get; }
    public IReadOnlyList<string> PositiveRelated { get; }
    public IReadOnlyList<string> NegativeRelated { get; }

    [JsonConstructor]
    public MemoryRecord(
        string id,
        int depth,
        string content,
        string? sourceRef,
        IReadOnlyList<string> derivedFrom,
        IReadOnlyList<MemoryRelation> relations,
        DateTimeOffset createdAt,
        long dreamRevision,
        DateTimeOffset? lastRecalledAt,
        string createdByModel,
        int uniqueSourceRootCount,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(derivedFrom);
        ArgumentNullException.ThrowIfNull(relations);

        Id = id;
        Depth = depth;
        Content = content;
        SourceRef = sourceRef;
        CreatedAt = createdAt;
        DreamRevision = dreamRevision;
        LastRecalledAt = lastRecalledAt;
        CreatedByModel = createdByModel;
        UniqueSourceRootCount = uniqueSourceRootCount;
        Sequence = sequence;

        // Own the inputs so callers cannot change the cached relation views.
        DerivedFrom = new List<string>(derivedFrom).AsReadOnly();
        Relations = new List<MemoryRelation>(relations).AsReadOnly();

        var positiveIds = new List<string>();
        var negativeIds = new List<string>();
        foreach (var relation in Relations)
        {
            if (relation.Kind == RelationKind.Positive)
            {
                positiveIds.Add(relation.RelatedMemoryId);
            }
            else if (relation.Kind == RelationKind.Negative)
            {
                negativeIds.Add(relation.RelatedMemoryId);
            }
        }

        PositiveRelated = positiveIds.AsReadOnly();
        NegativeRelated = negativeIds.AsReadOnly();
    }
}

public sealed record SourceRecord(string Id, string ContentHash, string RelativePath, DateTimeOffset CreatedAt, string Status);
public sealed record SourceArtifact(SourceRecord Source, string Raw);
public sealed record IngestionFailure(string SourceId, string ErrorType);
public sealed record RememberResult(string SourceId, bool Duplicate, IReadOnlyList<MemoryRecord> Memories, string Status = "complete");
public sealed record RecallResult(IReadOnlyList<MemoryRecord> Memories);
public sealed record TraceResult(string MemoryId, IReadOnlyList<MemoryRecord> Memories, IReadOnlyList<SourceArtifact> Sources);
public sealed record ObservationProposal(string Content);
public sealed record RelationProposal(string MemoryId, string RelatedMemoryId, RelationKind Kind);
public sealed record AbstractionProposal(string Content, IReadOnlyList<string> DerivedFrom);
/// <summary>A run-local work identity and its original frozen evidence for Meditation ordering.</summary>
public sealed record MeditationPriorityCandidate(
    string WorkKey, MemoryRecord Memory, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    IReadOnlyList<MemoryRecord> RelatedMemories);
public sealed record CognitiveResult<T>(T Value, string Model);
public sealed record EmbeddingVector(string Space, float[] Values);
/// <summary>The run charged for API calls. Carry work may use evidence from a different, older run.</summary>
public sealed record CallContext(long? RunId = null);
public sealed record RecallEvent(string MemoryId, DateTimeOffset RecalledAt, long Sequence);

/// <summary>A period and its fixed input sequence limits. Its ID also marks the output generation.</summary>
public sealed record RunRecord(
    long Id, RunKind Kind, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd,
    DateTimeOffset StartedAt, long MemoryHighWater, long RelationHighWater, long RecallHighWater,
    string Status, decimal? BudgetUsd);

/// <summary>Owns a fixed set of memories and recalls, indexed once when the snapshot is constructed.</summary>
public sealed record GraphSnapshot
{
    public IReadOnlyList<MemoryRecord> Memories { get; }
    public IReadOnlyList<RecallEvent> RecallEvents { get; }
    public IReadOnlyDictionary<string, MemoryRecord> ById { get; }

    [JsonConstructor]
    public GraphSnapshot(
        IReadOnlyList<MemoryRecord> memories,
        IReadOnlyList<RecallEvent> recallEvents)
    {
        ArgumentNullException.ThrowIfNull(memories);
        ArgumentNullException.ThrowIfNull(recallEvents);

        Memories = new List<MemoryRecord>(memories).AsReadOnly();
        RecallEvents = new List<RecallEvent>(recallEvents).AsReadOnly();

        var memoriesById = new Dictionary<string, MemoryRecord>(Memories.Count, StringComparer.Ordinal);
        foreach (var memory in Memories)
        {
            memoriesById.Add(memory.Id, memory);
        }
        ById = new ReadOnlyDictionary<string, MemoryRecord>(memoriesById);
    }
}

public sealed record RunWorkItem(long RunId, string Key, string Phase, string MemoryId, int Ordinal, string Status, string? ProposalJson, string? Model);
public sealed record RunSummary(long RunId, string Status, int CompletedItems, int RejectedProposals, decimal AccountedUsd);
public sealed record ApiUsage(long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, long CacheWriteTokens = 0);
public sealed record UsageReservation(string Id, long? RunId, string Model, string Operation, decimal ReservedUsd);

public sealed class InvariantException(string message) : Exception(message);
public sealed class BudgetExceededException(string message) : Exception(message);
public sealed class InputException(string message) : Exception(message);
