using System.Globalization;
using System.Text;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public static class BenchmarkReport
{
    public static void Write(string outputDirectory, IReadOnlyList<QuestionResult> results, int expectedQuestions)
    {
        var orderedResults = new List<QuestionResult>(results);
        orderedResults.Sort((left, right) => StringComparer.Ordinal.Compare(left.QuestionId, right.QuestionId));
        var summary = BenchmarkMetrics.Summarize(orderedResults, expectedQuestions);
        Directory.CreateDirectory(outputDirectory);
        BenchmarkFiles.WriteJson(Path.Combine(outputDirectory, "metrics.json"), summary);
        var report = new StringBuilder();
        report.AppendLine("# LongMemEval-S consolidation benchmark");
        report.AppendLine();
        report.AppendLine(summary.Complete
            ? "Status: COMPLETE — all 500 paired questions were evaluated."
            : $"Status: INCOMPLETE / INCONCLUSIVE — {summary.CompletedQuestions} of 500 paired questions are available; no final success verdict.");
        report.AppendLine();
        report.AppendLine("Primary denominator: all 500 LongMemEval-S questions, including abstention questions. " +
            "Partial tables use only the completed paired cases and must not be interpreted as the full benchmark result. " +
            "Gold Source Recall@5 is a per-question ANY-gold hit through source_ref or recursive derived_from ancestry. Relations do not count as provenance.");
        report.AppendLine();
        report.AppendLine($"Completed cases include {summary.AbstentionQuestions} abstention questions and " +
            $"{summary.QuestionsWithoutGoldSessions} questions with no gold session IDs. No question was excluded on either basis.");
        report.AppendLine();
        WriteSummary(report, summary);
        WriteMorphology(report, summary);
        WriteFailures(report, summary);
        WriteExamples(report, orderedResults, summary.Questions);

        var path = Path.Combine(outputDirectory, "report.md");
        File.WriteAllText(path + ".tmp", report.ToString());
        File.Move(path + ".tmp", path, true);
    }

    private static void WriteSummary(StringBuilder report, BenchmarkSummary summary)
    {
        var remember = summary.RememberOnly;
        var full = summary.FullLongJourney;
        report.AppendLine("## Summary");
        report.AppendLine();
        report.AppendLine("| Metric | Remember Only | Full Long Journey | Difference |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine($"| Gold Source Recall@5 | {Percent(remember.GoldSourceRecallAt5)} ({remember.GoldSourceRecallSuccesses}/{remember.Questions}) | " +
            $"{Percent(full.GoldSourceRecallAt5)} ({full.GoldSourceRecallSuccesses}/{full.Questions}) | {Points(summary.RecallDifferencePercentagePoints)} |");
        report.AppendLine($"| Answer Accuracy | {Percent(remember.AnswerAccuracy)} ({remember.CorrectAnswers}/{remember.Questions}) | " +
            $"{Percent(full.AnswerAccuracy)} ({full.CorrectAnswers}/{full.Questions}) | {Points((full.AnswerAccuracy - remember.AnswerAccuracy) * 100m)} |");
        report.AppendLine($"| Total API Cost (attributed, USD) | {Usd(remember.AttributedApiCostUsd)} | {Usd(full.AttributedApiCostUsd)} | {Usd(full.AttributedApiCostUsd - remember.AttributedApiCostUsd)} |");
        report.AppendLine($"| Avg. Recall Context Tokens | {Number(remember.AverageRecallInputTokens)} | {Number(full.AverageRecallInputTokens)} | {Number(full.AverageRecallInputTokens - remember.AverageRecallInputTokens)} |");
        report.AppendLine();
        report.AppendLine($"Shared observation ingestion cost: {Usd(summary.SharedIngestionCostUsd)}. " +
            $"Actual API cost paid once across the two conditions: {Usd(summary.ActualApiCostUsd)}. " +
            "Each attributed condition total includes the same shared ingestion cost; adding those columns double-counts ingestion. " +
            "Costs include ingestion, embeddings, consolidation where applicable, recall, answering, and judging. " +
            "These figures cover completed paired cases. [execution-status.json](execution-status.json) records physical settled/reserved costs " +
            "and API calls including unfinished or failed cases; unfinished stage costs remain in their execution ledgers.");
        report.AppendLine();
        report.AppendLine($"Unsettled reservations (actual, counted once): {Usd(summary.ActualReservedUsd)}. " +
            $"Attributed reservations: Remember Only {Usd(remember.AttributedReservedUsd)}, Full Long Journey {Usd(full.AttributedReservedUsd)}. " +
            "Reservations are conservative exposure bounds, not confirmed charges. They do not change the retrieval-quality verdict.");
        report.AppendLine();
        report.AppendLine("Avg. Recall Context Tokens uses API-reported input tokens of the contextual recall-selection call, " +
            "including its complete input and cached tokens, not a text-length estimate or answer/judge tokens.");
        report.AppendLine();
        if (summary.MeetsPrimaryThreshold is not null)
        {
            report.AppendLine(summary.MeetsPrimaryThreshold.Value
                ? "Primary criterion: MET — the Full Long Journey gain is at least +3 percentage points."
                : "Primary criterion: NOT MET — the Full Long Journey gain is below +3 percentage points.");
        }
        else
        {
            report.AppendLine("Primary criterion: NOT ASSESSED. The predeclared +3 percentage-point threshold applies to the complete 500-question experiment.");
        }
        report.AppendLine();
        report.AppendLine("### Question categories");
        report.AppendLine();
        report.AppendLine("| Category | Questions | Remember Only Recall@5 | Full Long Journey Recall@5 | Difference | Investigate decline ≥5 pp |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | --- |");
        foreach (var category in summary.Categories)
        {
            report.AppendLine($"| {Cell(category.Category)} | {category.Questions} | {Percent(category.RememberOnlyRecall)} | " +
                $"{Percent(category.FullLongJourneyRecall)} | {Points(category.DifferencePercentagePoints)} | " +
                $"{(category.RequiresRegressionInvestigation ? "YES" : "No")} |");
        }
        report.AppendLine();
        report.AppendLine("Every available official question category is reported. A decline of exactly 5 percentage points triggers investigation. " +
            "A primary-criterion gain does not resolve category regressions; flagged categories require the case-level analysis below.");
        report.AppendLine();
        report.AppendLine($"Answer model: `{BenchmarkLanguageModel.AnswerModelName}`, medium reasoning, the same fixed prompt and at most five selected memories per condition. " +
            $"Judge: `{BenchmarkLanguageModel.JudgeModelName}`, official LongMemEval category/abstention prompts, Chat Completions, " +
            "temperature 0, max_tokens 10, and the official case-insensitive substring `yes` scoring rule. " +
            "The answer model receives no gold annotations, source text, or expanded ancestor content.");
        report.AppendLine();
    }

    private static void WriteMorphology(StringBuilder report, BenchmarkSummary summary)
    {
        var remember = summary.RememberOnly.Morphology;
        var full = summary.FullLongJourney.Morphology;
        report.AppendLine("## Memory Morphology");
        report.AppendLine();
        report.AppendLine("| Metric | Remember Only | Full Long Journey |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| Depth 0 Memories | {remember.Depth0} | {full.Depth0} |");
        report.AppendLine($"| Depth 1 Memories | {remember.Depth1} | {full.Depth1} |");
        report.AppendLine($"| Depth 2+ Memories | {remember.Depth2Plus} | {full.Depth2Plus} |");
        report.AppendLine($"| Positive Relations | {remember.PositiveRelations} | {full.PositiveRelations} |");
        report.AppendLine($"| Negative Relations | {remember.NegativeRelations} | {full.NegativeRelations} |");
        report.AppendLine($"| Sources | {remember.Sources} | {full.Sources} |");
        report.AppendLine($"| Mean Depth 0 Memories per Source | {Number(remember.Depth0PerSource)} | {Number(full.Depth0PerSource)} |");
        report.AppendLine($"| Memories Created by Dream | {remember.DreamMemories} | {full.DreamMemories} |");
        report.AppendLine($"| Memories Created by Meditation | {remember.MeditationMemories} | {full.MeditationMemories} |");
        report.AppendLine();
        report.AppendLine("Counts sum the separate history corpora for completed questions. Source-weighted mean = total depth-0 memories / total sources. " +
            "Per-source counts, individual consolidation runs, and full graph/provenance records are retained in each case artifact. " +
            "Dream/Meditation memory totals count actual creations, not proposed or rejected abstractions.");
        report.AppendLine();
    }

    private static void WriteFailures(StringBuilder report, BenchmarkSummary summary)
    {
        var remember = summary.RememberOnly;
        var full = summary.FullLongJourney;
        report.AppendLine("## Failure Analysis");
        report.AppendLine();
        report.AppendLine("| Failure type | Remember Only | Full Long Journey | Count |");
        report.AppendLine("| --- | ---: | ---: | ---: |");
        report.AppendLine($"| Candidate retrieval failure | {remember.CandidateRetrievalFailures} | {full.CandidateRetrievalFailures} | {remember.CandidateRetrievalFailures + full.CandidateRetrievalFailures} condition-cases |");
        report.AppendLine($"| Recall selection failure | {remember.RecallSelectionFailures} | {full.RecallSelectionFailures} | {remember.RecallSelectionFailures + full.RecallSelectionFailures} condition-cases |");
        report.AppendLine($"| Consolidation regression | — | — | {summary.ConsolidationRegressions} paired questions |");
        report.AppendLine($"| Answer-model failure | {remember.AnswerModelFailures} | {full.AnswerModelFailures} | {remember.AnswerModelFailures + full.AnswerModelFailures} condition-cases |");
        report.AppendLine();
        report.AppendLine("Candidate retrieval failure means the selected top five miss all gold sessions and no fused BM25/embedding candidate has gold ancestry. " +
            "Recall selection failure means a gold-connected candidate existed but the selected top five missed it. " +
            "Consolidation regression means Remember Only retrieved gold but Full Long Journey did not. " +
            "Answer-model failure is operationally defined as a gold ancestry hit with an incorrect judged answer; a selected abstraction can omit the needed fact even when its ancestors include gold. " +
            "This bucket therefore does not establish that the answer model alone caused the error.");
        report.AppendLine();
        report.AppendLine("Counts can overlap: a paired consolidation regression also belongs to one Full Long Journey retrieval-failure bucket, " +
            "and a question may fail in both conditions. The rows must not be summed into a unique failed-question total. " +
            "Empty gold labels, if present, remain in the denominator and cannot produce a retrieval hit; their retrieval failure flags are mechanical, not evidence of a search defect.");
        report.AppendLine();
        report.AppendLine($"Full Long Journey-only retrieval successes: {summary.ConsolidationGains}. " +
            $"Remember Only-only retrieval successes (regressions): {summary.ConsolidationRegressions}.");
        report.AppendLine();
        report.AppendLine("The cases below show actual selected/candidate memories, derived_from paths, and outgoing relations. " +
            "They are descriptive evidence from the two declared conditions; they do not isolate a causal contribution of relations or abstraction without another experiment. " +
            "Examples are the first three cases of each type in stable ordinal question-ID order, avoiding post-hoc favorable case selection.");
        report.AppendLine();
    }

    private static void WriteExamples(StringBuilder report, IReadOnlyList<QuestionResult> results, IReadOnlyList<QuestionMetrics> metrics)
    {
        WriteExampleGroup(report, results, metrics, gains: true);
        WriteExampleGroup(report, results, metrics, gains: false);
    }

    private static void WriteExampleGroup(StringBuilder report, IReadOnlyList<QuestionResult> results,
        IReadOnlyList<QuestionMetrics> metrics, bool gains)
    {
        report.AppendLine(gains ? "### Newly successful retrieval cases" : "### Consolidation regression cases");
        report.AppendLine();
        var shown = 0;
        for (var index = 0; index < results.Count && shown < 3; index++)
        {
            var metric = metrics[index];
            if (!(gains ? metric.ConsolidationGain : metric.ConsolidationRegression))
            {
                continue;
            }
            shown++;
            var result = results[index];
            report.AppendLine($"#### {Cell(result.QuestionId)} — {Cell(result.QuestionType)}");
            report.AppendLine();
            report.AppendLine($"Question: {Cell(result.Question)}");
            report.AppendLine();
            report.AppendLine($"Reference answer: {Cell(result.ReferenceAnswer)}");
            report.AppendLine();
            report.AppendLine($"Gold evidence sessions: {Cell(string.Join(", ", result.GoldSessions))}");
            report.AppendLine();
            WriteConditionExample(report, result.RememberOnly, metric.RememberOnly, result.GoldSessions);
            WriteConditionExample(report, result.FullLongJourney, metric.FullLongJourney, result.GoldSessions);
        }
        if (shown == 0)
        {
            report.AppendLine("No cases of this type in the completed paired results.");
            report.AppendLine();
        }
    }

    private static void WriteConditionExample(StringBuilder report, ConditionResult condition,
        RetrievalMetrics metric, IReadOnlyList<string> goldSessions)
    {
        report.AppendLine($"**{Cell(condition.Condition)}** — Gold Recall@5: {metric.GoldSourceRecallAt5}; " +
            $"gold in candidates: {metric.GoldInCandidates}; judged answer correct: {condition.Judge.Correct}.");
        report.AppendLine();
        report.AppendLine($"Answer: {Cell(condition.Answer.Hypothesis)}");
        report.AppendLine();
        report.AppendLine("| Rank | Memory | Depth | Content | Ancestry sessions | Outgoing relations |");
        report.AppendLine("| ---: | --- | ---: | --- | --- | --- |");
        var graph = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var memory in condition.Recall.ProvenanceMemories)
        {
            graph.Add(memory.Id, memory);
        }
        foreach (var memory in condition.Recall.Candidates)
        {
            graph.TryAdd(memory.Id, memory);
        }
        foreach (var memory in condition.Recall.Selected)
        {
            graph.TryAdd(memory.Id, memory);
        }
        for (var rank = 0; rank < Math.Min(5, condition.Recall.Selected.Count); rank++)
        {
            var memory = condition.Recall.Selected[rank];
            report.AppendLine($"| {rank + 1} | {Cell(memory.Id)} | {memory.Depth} | {Cell(memory.Content)} | " +
                $"{Cell(string.Join(", ", BenchmarkMetrics.SourceSessions(memory, condition.Recall)))} | {Relations(memory, graph)} |");
        }
        report.AppendLine();
        var gold = new HashSet<string>(goldSessions, StringComparer.Ordinal);
        if (condition.Recall.CandidateTrace is { } trace)
        {
            report.AppendLine("Recorded candidate rankings (1-based; gold ancestry marked):");
            report.AppendLine();
            WriteCandidateRanking(report, "BM25", trace.LexicalMemoryIds, condition.Recall, graph, gold);
            WriteCandidateRanking(report, "Embedding", trace.SemanticMemoryIds, condition.Recall, graph, gold);
            WriteCandidateRanking(report, "RRF fused", trace.FusedMemoryIds, condition.Recall, graph, gold);
            report.AppendLine();
        }
        var unselectedGold = new List<MemoryRecord>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memory in condition.Recall.Selected)
        {
            selected.Add(memory.Id);
        }
        foreach (var memory in condition.Recall.Candidates)
        {
            if (!selected.Contains(memory.Id) && HasGold(memory, condition.Recall, gold))
            {
                unselectedGold.Add(memory);
            }
        }
        if (unselectedGold.Count > 0)
        {
            report.AppendLine($"Gold-connected candidates omitted by contextual selection: {unselectedGold.Count}. " +
                "The first three in candidate order are shown here.");
            report.AppendLine();
            for (var index = 0; index < Math.Min(3, unselectedGold.Count); index++)
            {
                var memory = unselectedGold[index];
                report.AppendLine($"- {Cell(memory.Id)} (depth {memory.Depth}): {Cell(memory.Content)}. " +
                    $"Ancestry sessions: {Cell(string.Join(", ", BenchmarkMetrics.SourceSessions(memory, condition.Recall)))}. " +
                    $"Outgoing relations: {Relations(memory, graph)}.");
            }
            report.AppendLine();
        }
        report.AppendLine("Provenance paths and parent content for the selected memories:");
        report.AppendLine();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < Math.Min(5, condition.Recall.Selected.Count); rank++)
        {
            WriteProvenance(report, condition.Recall.Selected[rank].Id, graph, condition.Recall.SourceToSession, visited);
        }
        report.AppendLine();
    }

    private static void WriteCandidateRanking(StringBuilder report, string label, IReadOnlyList<string> memoryIds,
        RecallArtifact recall, IReadOnlyDictionary<string, MemoryRecord> graph, HashSet<string> gold)
    {
        var ranked = new List<string>(memoryIds.Count);
        for (var rank = 0; rank < memoryIds.Count; rank++)
        {
            var id = memoryIds[rank];
            var marker = !graph.TryGetValue(id, out var memory) ? " [provenance unavailable]"
                : HasGold(memory, recall, gold) ? " [gold]" : "";
            ranked.Add($"{rank + 1}: {id}{marker}");
        }
        report.AppendLine($"- {label} ({memoryIds.Count}): {Cell(string.Join("; ", ranked))}.");
    }

    private static void WriteProvenance(StringBuilder report, string memoryId, IReadOnlyDictionary<string, MemoryRecord> graph,
        IReadOnlyDictionary<string, string> sourceToSession, HashSet<string> visited)
    {
        if (!visited.Add(memoryId))
        {
            return;
        }
        var memory = graph[memoryId];
        var provenance = memory.Depth == 0
            ? $"source_ref {memory.SourceRef} → session {sourceToSession[memory.SourceRef!]}"
            : $"derived_from [{string.Join(", ", memory.DerivedFrom)}]";
        report.AppendLine($"- {Cell(memory.Id)} (depth {memory.Depth}, {memory.CreatedAt:O}) → {Cell(provenance)}. " +
            $"Content: {Cell(memory.Content)}. Outgoing relations: {Relations(memory, graph)}.");
        foreach (var parent in memory.DerivedFrom)
        {
            WriteProvenance(report, parent, graph, sourceToSession, visited);
        }
    }

    private static bool HasGold(MemoryRecord memory, RecallArtifact recall, HashSet<string> gold)
    {
        foreach (var session in BenchmarkMetrics.SourceSessions(memory, recall))
        {
            if (gold.Contains(session))
            {
                return true;
            }
        }
        return false;
    }

    private static string Relations(MemoryRecord memory, IReadOnlyDictionary<string, MemoryRecord> graph)
    {
        if (memory.Relations.Count == 0)
        {
            return "None";
        }
        var descriptions = new List<string>(memory.Relations.Count);
        foreach (var relation in memory.Relations)
        {
            var target = graph.TryGetValue(relation.RelatedMemoryId, out var related)
                ? $"depth {related.Depth}: {related.Content}"
                : "target content unavailable in this artifact";
            descriptions.Add($"{relation.Kind} → {relation.RelatedMemoryId} ({target}; related_at {relation.RelatedAt:O})");
        }
        return Cell(string.Join("; ", descriptions));
    }

    private static string Cell(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);
    private static string Percent(decimal? value) => value is null ? "N/A" : Number(value * 100m) + "%";
    private static string Points(decimal? value) => value is null ? "N/A" : value.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " pp";
    private static string Number(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A";
    private static string Usd(decimal value) => "$" + value.ToString("0.000000", CultureInfo.InvariantCulture);
}
