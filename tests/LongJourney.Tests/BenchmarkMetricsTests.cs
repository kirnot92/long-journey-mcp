using System.Text.Json;
using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkMetricsTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2024-01-02T03:04:05Z");

    [Fact]
    public void RecallHitUsesAnyGoldThroughRecursiveParentsNotRelations()
    {
        var gold = Memory("gold", source: "source-gold");
        var other = Memory("other", source: "source-other");
        var relationOnly = Memory("relation-only", source: "source-other",
            relations: [new("gold", RelationKind.Positive, Time, 1), new("gold", RelationKind.Negative, Time, 2)]);
        var parent = Memory("parent", depth: 1, parents: ["gold", "other"]);
        var abstraction = Memory("abstraction", depth: 2, parents: ["parent"]);
        var graph = new[] { gold, other, relationOnly, parent, abstraction };
        var baseline = Condition("Remember Only", [relationOnly], [relationOnly], graph);
        var full = Condition("Full Long Journey", [abstraction], [abstraction], graph);
        var result = Result(baseline, full) with { GoldSessions = ["absent-other-gold", "session-gold"] };
        var metrics = BenchmarkMetrics.Evaluate(result);
        Assert.False(metrics.RememberOnly.GoldSourceRecallAt5);
        Assert.True(metrics.RememberOnly.CandidateRetrievalFailure);
        Assert.True(metrics.FullLongJourney.GoldSourceRecallAt5);
        Assert.True(metrics.ConsolidationGain);
        Assert.Equal(["session-gold", "session-other"], BenchmarkMetrics.SourceSessions(abstraction, full.Recall));
    }

    [Fact]
    public void SixthMemoryDoesNotCountAsRecallAtFiveAndSelectionFailureUsesCandidates()
    {
        var other = Memory("other", source: "source-other");
        var gold = Memory("gold", source: "source-gold");
        var condition = Condition("Remember Only", [other, other, other, other, other, gold], [other, gold], [other, gold]);
        var metrics = BenchmarkMetrics.Evaluate(Result(condition, condition));
        Assert.False(metrics.RememberOnly.GoldSourceRecallAt5);
        Assert.True(metrics.RememberOnly.GoldInCandidates);
        Assert.True(metrics.RememberOnly.RecallSelectionFailure);
        Assert.False(metrics.RememberOnly.CandidateRetrievalFailure);
    }

    [Fact]
    public void RegressionAndAnswerFailureRemainSeparateOverlappingOperationalFlags()
    {
        var gold = Memory("gold", source: "source-gold");
        var other = Memory("other", source: "source-other");
        var baseline = Condition("Remember Only", [gold], [gold], [gold, other], correct: false);
        var full = Condition("Full Long Journey", [other], [other, gold], [gold, other], correct: true);
        var metrics = BenchmarkMetrics.Evaluate(Result(baseline, full));
        Assert.True(metrics.ConsolidationRegression);
        Assert.True(metrics.RememberOnly.AnswerModelFailure);
        Assert.True(metrics.FullLongJourney.RecallSelectionFailure);
        Assert.False(metrics.FullLongJourney.AnswerModelFailure);
    }

    [Fact]
    public void IncompleteProvenanceDoesNotSilentlyScoreAMiss()
    {
        var abstraction = Memory("abstract", depth: 1, parents: ["missing"]);
        var condition = Condition("Full Long Journey", [abstraction], [abstraction], [abstraction]);
        Assert.Throws<InvalidDataException>(() => BenchmarkMetrics.Evaluate(Result(condition, condition)));
    }

    [Fact]
    public void ProvenanceCyclesAreRejected()
    {
        var a = Memory("a", depth: 1, parents: ["b"]);
        var b = Memory("b", depth: 2, parents: ["a"]);
        var condition = Condition("Full Long Journey", [b], [b], [a, b]);
        Assert.Throws<InvalidDataException>(() => BenchmarkMetrics.Evaluate(Result(condition, condition)));
    }

    [Fact]
    public void CostsAttributeSharedIngestionToEachConditionButPayItOnce()
    {
        var gold = Memory("gold", source: "source-gold");
        var baseline = Condition("Remember Only", [gold], [gold], [gold]) with { Usage = new(2m, 0.2m, 0, 0, 1) };
        var full = Condition("Full Long Journey", [gold], [gold], [gold]) with { Usage = new(3m, 0.3m, 0, 0, 1) };
        var result = Result(baseline, full) with { SharedIngestionUsage = new(1m, 0.1m, 0, 0, 1) };
        var summary = BenchmarkMetrics.Summarize([result], 500);
        Assert.Equal(3m, summary.RememberOnly.AttributedApiCostUsd);
        Assert.Equal(4m, summary.FullLongJourney.AttributedApiCostUsd);
        Assert.Equal(6m, summary.ActualApiCostUsd);
        Assert.Equal(0.6m, summary.ActualReservedUsd);
        Assert.Equal(0.3m, summary.RememberOnly.AttributedReservedUsd);
        Assert.Equal(0.4m, summary.FullLongJourney.AttributedReservedUsd);
        Assert.Equal(123m, summary.RememberOnly.AverageRecallInputTokens);
        Assert.False(summary.Complete);
        Assert.Null(summary.MeetsPrimaryThreshold);
    }

    [Fact]
    public void AbstentionsAndMissingGoldStayInPrimaryDenominator()
    {
        var gold = Memory("gold", source: "source-gold");
        var condition = Condition("Remember Only", [gold], [gold], [gold]);
        var normal = Result(condition, condition);
        var abstention = normal with { QuestionId = "qid_abs", GoldSessions = [] };
        var summary = BenchmarkMetrics.Summarize([normal, abstention], 500);
        Assert.Equal(2, summary.RememberOnly.Questions);
        Assert.Equal(0.5m, summary.RememberOnly.GoldSourceRecallAt5);
        Assert.Equal(1, summary.AbstentionQuestions);
        Assert.Equal(1, summary.QuestionsWithoutGoldSessions);
        Assert.Equal(1m, summary.RememberOnly.AnswerAccuracy);
    }

    [Fact]
    public void ThresholdsAreInclusiveAndApplyOnlyToCompleteFiveHundred()
    {
        var results = FiveHundredResults();
        var summary = BenchmarkMetrics.Summarize(results, 500);
        Assert.True(summary.Complete);
        Assert.True(summary.MeetsPrimaryThreshold);
        Assert.Equal(3m, summary.RecallDifferencePercentagePoints);
        Assert.Contains(summary.Categories, category => category.Category == "temporal-reasoning" &&
            category.DifferencePercentagePoints == -5m && category.RequiresRegressionInvestigation);
        var partial = BenchmarkMetrics.Summarize(results.GetRange(100, 400), 500);
        Assert.False(partial.Complete);
        Assert.Null(partial.MeetsPrimaryThreshold);
        results[0] = results[0] with { SharedIngestionUsage = new(0, 1m, 0, 0, 1) };
        var unsettled = BenchmarkMetrics.Summarize(results, 500);
        Assert.True(unsettled.Complete);
        Assert.True(unsettled.MeetsPrimaryThreshold);
        Assert.Equal(1m, unsettled.ActualReservedUsd);
    }

    [Fact]
    public void ReportIncludesActualMemoriesProvenanceRelationsAndRequiredTables()
    {
        var directory = Path.Combine(Path.GetTempPath(), "long-journey-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gold = Memory("gold", source: "source-gold");
            var other = Memory("other", source: "source-other");
            var abstraction = Memory("abstract", depth: 1, parents: ["gold"],
                relations: [new("other", RelationKind.Negative, Time, 1)]);
            var graph = new[] { gold, other, abstraction };
            var baseline = Condition("Remember Only", [other], [other], graph);
            var full = Condition("Full Long Journey", [abstraction], [abstraction], graph);
            full = full with { Recall = full.Recall with { CandidateTrace = new(["other", "abstract"], ["abstract"], ["abstract"]) } };
            var gain = Result(baseline, full);
            var regression = Result(full, baseline) with { QuestionId = "qid-regression" };
            BenchmarkReport.Write(directory, [gain, regression], 500);
            var report = File.ReadAllText(Path.Combine(directory, "report.md"));
            Assert.Contains("INCOMPLETE / INCONCLUSIVE", report);
            Assert.Contains("Primary criterion: NOT ASSESSED", report);
            Assert.Contains("## Summary", report);
            Assert.Contains("## Memory Morphology", report);
            Assert.Contains("## Failure Analysis", report);
            Assert.Contains("derived_from [gold]", report);
            Assert.Contains("source_ref source-gold", report);
            Assert.Contains("session session-gold", report);
            Assert.Contains("Content of abstract", report);
            Assert.Contains("Negative → other", report);
            Assert.Contains("### Newly successful retrieval cases", report);
            Assert.Contains("### Consolidation regression cases", report);
            Assert.Contains("This bucket therefore does not establish that the answer model alone caused the error.", report);
            Assert.Contains("BM25 (2): 1: other; 2: abstract [gold]", report);
            Assert.Contains("execution-status.json", report);
            using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")));
            Assert.False(metrics.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(JsonValueKind.Null, metrics.RootElement.GetProperty("meets_primary_threshold").ValueKind);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static List<QuestionResult> FiveHundredResults()
    {
        var gold = Memory("gold", source: "source-gold");
        var other = Memory("other", source: "source-other");
        var hit = Condition("hit", [gold], [gold], [gold, other]);
        var miss = Condition("miss", [other], [other], [gold, other]);
        var results = new List<QuestionResult>(500);
        for (var index = 0; index < 500; index++)
        {
            var temporal = index < 100;
            var baselineHit = temporal ? index < 10 : index < 120;
            var fullHit = temporal ? index < 5 : index < 140;
            results.Add(Result(baselineHit ? hit : miss, fullHit ? hit : miss) with
            {
                QuestionId = "qid-" + index,
                QuestionType = temporal ? "temporal-reasoning" : "multi-session"
            });
        }
        return results;
    }

    private static MemoryRecord Memory(string id, int depth = 0, string? source = null,
        IReadOnlyList<string>? parents = null, IReadOnlyList<MemoryRelation>? relations = null) =>
        new(id, depth, "Content of " + id, source, parents ?? [], relations ?? [], Time, 0, null, "test", 1, 1);

    private static ConditionResult Condition(string name, IReadOnlyList<MemoryRecord> selected,
        IReadOnlyList<MemoryRecord> candidates, IReadOnlyList<MemoryRecord> graph, bool correct = true) =>
        new(name, new(candidates, selected, graph,
                new Dictionary<string, string> { ["source-gold"] = "session-gold", ["source-other"] = "session-other" }, 123),
            new("An answer.", "test"), new(correct, correct ? "yes" : "no", "test"),
            new(2, 2, 0, 0, 0, 0, 0, 0, new Dictionary<string, int> { ["source-gold"] = 1, ["source-other"] = 1 }),
            new(0, 0, 0, 0, 0), []);

    private static QuestionResult Result(ConditionResult baseline, ConditionResult full) =>
        new("qid", "multi-session", "Question?", "Reference answer.", ["session-gold"], new(0, 0, 0, 0, 0), baseline, full);
}
