using System.Security.Cryptography;
using System.Text;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

/// <summary>Retains completed extraction/embedding calls before ingestion can fail at a later step.</summary>
public sealed class CachedIngestionCognition(ICognition inner, string directory) : ICognition
{
    public string EmbeddingSpace => inner.EmbeddingSpace;

    public async Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
        string raw, CallContext context, CancellationToken cancellationToken)
    {
        var path = CachePath("observations", raw);
        if (File.Exists(path))
        {
            return BenchmarkFiles.ReadJson<CognitiveResult<IReadOnlyList<ObservationProposal>>>(path);
        }
        var result = await inner.ExtractAsync(raw, context, cancellationToken);
        BenchmarkFiles.WriteJson(path, result);
        return result;
    }

    public async Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
    {
        var path = CachePath("embedding", EmbeddingSpace + "\n" + text);
        if (File.Exists(path))
        {
            return BenchmarkFiles.ReadJson<EmbeddingVector>(path);
        }
        var result = await inner.EmbedAsync(text, context, cancellationToken);
        BenchmarkFiles.WriteJson(path, result);
        return result;
    }

    private string CachePath(string kind, string input) => Path.Combine(directory, kind,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input))) + ".json");

    public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context,
        IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken) =>
        inner.SelectAsync(query, context, candidates, call, cancellationToken);

    public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation,
        IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken) =>
        inner.AssimilateAsync(observation, candidates, context, cancellationToken);

    public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
        IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources, CognitionRole role,
        CallContext context, CancellationToken cancellationToken) =>
        inner.AbstractAsync(neighborhood, sources, role, context, cancellationToken);

    public Task<CognitiveResult<IReadOnlyList<string>>> PrioritizeMeditationAsync(
        IReadOnlyList<MeditationPriorityCandidate> candidates, CallContext context,
        CancellationToken cancellationToken) => inner.PrioritizeMeditationAsync(candidates, context, cancellationToken);
}
