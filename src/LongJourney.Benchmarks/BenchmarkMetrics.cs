using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record RetrievalMetrics(
    bool GoldSourceRecallAt5, bool GoldInCandidates,
    bool CandidateRetrievalFailure, bool RecallSelectionFailure, bool AnswerModelFailure);
public sealed record QuestionMetrics(
    string QuestionId, string QuestionType, bool HasGoldSessions,
    RetrievalMetrics RememberOnly, RetrievalMetrics FullLongJourney,
    bool ConsolidationGain, bool ConsolidationRegression);
public sealed record MorphologyTotals(
    long Sources, long Depth0, long Depth1, long Depth2Plus, long PositiveRelations,
    long NegativeRelations, long DreamMemories, long MeditationMemories, decimal? Depth0PerSource);
public sealed record ConditionMetrics(
    int Questions, int GoldSourceRecallSuccesses, decimal? GoldSourceRecallAt5,
    int CorrectAnswers, decimal? AnswerAccuracy,
    decimal AttributedApiCostUsd, decimal AttributedReservedUsd,
    decimal ConditionOnlyApiCostUsd, long RecallInputTokens, decimal? AverageRecallInputTokens,
    int CandidateRetrievalFailures, int RecallSelectionFailures, int AnswerModelFailures,
    MorphologyTotals Morphology);
public sealed record CategoryMetrics(
    string Category, int Questions, decimal RememberOnlyRecall, decimal FullLongJourneyRecall,
    decimal DifferencePercentagePoints, bool RequiresRegressionInvestigation);
public sealed record BenchmarkSummary(
    int ExpectedQuestions, int CompletedQuestions, bool Complete, bool? MeetsPrimaryThreshold,
    int QuestionsWithoutGoldSessions, int AbstentionQuestions,
    decimal? RecallDifferencePercentagePoints, decimal SharedIngestionCostUsd,
    decimal ActualApiCostUsd, decimal ActualReservedUsd,
    ConditionMetrics RememberOnly, ConditionMetrics FullLongJourney,
    int ConsolidationGains, int ConsolidationRegressions,
    IReadOnlyList<CategoryMetrics> Categories, IReadOnlyList<QuestionMetrics> Questions);

public static class BenchmarkMetrics
{
    public static QuestionMetrics Evaluate(QuestionResult result)
    {
        var gold = new HashSet<string>(result.GoldSessions, StringComparer.Ordinal);
        var remember = EvaluateCondition(result.RememberOnly, gold);
        var full = EvaluateCondition(result.FullLongJourney, gold);
        return new QuestionMetrics(result.QuestionId, result.QuestionType, gold.Count > 0, remember, full,
            !remember.GoldSourceRecallAt5 && full.GoldSourceRecallAt5,
            remember.GoldSourceRecallAt5 && !full.GoldSourceRecallAt5);
    }

    public static IReadOnlyList<string> SourceSessions(MemoryRecord memory, RecallArtifact recall)
    {
        var ancestry = new SourceAncestry(recall);
        var sessions = new List<string>(ancestry.Sessions(memory.Id));
        sessions.Sort(StringComparer.Ordinal);
        return sessions;
    }

