using LongJourney.Core;

namespace LongJourney.Benchmarks;

// Evaluation annotations never cross the ingestion boundary: replay accepts Sessions only.
public sealed record BenchmarkSession(string SessionId, DateTimeOffset Timestamp, string Raw);
public sealed record BenchmarkQuestion(
    string QuestionId, string QuestionType, string Question, string Answer,
    DateTimeOffset QuestionDate, IReadOnlyList<string> AnswerSessionIds,
    IReadOnlyList<BenchmarkSession> Sessions);

public sealed class BenchmarkClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; }
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

public sealed record UsageTotals(decimal SettledUsd, decimal ReservedUsd, long InputTokens, long OutputTokens, int Calls);
public sealed record CorpusMorphology(
    int Sources, int Depth0, int Depth1, int Depth2Plus,
    int PositiveRelations, int NegativeRelations, int DreamMemories, int MeditationMemories,
    IReadOnlyDictionary<string, int> Depth0PerSource);

public sealed record RecallArtifact(
    IReadOnlyList<MemoryRecord> Candidates, IReadOnlyList<MemoryRecord> Selected,
    IReadOnlyList<MemoryRecord> ProvenanceMemories,
    IReadOnlyDictionary<string, string> SourceToSession,
    long RecallInputTokens, SearchCandidateTrace? CandidateTrace = null);

public sealed record AnswerArtifact(string Hypothesis, string Model);
public sealed record JudgeArtifact(bool Correct, string Response, string Model);
public sealed record ConditionResult(
    string Condition, RecallArtifact Recall, AnswerArtifact Answer, JudgeArtifact Judge,
    CorpusMorphology Morphology, UsageTotals Usage, IReadOnlyList<RunRecord> Runs);
public sealed record QuestionResult(
    string QuestionId, string QuestionType, string Question, string ReferenceAnswer,
    IReadOnlyList<string> GoldSessions, UsageTotals SharedIngestionUsage,
    ConditionResult RememberOnly, ConditionResult FullLongJourney);

public static class BenchmarkFiles
{
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, System.Text.Json.JsonSerializer.Serialize(value, JsonDefaults.Options));
        File.Move(temporaryPath, path, true);
    }

    public static T ReadJson<T>(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InvalidDataException($"Empty benchmark artifact: {path}");
}
