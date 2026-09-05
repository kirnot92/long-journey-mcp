using System.Globalization;
using System.Text;

namespace LongJourney.Benchmarks;

public static class DreamMicroReport
{
    public static void Write(string outputDirectory, IReadOnlyList<DreamMicroQuestionResult> results,
        int expectedQuestions, string status, IReadOnlyDictionary<string, UsageTotals> globalUsage, string? error = null)
    {
        if (expectedQuestions != 8 || results.Count > expectedQuestions)
        {
            throw new InvalidDataException("Dream micro benchmark reports require eight expected pairs.");
        }
        var wins = 0;
        var losses = 0;
        var tieSuccess = 0;
        var tieFailure = 0;
        var abstractionWins = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (!seen.Add(result.QuestionId))
            {
                throw new InvalidDataException("Dream micro benchmark report contains duplicate question IDs.");
            }
            var baseline = result.RememberOnly.Metrics.HitAt5;
            var dream = result.RememberPlusDream.Metrics.HitAt5;
            if (!baseline && dream)
            {
                wins++;
                var selectedById = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var memory in result.RememberPlusDream.Recall.Selected)
                {
                    selectedById.Add(memory.Id, memory.Depth);
                }
                foreach (var match in result.RememberPlusDream.Metrics.SelectedMatches)
                {
                    if (match.GoldDepth0Ids.Count > 0 && selectedById[match.MemoryId] > 0)
                    {
                        abstractionWins++;
                        break;
                    }
                }
            }
            else if (baseline && !dream)
            {
                losses++;
            }
            else if (baseline)
            {
                tieSuccess++;
            }
            else
            {
                tieFailure++;
            }
        }
        var complete = results.Count == expectedQuestions && status == "complete";
        var interpretation = !complete ? "INCOMPLETE / INCONCLUSIVE"
            : losses > wins ? "REGRESSION"
            : wins > losses && abstractionWins > 0 ? "PROMISING (recorded abstraction provenance; human audit required)"
            : "INCONCLUSIVE";
        var report = new StringBuilder();
        report.AppendLine("# Daily Dream retrieval micro benchmark");
        report.AppendLine();
        report.AppendLine($"Status: {status}. Completed pairs: {results.Count}/{expectedQuestions}. Interpretation: {interpretation}.");
        report.AppendLine();
        report.AppendLine("This is a diagnostic on a deterministic reduced history, not a LongMemEval score or proof of overall system performance. " +
            "Gold Evidence Recall@5 is ANY labeled answer-bearing D0 in a selected memory's actual derived_from ancestry. " +
            "A shared Source or relation alone never counts. An ancestry hit does not prove that the abstraction text itself retains the answer. " +
            "Coverage and all-evidence columns show how much of a multi-fact answer is represented. " +
            "Absent supported D0 is retained as Remember extraction failure, including dataset abstention cases; these are not silently substituted.");
        report.AppendLine();
        report.AppendLine("Retrieval metrics below describe only the completed-pair subset. Uncompleted questions receive no pass/fail classification, " +
            "and an interrupted run receives no overall success or regression verdict.");
        if (error is not null)
        {
            report.AppendLine();
            report.AppendLine($"Stop reason: {Escape(error)}");
        }
        report.AppendLine();
        report.AppendLine("## Retrieval and paired results");
        report.AppendLine();
        report.AppendLine("| Question | Type | Remember hit / candidate | Dream hit / candidate | Remember coverage / all | Dream coverage / all | Paired result |");
        report.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        var baselineHits = 0;
        var dreamHits = 0;
        var baselineCandidates = 0;
        var dreamCandidates = 0;
        long baselineTokens = 0;
        long dreamTokens = 0;
        foreach (var result in results)
        {
            var a = result.RememberOnly.Metrics;
            var b = result.RememberPlusDream.Metrics;
            baselineHits += a.HitAt5 ? 1 : 0;
            dreamHits += b.HitAt5 ? 1 : 0;
            baselineCandidates += a.GoldInCandidates ? 1 : 0;
            dreamCandidates += b.GoldInCandidates ? 1 : 0;
            baselineTokens += result.RememberOnly.Recall.RecallInputTokens;
            dreamTokens += result.RememberPlusDream.Recall.RecallInputTokens;
            report.AppendLine($"| {Escape(result.QuestionId)} | {Escape(result.QuestionType)} | {a.HitAt5} / {a.GoldInCandidates} | " +
                $"{b.HitAt5} / {b.GoldInCandidates} | {Number(a.SelectedEvidenceCoverage)} / {a.AllEvidenceAt5} | " +
                $"{Number(b.SelectedEvidenceCoverage)} / {b.AllEvidenceAt5} | {Pair(a.HitAt5, b.HitAt5)} |");
        }
        report.AppendLine();
        report.AppendLine("| Metric | Remember Only | Daily Dream |");
        report.AppendLine("| --- | ---: | ---: |");
        report.AppendLine($"| Gold Evidence Recall@5 | {Rate(baselineHits, results.Count)} | {Rate(dreamHits, results.Count)} |");
        report.AppendLine($"| Gold Evidence Candidate Hit | {Rate(baselineCandidates, results.Count)} | {Rate(dreamCandidates, results.Count)} |");
        report.AppendLine($"| Recall input tokens | {baselineTokens} | {dreamTokens} |");
        report.AppendLine();
        report.AppendLine($"Dream wins: {wins}; Dream losses: {losses}; tie-success: {tieSuccess}; tie-failure: {tieFailure}. " +
            $"Wins with a selected abstraction reaching labeled D0: {abstractionWins}.");
        report.AppendLine();
        report.AppendLine("## Physical API cost (including unfinished work)");
        report.AppendLine();
        report.AppendLine("Shared Remember/embedding calls appear once in these global totals. Pending reservations remain part of the hard-cap accounting.");
        report.AppendLine();
        report.AppendLine("| Operation | Calls | Settled USD | Reserved USD | Input / output tokens |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        decimal totalCost = 0;
        decimal totalReserved = 0;
        var totalCalls = 0;
        var sortedUsage = new SortedDictionary<string, UsageTotals>(StringComparer.Ordinal);
        foreach (var pair in globalUsage)
        {
            sortedUsage.Add(pair.Key, pair.Value);
        }
        foreach (var (operation, usage) in sortedUsage)
        {
            totalCost += usage.SettledUsd;
            totalReserved += usage.ReservedUsd;
            totalCalls += usage.Calls;
            report.AppendLine($"| {Escape(operation)} | {usage.Calls} | {Number(usage.SettledUsd)} | {Number(usage.ReservedUsd)} | {usage.InputTokens} / {usage.OutputTokens} |");
        }
        report.AppendLine($"| Total | {totalCalls} | {Number(totalCost)} | {Number(totalReserved)} | |");
        report.AppendLine();
        report.AppendLine("## Dream pruning (completed pairs)");
        report.AppendLine();
        report.AppendLine("| Question | Consolidation work | Impossible before LLM | Exact duplicate neighborhood | Actual LLM calls | Zero-abstraction calls | Created abstractions |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var result in results)
        {
            var p = result.RememberPlusDream.Pruning;
            report.AppendLine($"| {Escape(result.QuestionId)} | {p.ConsolidationWork} | {p.ImpossibleBeforeLlm} | {p.ExactDuplicateNeighborhood} | {p.ActualLlmCalls} | {p.ZeroAbstractionCalls} | {p.CreatedAbstractions} |");
        }
        report.AppendLine();
        report.AppendLine("Zero-abstraction calls count saved successful LLM responses with an empty proposal list; rejected nonempty proposals and failed calls are not empty responses.");
        report.AppendLine();
        report.AppendLine("## Memory morphology (completed pairs)");
        report.AppendLine();
        report.AppendLine("| Question / condition | Sources | D0 | D1 | D2+ | Positive / negative relations | Dream-created | D1 / D0 | Candidates | Recall input tokens |");
        report.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var result in results)
        {
            WriteMorphology(report, result.QuestionId, result.RememberOnly);
            WriteMorphology(report, result.QuestionId, result.RememberPlusDream);
        }
        report.AppendLine();
        report.AppendLine("## Evidence audit and actual retrieval provenance");
        foreach (var result in results)
        {
            report.AppendLine();
            report.AppendLine($"### {result.QuestionId}: {Pair(result.RememberOnly.Metrics.HitAt5, result.RememberPlusDream.Metrics.HitAt5)}");
            report.AppendLine();
            report.AppendLine($"Question: {Escape(result.Question)}");
            report.AppendLine();
            report.AppendLine($"Reference: {Escape(result.ReferenceAnswer)}");
            report.AppendLine();
            report.AppendLine($"Evaluator: {result.Evidence.Model}. {result.Evidence.Note}");
            var judgments = new Dictionary<string, DreamMicroEvidenceJudgment>(StringComparer.Ordinal);
            foreach (var judgment in result.Evidence.Judgments)
            {
                judgments.Add(judgment.MemoryId, judgment);
            }
            foreach (var memory in result.Evidence.OfferedDepth0)
            {
                var judgment = judgments[memory.Id];
                report.AppendLine();
                report.AppendLine($"- D0 `{memory.Id}` ({memory.CreatedAt:O}), answer-bearing: {judgment.AnswerBearing}. {Escape(memory.Content)} Reason: {Escape(judgment.Reason)}");
            }
            WriteRecall(report, result.RememberOnly);
            WriteRecall(report, result.RememberPlusDream);
        }
        Directory.CreateDirectory(outputDirectory);
        BenchmarkFiles.WriteJson(Path.Combine(outputDirectory, "metrics.json"), new
        {
            status,
            complete,
            expectedQuestions,
            completedQuestions = results.Count,
            interpretation,
            wins,
            losses,
            tieSuccess,
            tieFailure,
            abstractionWins,
            globalUsage,
            results,
            error
        });
        var path = Path.Combine(outputDirectory, "report.md");
        File.WriteAllText(path + ".tmp", report.ToString());
        File.Move(path + ".tmp", path, true);
    }

    private static void WriteMorphology(StringBuilder report, string questionId, DreamMicroConditionResult condition)
    {
        var m = condition.Morphology;
        report.AppendLine($"| {Escape(questionId)} / {Escape(condition.Condition)} | {m.Sources} | {m.Depth0} | {m.Depth1} | {m.Depth2Plus} | " +
            $"{m.PositiveRelations} / {m.NegativeRelations} | {m.DreamMemories} | {(m.Depth0 == 0 ? "n/a" : Number(m.Depth1 / (decimal)m.Depth0))} | " +
            $"{condition.Recall.Candidates.Count} | {condition.Recall.RecallInputTokens} |");
    }

    private static void WriteRecall(StringBuilder report, DreamMicroConditionResult condition)
    {
        report.AppendLine();
        report.AppendLine($"**{condition.Condition}** — extraction failure: {condition.Metrics.RememberExtractionFailure}; " +
            $"candidate failure: {condition.Metrics.CandidateRetrievalFailure}; selection failure: {condition.Metrics.RecallSelectionFailure}.");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memory in condition.Recall.Selected)
        {
            selected.Add(memory.Id);
        }
        var matches = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var match in condition.Metrics.CandidateMatches)
        {
            matches.Add(match.MemoryId, match.GoldDepth0Ids);
        }
        foreach (var memory in condition.Recall.Candidates)
        {
            report.AppendLine();
            report.AppendLine($"- {(selected.Contains(memory.Id) ? "SELECTED" : "candidate")} `{memory.Id}` D{memory.Depth}: {Escape(memory.Content)} " +
                $"derived_from [{string.Join(", ", memory.DerivedFrom)}]; matching gold D0 [{string.Join(", ", matches[memory.Id])}].");
        }
        report.AppendLine();
        report.AppendLine("Recorded ancestry:");
        foreach (var memory in condition.Recall.ProvenanceMemories)
        {
            report.AppendLine($"- `{memory.Id}` D{memory.Depth}, derived_from [{string.Join(", ", memory.DerivedFrom)}], source_ref {memory.SourceRef}: {Escape(memory.Content)}");
        }
    }

    private static string Pair(bool baseline, bool dream) => baseline
        ? dream ? "Tie-success" : "Dream loss"
        : dream ? "Dream win" : "Tie-failure";
    private static string Number(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static string Rate(int hits, int count) => count == 0 ? "n/a" : $"{hits}/{count}";
    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