    public static BenchmarkSummary Summarize(IReadOnlyList<QuestionResult> results, int expectedQuestions)
    {
        if (expectedQuestions < 1 || results.Count > expectedQuestions)
        {
            throw new InputException("Invalid benchmark report question count.");
        }
        var questionMetrics = new List<QuestionMetrics>(results.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var categories = new SortedDictionary<string, List<QuestionMetrics>>(StringComparer.Ordinal);
        decimal sharedCost = 0;
        decimal actualCost = 0;
        decimal actualReserved = 0;
        var gains = 0;
        var regressions = 0;
        var noGold = 0;
        var abstentions = 0;
        foreach (var result in results)
        {
            if (!seen.Add(result.QuestionId))
            {
                throw new InvalidDataException("The benchmark report contains a duplicate question.");
            }
            var metric = Evaluate(result);
            questionMetrics.Add(metric);
            if (!categories.TryGetValue(result.QuestionType, out var members))
            {
                members = [];
                categories.Add(result.QuestionType, members);
            }
            members.Add(metric);
            sharedCost += result.SharedIngestionUsage.SettledUsd;
            actualCost += result.SharedIngestionUsage.SettledUsd + result.RememberOnly.Usage.SettledUsd + result.FullLongJourney.Usage.SettledUsd;
            actualReserved += result.SharedIngestionUsage.ReservedUsd + result.RememberOnly.Usage.ReservedUsd + result.FullLongJourney.Usage.ReservedUsd;
            gains += metric.ConsolidationGain ? 1 : 0;
            regressions += metric.ConsolidationRegression ? 1 : 0;
            noGold += metric.HasGoldSessions ? 0 : 1;
            abstentions += result.QuestionId.Contains("_abs", StringComparison.Ordinal) ? 1 : 0;
        }
        var categoryMetrics = new List<CategoryMetrics>(categories.Count);
        foreach (var (category, members) in categories)
        {
            var rememberHits = 0;
            var fullHits = 0;
            foreach (var member in members)
            {
                rememberHits += member.RememberOnly.GoldSourceRecallAt5 ? 1 : 0;
                fullHits += member.FullLongJourney.GoldSourceRecallAt5 ? 1 : 0;
            }
            var difference = (fullHits - rememberHits) * 100m / members.Count;
            categoryMetrics.Add(new CategoryMetrics(category, members.Count, rememberHits / (decimal)members.Count,
                fullHits / (decimal)members.Count, difference, difference <= -5m));
        }
        var rememberSummary = SummarizeCondition(results, questionMetrics, full: false);
        var fullSummary = SummarizeCondition(results, questionMetrics, full: true);
        var recallDifference = (fullSummary.GoldSourceRecallAt5 - rememberSummary.GoldSourceRecallAt5) * 100m;
        // A subset never receives the first experiment's all-500 success verdict.
        var complete = expectedQuestions == 500 && results.Count == 500;
        return new BenchmarkSummary(expectedQuestions, results.Count, complete,
            complete ? recallDifference >= 3m : null, noGold, abstentions, recallDifference, sharedCost,
            actualCost, actualReserved, rememberSummary, fullSummary, gains, regressions, categoryMetrics, questionMetrics);
    }

    private static RetrievalMetrics EvaluateCondition(ConditionResult condition, HashSet<string> gold)
    {
        var ancestry = new SourceAncestry(condition.Recall);
        var selectedHit = ContainsGold(condition.Recall.Selected, Math.Min(5, condition.Recall.Selected.Count), ancestry, gold);
        var candidateHit = ContainsGold(condition.Recall.Candidates, condition.Recall.Candidates.Count, ancestry, gold);
        // Source ancestry does not guarantee that a selected abstraction contains the answer text.
        // This flag is the proposal's operational QA failure bucket, not a causal diagnosis.
        return new RetrievalMetrics(selectedHit, candidateHit,
            !selectedHit && !candidateHit, !selectedHit && candidateHit, selectedHit && !condition.Judge.Correct);
    }

    private static bool ContainsGold(IReadOnlyList<MemoryRecord> memories, int count, SourceAncestry ancestry, HashSet<string> gold)
    {
        var found = false;
        for (var index = 0; index < count; index++)
        {
            foreach (var session in ancestry.Sessions(memories[index].Id))
            {
                if (gold.Contains(session))
                {
                    found = true;
                }
            }
        }
        return found;
    }

    private static ConditionMetrics SummarizeCondition(IReadOnlyList<QuestionResult> results,
        IReadOnlyList<QuestionMetrics> metrics, bool full)
    {
        var hits = 0;
        var correct = 0;
        var candidateFailures = 0;
        var selectionFailures = 0;
        var answerFailures = 0;
        decimal attributedCost = 0;
        decimal conditionCost = 0;
        decimal reserved = 0;
        long recallTokens = 0;
        long sources = 0;
        long depth0 = 0;
        long depth1 = 0;
        long depth2Plus = 0;
        long positive = 0;
        long negative = 0;
        long dream = 0;
        long meditation = 0;
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            var condition = full ? result.FullLongJourney : result.RememberOnly;
            var metric = full ? metrics[index].FullLongJourney : metrics[index].RememberOnly;
            hits += metric.GoldSourceRecallAt5 ? 1 : 0;
            correct += condition.Judge.Correct ? 1 : 0;
            candidateFailures += metric.CandidateRetrievalFailure ? 1 : 0;
            selectionFailures += metric.RecallSelectionFailure ? 1 : 0;
            answerFailures += metric.AnswerModelFailure ? 1 : 0;
            conditionCost += condition.Usage.SettledUsd;
            attributedCost += result.SharedIngestionUsage.SettledUsd + condition.Usage.SettledUsd;
            reserved += result.SharedIngestionUsage.ReservedUsd + condition.Usage.ReservedUsd;
            recallTokens += condition.Recall.RecallInputTokens;
            var morphology = condition.Morphology;
            sources += morphology.Sources;
            depth0 += morphology.Depth0;
            depth1 += morphology.Depth1;
            depth2Plus += morphology.Depth2Plus;
            positive += morphology.PositiveRelations;
            negative += morphology.NegativeRelations;
            dream += morphology.DreamMemories;
            meditation += morphology.MeditationMemories;
        }
        return new ConditionMetrics(results.Count, hits, Rate(hits, results.Count), correct, Rate(correct, results.Count),
            attributedCost, reserved, conditionCost, recallTokens, Rate(recallTokens, results.Count),
            candidateFailures, selectionFailures, answerFailures,
            new MorphologyTotals(sources, depth0, depth1, depth2Plus, positive, negative, dream, meditation,
                Rate(depth0, sources)));
    }

