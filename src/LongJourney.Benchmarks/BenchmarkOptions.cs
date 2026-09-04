using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Benchmarks;

public enum BenchmarkVariant
{
    FullHistory,
    Remember,
    Dream,
    Relations,
    Meditation
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BenchmarkOptions
{
    public const string ProtocolVersion = "longmemeval-v1";
    public string DatasetPath { get; set; } = "";
    public string Split { get; set; } = "oracle";
    public string OutputDirectory { get; set; } = "";
    public IReadOnlyList<string> QuestionIds { get; set; } = [];
    public int Limit { get; set; } = 1;
    public IReadOnlyList<BenchmarkVariant> Variants { get; set; } =
        [BenchmarkVariant.FullHistory, BenchmarkVariant.Remember, BenchmarkVariant.Dream,
         BenchmarkVariant.Relations, BenchmarkVariant.Meditation];
    public decimal ExperimentBudgetUsd { get; set; } = 10m;
    public decimal MeditationBudgetUsd { get; set; } = 5m;
    public int MaxRawCharacters { get; set; } = 1000;
    public int RetrievalContextCharacters { get; set; } = 32_000;
    public int FullHistoryContextCharacters { get; set; } = 1_000_000;
    public ModelOptions AnswerModel { get; set; } = new() { ReasoningEffort = "medium" };
    public ModelOptions JudgeModel { get; set; } = new() { ReasoningEffort = "medium" };
    public OpenAiOptions OpenAI { get; set; } = new();

    public static BenchmarkOptions Read(string configPath)
    {
        var absoluteConfigPath = Path.GetFullPath(configPath);
        var options = JsonSerializer.Deserialize<BenchmarkOptions>(
            File.ReadAllText(absoluteConfigPath), JsonDefaults.Options)
            ?? throw new InputException("Benchmark configuration must be an object.");
        options.Validate();
        var baseDirectory = Path.GetDirectoryName(absoluteConfigPath)!;
        options.DatasetPath = Path.GetFullPath(options.DatasetPath, baseDirectory);
        options.OutputDirectory = Path.GetFullPath(options.OutputDirectory, baseDirectory);
        return options;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DatasetPath) || string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new InputException("DatasetPath and OutputDirectory are required.");
        }
        if (Split is not ("oracle" or "s" or "m"))
        {
            throw new InputException("Split must be oracle, s or m.");
        }
        if (Limit < 1 || ExperimentBudgetUsd <= 0 || MeditationBudgetUsd <= 0 ||
            MaxRawCharacters < 100 || MaxRawCharacters > 1000 ||
            RetrievalContextCharacters < 4000 || FullHistoryContextCharacters < 4000)
        {
            throw new InputException("Invalid benchmark limits or budgets.");
        }
        if (Variants is null || Variants.Count == 0 || QuestionIds is null)
        {
            throw new InputException("Variants must be nonempty and QuestionIds must be a list.");
        }
        var seen = new HashSet<BenchmarkVariant>();
        foreach (var variant in Variants)
        {
            if (!Enum.IsDefined(variant) || !seen.Add(variant))
            {
                throw new InputException("Variants contain an unknown or duplicate entry.");
            }
        }
        OpenAiCognition.ValidateOptions(OpenAI);
        OpenAiPricing.ValidateModel(AnswerModel, "benchmark_answer");
        OpenAiPricing.ValidateModel(JudgeModel, "benchmark_judge");
    }

    public EngineOptions CreateEngineOptions(string corpusDirectory, BenchmarkVariant variant)
    {
        return new EngineOptions
        {
            DataDirectory = corpusDirectory,
            RootBase = 3,
            MaxRawCharacters = 4000,
            MaxObservations = 1,
            TimeZoneId = "UTC",
            SchedulerEnabled = variant is not (BenchmarkVariant.FullHistory or BenchmarkVariant.Remember),
            DreamAssimilationEnabled = variant != BenchmarkVariant.Dream,
            MeditationBudgetUsd = variant == BenchmarkVariant.Meditation ? MeditationBudgetUsd : null
        };
    }

    public IReadOnlyList<BenchmarkCase> SelectCases(BenchmarkDataset dataset)
    {
        var selected = new List<BenchmarkCase>();
        var requested = new HashSet<string>(QuestionIds, StringComparer.Ordinal);
        if (requested.Count != QuestionIds.Count)
        {
            throw new InputException("QuestionIds contains duplicates.");
        }
        foreach (var item in dataset.Cases)
        {
            if (requested.Count == 0 || requested.Contains(item.Id))
            {
                selected.Add(item);
            }
        }
        if (requested.Count != 0 && selected.Count != requested.Count)
        {
            throw new InputException("One or more requested question IDs are absent from the dataset.");
        }
        selected.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        if (requested.Count == 0 && selected.Count > Limit)
        {
            selected.RemoveRange(Limit, selected.Count - Limit);
        }
        if (selected.Count == 0)
        {
            throw new InputException("No benchmark questions selected.");
        }
        return selected;
    }

    public string Fingerprint(string datasetHash)
    {
        var serialized = JsonSerializer.Serialize(new
        {
            protocol = ProtocolVersion,
            prompts = BenchmarkLanguageModel.PromptVersion,
            datasetHash,
            implementation = new[]
            {
                typeof(BenchmarkOptions).Assembly.ManifestModule.ModuleVersionId,
                typeof(MemoryEngine).Assembly.ManifestModule.ModuleVersionId,
                typeof(OpenAiCognition).Assembly.ManifestModule.ModuleVersionId
            },
            options = this
        }, JsonDefaults.Options);
        return Hash(serialized);
    }

    public static string CaseDirectoryName(string questionId) => Hash(questionId);

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
