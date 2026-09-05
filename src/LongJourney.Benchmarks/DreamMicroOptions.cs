using System.Security.Cryptography;
using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Benchmarks;

public sealed class DreamMicroOptions
{
    public string DatasetPath { get; set; } = "data/benchmark/longmemeval_s_cleaned.json";
    public string DatasetSha256 { get; set; } = "d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442";
    public string OutputDirectory { get; set; } = "data/benchmark/runs/dream-micro-2026-09-05";
    public decimal TotalBudgetUsd { get; set; } = 20m;
    public int Workers { get; set; } = 2;
    public int MaxObservationsPerSession { get; set; } = 128;
    public OpenAiOptions OpenAi { get; set; } = new BenchmarkOptions().OpenAi;
    public ModelOptions EvidenceModel { get; set; } = new() { ReasoningEffort = "medium", MaxOutputTokens = 8192 };

    public EngineOptions CreateEngineOptions(string directory, int maximumRaw) => new()
    {
        DataDirectory = directory,
        MaxRawCharacters = maximumRaw,
        MaxObservations = MaxObservationsPerSession,
        RecallLimit = 5,
        TimeZoneId = "UTC",
        MeditationBudgetUsd = null,
        SchedulerEnabled = false
    };

    public IReadOnlyList<BenchmarkQuestion> ValidateAndSelect()
    {
        if (TotalBudgetUsd is <= 0 or > 20 || Workers is < 1 or > 2 || MaxObservationsPerSession < 2 ||
            string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new InputException("Micro benchmark requires a total cap in (0,20], multiple observations and an output directory.");
        }
        using var stream = File.OpenRead(DatasetPath);
        if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(DatasetSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputException("Micro dataset differs from the pinned checksum.");
        }
        OpenAiClient.ValidateOptions(OpenAi);
        OpenAiPricing.ValidateModel(EvidenceModel, "benchmark_evidence");
        var dataset = LongMemEvalDataset.Load(DatasetPath);
        if (dataset.Count != 500)
        {
            throw new InputException("Micro sampling requires the complete 500-question LongMemEval-S dataset.");
        }
        return DreamMicroSelection.Select(dataset);
    }

    public static DreamMicroOptions Load(string path) =>
        JsonSerializer.Deserialize<DreamMicroOptions>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InputException("Micro benchmark configuration is empty.");
}