    private static decimal? Rate(long numerator, long denominator) => denominator == 0 ? null : numerator / (decimal)denominator;

    private sealed class SourceAncestry
    {
        private readonly Dictionary<string, MemoryRecord> _memories = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _sourceToSession;
        private readonly Dictionary<string, HashSet<string>> _sessions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        public SourceAncestry(RecallArtifact recall)
        {
            _sourceToSession = recall.SourceToSession;
            foreach (var memory in recall.ProvenanceMemories)
            {
                _memories.Add(memory.Id, memory);
            }
            foreach (var memory in recall.Candidates)
            {
                _memories.TryAdd(memory.Id, memory);
            }
            foreach (var memory in recall.Selected)
            {
                _memories.TryAdd(memory.Id, memory);
            }
        }

        public HashSet<string> Sessions(string memoryId)
        {
            if (_sessions.TryGetValue(memoryId, out var cached))
            {
                return cached;
            }
            if (!_memories.TryGetValue(memoryId, out var memory))
            {
                throw new InvalidDataException("A benchmark provenance parent is missing from the recorded graph.");
            }
            if (!_visiting.Add(memoryId))
            {
                throw new InvalidDataException("A benchmark provenance graph contains a cycle.");
            }
            var sources = new HashSet<string>(StringComparer.Ordinal);
            if (memory.Depth == 0)
            {
                if (memory.SourceRef is null || !_sourceToSession.TryGetValue(memory.SourceRef, out var session))
                {
                    throw new InvalidDataException("A benchmark source has no recorded dataset session mapping.");
                }
                sources.Add(session);
            }
            else
            {
                if (memory.DerivedFrom.Count == 0)
                {
                    throw new InvalidDataException("A benchmark abstraction has no recorded provenance parents.");
                }
                // Outgoing positive/negative relations are context, never ancestry evidence.
                foreach (var parent in memory.DerivedFrom)
                {
                    sources.UnionWith(Sessions(parent));
                }
            }
            _visiting.Remove(memoryId);
            _sessions.Add(memoryId, sources);
            return sources;
        }
    }
}
