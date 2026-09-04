namespace LongJourney.Core;

/// <summary>Retrieves candidates and may generate missing embeddings, charged to the supplied call context.</summary>
public interface IMemorySearch
{
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(
        string query,
        CallContext context,
        CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null,
        int? depth = null,
        int? limit = null);

    Task<IReadOnlyList<MemoryRecord>> NearestAsync(
        MemoryRecord seed,
        GraphSnapshot snapshot,
        CallContext context,
        CancellationToken cancellationToken,
        int? depth = null,
        int? limit = null);
}
