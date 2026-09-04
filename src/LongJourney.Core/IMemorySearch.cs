namespace LongJourney.Core;

public interface IMemorySearch
{
    Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, CallContext context, CancellationToken cancellationToken,
        GraphSnapshot? snapshot = null, int? depth = null, int? limit = null);
    Task<IReadOnlyList<MemoryRecord>> NearestAsync(MemoryRecord seed, GraphSnapshot snapshot, CallContext context,
        CancellationToken cancellationToken, int? depth = null, int? limit = null);
}
