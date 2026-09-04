namespace LongJourney.Core;

public sealed class EngineOptions
{
    public string DataDirectory { get; set; } = "data";
    public int RootBase { get; set; } = 3;
    public int MaxRawCharacters { get; set; } = 4000;
    public int MaxObservations { get; set; } = 1;
    public int MaxMemoryCharacters { get; set; } = 4000;
    public int SearchCandidates { get; set; } = 30;
    public int RecallLimit { get; set; } = 10;
    public int NeighborhoodSize { get; set; } = 20;
    public int MeditationGraphLimit { get; set; } = 80;
    public int MeditationSourceLimit { get; set; } = 12;
    public string TimeZoneId { get; set; } = "Asia/Seoul";
    public decimal? MeditationBudgetUsd
    {
        get; set;
    }
    public bool SchedulerEnabled { get; set; } = true;
    public int SchedulerPollSeconds { get; set; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory) ||
            RootBase < 2 ||
            MaxRawCharacters < 1 ||
            MaxObservations < 1 ||
            MaxMemoryCharacters < 1 ||
            SearchCandidates < 1 ||
            RecallLimit < 1 ||
            NeighborhoodSize < RootBase ||
            MeditationGraphLimit < NeighborhoodSize ||
            MeditationSourceLimit < 1 ||
            SchedulerPollSeconds < 1 ||
            MeditationBudgetUsd is <= 0)
        {
            throw new InputException("Invalid engine configuration. Bounds must be positive; RootBase must be at least 2.");
        }

        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }
}

public sealed class ModelOptions
{
    public string Model { get; set; } = "gpt-5.6-terra";
    public string? ReasoningEffort { get; set; } = "low";
    public int MaxOutputTokens { get; set; } = 4096;
    public decimal InputUsdPerMillion { get; set; } = 2m;
    public decimal CachedInputUsdPerMillion { get; set; } = 0.2m;
    public decimal CacheWriteUsdPerMillion { get; set; } = 2.5m;
    public decimal OutputUsdPerMillion { get; set; } = 12m;
    public int LongContextThresholdTokens { get; set; } = 272000;
    public decimal LongContextInputMultiplier { get; set; } = 2m;
    public decimal LongContextOutputMultiplier { get; set; } = 1.5m;
}

public sealed class OpenAiOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public int TimeoutSeconds { get; set; } = 300;
    public ModelOptions Remember { get; set; } = new();
    public ModelOptions Recall { get; set; } = new() { ReasoningEffort = "medium" };
    public ModelOptions Dream
    {
        get; set;
    } = new()
    {
        ReasoningEffort = "high",
        MaxOutputTokens = 8192
    };
    public ModelOptions Meditation
    {
        get; set;
    } = new()
    {
        Model = "gpt-5.6-sol",
        ReasoningEffort = "high",
        MaxOutputTokens = 16384,
        InputUsdPerMillion = 4m,
        CachedInputUsdPerMillion = 0.4m,
        CacheWriteUsdPerMillion = 5m,
        OutputUsdPerMillion = 20m
    };
    public string EmbeddingModel { get; set; } = "text-embedding-3-large";
    public int EmbeddingDimensions { get; set; } = 3072;
    public decimal EmbeddingInputUsdPerMillion { get; set; } = 0.13m;
    public string EmbeddingSpace => $"{EmbeddingModel}:{EmbeddingDimensions}";

    public ModelOptions For(CognitionRole role) => role switch
    {
        CognitionRole.Remember => Remember,
        CognitionRole.Recall => Recall,
        CognitionRole.Dream => Dream,
        CognitionRole.Meditation => Meditation,
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
