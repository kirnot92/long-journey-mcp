using System.Security.Cryptography;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed class BenchmarkOptions
{
    public string DatasetPath { get; set; } = "data/benchmark/longmemeval_s_cleaned.json";
    public string DatasetSha256 { get; set; } = "d6f21ea9d60a0d56f34a05b609c79c88a451d2ae03597821ea3d5a9678c3a442";
    public string OutputDirectory { get; set; } = "data/benchmark/runs/proposal-2026-09-05";
    public int Workers { get; set; } = 4;
    public decimal MeditationBudgetUsd { get; set; } = 5m;
    public int MaxObservationsPerSession { get; set; } = 128;
    public OpenAiOptions OpenAi { get; set; } = CreateModels();

    private static OpenAiOptions CreateModels()
    {
        var models = new OpenAiOptions();
        // The benchmark adapter processes full sessions, which can contain many observations.
        models.Remember.MaxOutputTokens = 16384;
        return models;
    }

    public EngineOptions CreateEngineOptions(string directory, int maximumRawCharacters) => new()
    {
        DataDirectory = directory,
        MaxRawCharacters = maximumRawCharacters,
        MaxObservations = MaxObservationsPerSession,
        RecallLimit = 5,
        TimeZoneId = "UTC",
        MeditationBudgetUsd = MeditationBudgetUsd
    };

    public void Validate()
    {
        if (MeditationBudgetUsd <= 0 || MeditationBudgetUsd > 5m)
        {
            throw new InputException("Weekly Meditation budget must be greater than zero and at most USD 5 per run.");
        }
        if (Workers < 1 || Workers > 32 || MaxObservationsPerSession < 2)
        {
            throw new InputException("Workers must be 1..32 and session ingestion must support multiple observations.");
        }
        using var stream = File.OpenRead(DatasetPath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!actualHash.Equals(DatasetSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InputException("Dataset checksum differs from the pinned LongMemEval-S dataset.");
        }
        LongJourney.OpenAI.OpenAiClient.ValidateOptions(OpenAi);
    }

    public static BenchmarkOptions Load(string path) =>
        JsonSerializer.Deserialize<BenchmarkOptions>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InputException("Benchmark configuration is empty.");
}
