namespace LongJourney.Core;

public interface ICognition
{
    string EmbeddingSpace { get; }
    Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(string raw, CallContext context, CancellationToken cancellationToken);
    Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken);
    Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context, IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken);
    Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation, IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken);
    Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources, CognitionRole role, CallContext context, CancellationToken cancellationToken);
}

public interface IUsageLedger
{
    UsageReservation ReserveUsage(long? runId, string model, string operation, decimal maximumUsd, DateTimeOffset now);
    void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now);
}
