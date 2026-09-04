namespace LongJourney.Core;

/// <summary>Produces proposals and embeddings; the store alone validates and commits graph changes.</summary>
/// <remarks>
/// Returned results are complete and may be retained without copying. Implementations must not mutate
/// returned collections, proposal parent lists, or embedding buffers. Sharing immutable data is allowed.
/// </remarks>
public interface ICognition
{
    string EmbeddingSpace
    {
        get;
    }

    Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
        string raw,
        CallContext context,
        CancellationToken cancellationToken);

    Task<EmbeddingVector> EmbedAsync(
        string text,
        CallContext context,
        CancellationToken cancellationToken);

    Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(
        string query,
        string? context,
        IReadOnlyList<MemoryRecord> candidates,
        CallContext call,
        CancellationToken cancellationToken);

    Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(
        MemoryRecord observation,
        IReadOnlyList<MemoryRecord> candidates,
        CallContext context,
        CancellationToken cancellationToken);

    Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
        IReadOnlyList<MemoryRecord> neighborhood,
        IReadOnlyList<SourceArtifact> sources,
        CognitionRole role,
        CallContext context,
        CancellationToken cancellationToken);
}

/// <summary>Reserves the maximum cost before a call and settles known usage even if its proposal is rejected.</summary>
public interface IUsageLedger
{
    UsageReservation ReserveUsage(
        long? runId,
        string model,
        string operation,
        decimal maximumUsd,
        DateTimeOffset now);

    void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now);
}
