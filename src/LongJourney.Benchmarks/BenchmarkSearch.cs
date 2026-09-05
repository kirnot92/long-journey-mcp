using LongJourney.Core;

namespace LongJourney.Benchmarks;

// Observation only: candidate collection delegates to the exact production search path.
public sealed class BenchmarkSearch : IMemorySearch
{
    private readonly IMemorySearch inner;
    public IReadOnlyList<MemoryRecord> LastCandidates { get; private set; } = [];
    public SearchCandidateTrace? LastTrace { get; private set; }

    public BenchmarkSearch(SqliteMemoryStore store, ICognition cognition, EngineOptions options)
    {
        inner = new MemorySearch(store, cognition, options, trace => LastTrace = trace);
    }

    public async Task<IReadOnlyList<MemoryRecord>> SearchAsync(string query, CallContext context,
        CancellationToken cancellationToken, GraphSnapshot? snapshot = null, int? depth = null, int? limit = null)
    {
        LastTrace = null;
        LastCandidates = await inner.SearchAsync(query, context, cancellationToken, snapshot, depth, limit);
        return LastCandidates;
    }

    public Task<IReadOnlyList<MemoryRecord>> NearestAsync(MemoryRecord seed, GraphSnapshot snapshot,
        CallContext context, CancellationToken cancellationToken, int? depth = null, int? limit = null) =>
        inner.NearestAsync(seed, snapshot, context, cancellationToken, depth, limit);
}
