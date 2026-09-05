using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record DreamMicroEvidenceMemory(string Id, string Content, DateTimeOffset CreatedAt);
public sealed record DreamMicroEvidenceJudgment(string MemoryId, bool AnswerBearing, string Reason);
public sealed record DreamMicroEvidenceArtifact(
    string Model, IReadOnlyList<DreamMicroEvidenceMemory> OfferedDepth0,
    IReadOnlyList<DreamMicroEvidenceJudgment> Judgments, bool IsDatasetAbstention, string Note);
public sealed record DreamMicroMemoryMatch(string MemoryId, IReadOnlyList<string> GoldDepth0Ids);
public sealed record DreamMicroRetrievalMetrics(
    bool HitAt5, bool GoldInCandidates, decimal SelectedEvidenceCoverage, decimal CandidateEvidenceCoverage,
    bool AllEvidenceAt5, bool AllEvidenceInCandidates, bool RememberExtractionFailure,
    bool CandidateRetrievalFailure, bool RecallSelectionFailure,
    IReadOnlyList<DreamMicroMemoryMatch> SelectedMatches, IReadOnlyList<DreamMicroMemoryMatch> CandidateMatches);
public sealed record DreamMicroPruning(
    int ConsolidationWork, int ImpossibleBeforeLlm, int ExactDuplicateNeighborhood,
    int ActualLlmCalls, int ZeroAbstractionCalls, int CreatedAbstractions);
public sealed record DreamMicroConditionResult(
    string Condition, RecallArtifact Recall, DreamMicroRetrievalMetrics Metrics,
    CorpusMorphology Morphology, UsageTotals Usage, DreamMicroPruning Pruning);
public sealed record DreamMicroQuestionResult(
    string QuestionId, string QuestionType, string Question, string ReferenceAnswer,
    IReadOnlyList<string> GoldSessions, DreamMicroEvidenceArtifact Evidence, UsageTotals SharedIngestionUsage,
    DreamMicroConditionResult RememberOnly, DreamMicroConditionResult RememberPlusDream);
