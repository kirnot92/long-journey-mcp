namespace LongJourney.Core;

/// <summary>Local inspection only: no cognition, recovery, recall recording, or state changes.</summary>
public interface IInspectionReader
{
    InspectionOverview BrowseMemories(InspectionMemoryQuery query);
    MemoryRecord? GetMemory(string id);
    InspectionTrace? ReadTrace(string id);
    InspectionSource? InspectSource(string id, int page = 1, long? snapshot = null);
    InspectionPage<InspectionRun> BrowseRuns(int page = 1, long? snapshot = null);
    InspectionRunDetail? InspectRun(long id, int page = 1);
    InspectionWorkDetail? InspectWork(long runId, string key);
}

public sealed record InspectionMemoryQuery(
    int Page = 1, int? Depth = null, string? Search = null,
    long? Snapshot = null, long? Revision = null);

public sealed record InspectionPage<T>(IReadOnlyList<T> Items, long Total, int Page, long Snapshot)
{
    public const int PageSize = 25;
    public long Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < Pages;
}

public sealed record InspectionMemory(string Id, int Depth, string Content, DateTimeOffset CreatedAt);
public sealed record InspectionDepth(int Depth, long Count);
public sealed record InspectionStatistics(
    long Memories, long Sources, long Relations, IReadOnlyList<InspectionDepth> Depths);
public sealed record InspectionRelation(
    string MemoryId, string RelatedMemoryId, RelationKind Kind, DateTimeOffset RelatedAt);
public sealed record InspectionOverview(
    InspectionPage<InspectionMemory> Memories,
    InspectionStatistics Statistics, IReadOnlyList<InspectionRelation> RecentRelations);

public sealed record InspectionTrace(string MemoryId, IReadOnlyList<MemoryRecord> Memories, bool Truncated)
{
    public const int NodeLimit = 200;
}

public sealed record InspectionSource(
    SourceRecord Source, string? Raw, string? ReadError, InspectionPage<InspectionMemory> Observations);

public sealed record InspectionRun(RunRecord Run, DateTimeOffset? FinishedAt, bool WorkInitialized);
public sealed record InspectionCost(decimal ActualUsd, decimal UnsettledReservedUsd, long UnsettledCalls)
{
    public decimal AccountedUsd => ActualUsd + UnsettledReservedUsd;
}
public sealed record InspectionWorkSummary(
    long RunId, string Key, string Phase, string MemoryId, int Ordinal, string Status,
    bool HasProposal, string? Model);
public sealed record InspectionRunDetail(
    InspectionRun Run, InspectionCost Cost,
    InspectionPage<InspectionWorkSummary> Work, long CompletedWork, long OutputMemories);
public sealed record InspectionRejection(int ProposalIndex, string Reason);
public sealed record InspectionWorkOrigin(long RunId, string Key);
public sealed record InspectionWorkDetail(
    RunWorkItem Work, IReadOnlyList<InspectionRejection> Rejections, InspectionWorkOrigin? Origin);
