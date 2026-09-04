using System.Text.Json;
using System.Text.Json.Serialization;

namespace LongJourney.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
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

public sealed record MemoryRecord(
    string Id, int Depth, string Content, string? SourceRef,
    IReadOnlyList<string> DerivedFrom, IReadOnlyList<MemoryRelation> Relations,
    DateTimeOffset CreatedAt, long DreamRevision, DateTimeOffset? LastRecalledAt,
    string CreatedByModel, int UniqueSourceRootCount, long Sequence)
{
    public IReadOnlyList<string> PositiveRelated => GetRelatedMemoryIds(RelationKind.Positive);
    public IReadOnlyList<string> NegativeRelated => GetRelatedMemoryIds(RelationKind.Negative);

    private string[] GetRelatedMemoryIds(RelationKind kind)
    {
        var memoryIds = new List<string>();
        foreach (var relation in Relations)
        {
            if (relation.Kind == kind)
            {
                memoryIds.Add(relation.RelatedMemoryId);
            }
        }

        return memoryIds.ToArray();
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

public sealed record GraphSnapshot(IReadOnlyList<MemoryRecord> Memories, IReadOnlyList<RecallEvent> RecallEvents)
{
    // Build an index on demand without changing or caching the evidence snapshot.
    public IReadOnlyDictionary<string, MemoryRecord> ById
    {
        get
        {
            var memoriesById = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
            foreach (var memory in Memories)
            {
                memoriesById.Add(memory.Id, memory);
            }

            return memoriesById;
        }
    }
}

public sealed record RunWorkItem(long RunId, string Key, string Phase, string MemoryId, int Ordinal, string Status, string? ProposalJson, string? Model);
public sealed record RunSummary(long RunId, string Status, int CompletedItems, int RejectedProposals, decimal AccountedUsd);
public sealed record ApiUsage(long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd, long CacheWriteTokens = 0);
public sealed record UsageReservation(string Id, long? RunId, string Model, string Operation, decimal ReservedUsd);

public sealed class InvariantException(string message) : Exception(message);
public sealed class BudgetExceededException(string message) : Exception(message);
public sealed class InputException(string message) : Exception(message);
