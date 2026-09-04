using System.Text;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record Score(int Completed, int Correct, double? Accuracy);
public sealed record VariantReport(BenchmarkVariant Variant, int Planned, int Completed,
    Score Score, Score PairedScore, IReadOnlyDictionary<string, Score> ByQuestionType,
    double? AdmittedSessionCoverage);
public sealed record BenchmarkReport(int Planned, int Completed, int PairedQuestions,
    BenchmarkUsage Usage, IReadOnlyList<VariantReport> Variants,
    IReadOnlyList<BenchmarkResult> Results)
{
    public static BenchmarkReport Create(IReadOnlyList<BenchmarkUnit> units, BenchmarkUsage usage)
    {
        var results = new List<BenchmarkResult>();
        var byVariant = new Dictionary<BenchmarkVariant, List<BenchmarkResult>>();
        var planned = new Dictionary<BenchmarkVariant, int>();
        var completedVariants = new Dictionary<string, HashSet<BenchmarkVariant>>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            planned.TryGetValue(unit.Variant, out var count);
            planned[unit.Variant] = count + 1;
            if (!byVariant.TryGetValue(unit.Variant, out var variantResults))
            {
                variantResults = [];
                byVariant.Add(unit.Variant, variantResults);
            }
            var result = BenchmarkArtifacts.Read<BenchmarkResult>(Path.Combine(unit.Directory, "result.json"));
            if (result is null)
            {
                continue;
            }
            results.Add(result);
            variantResults.Add(result);
            if (result.Status == "complete")
            {
                if (!completedVariants.TryGetValue(result.QuestionId, out var variants))
                {
                    variants = [];
                    completedVariants.Add(result.QuestionId, variants);
                }
                variants.Add(result.Variant);
            }
        }
        var paired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in completedVariants)
        {
            if (entry.Value.Count == byVariant.Count)
            {
                paired.Add(entry.Key);
            }
        }
        var reports = new List<VariantReport>();
        var totalCompleted = 0;
        foreach (var entry in byVariant)
        {
            var complete = new List<BenchmarkResult>();
            var common = new List<BenchmarkResult>();
            var types = new Dictionary<string, List<BenchmarkResult>>(StringComparer.Ordinal);
            double coverage = 0;
            var coveredCases = 0;
            foreach (var result in entry.Value)
            {
                if (result.Status != "complete")
                {
                    continue;
                }
                complete.Add(result);
                if (paired.Contains(result.QuestionId))
                {
                    common.Add(result);
                }
                var typeName = result.IsAbstention ? "abstention" : result.QuestionType;
                if (!types.TryGetValue(typeName, out var typeResults))
                {
                    typeResults = [];
                    types.Add(typeName, typeResults);
                }
                typeResults.Add(result);
                if (result.Metrics?.AdmittedSessionCoverage is { } retrieved)
                {
                    coverage += retrieved.Fraction;
                    coveredCases++;
                }
            }
            var typeScores = new Dictionary<string, Score>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                typeScores.Add(type.Key, Calculate(type.Value));
            }
            totalCompleted += complete.Count;
            reports.Add(new VariantReport(entry.Key, planned[entry.Key], complete.Count,
                Calculate(complete), Calculate(common), typeScores,
                coveredCases == 0 ? null : coverage / coveredCases));
        }
        return new BenchmarkReport(units.Count, totalCompleted, paired.Count, usage, reports, results);
    }

    private static Score Calculate(IReadOnlyList<BenchmarkResult> results)
    {
        var correct = 0;
        foreach (var result in results)
        {
            if (result.Judgment?.Value.Correct == true)
            {
                correct++;
            }
        }
        return new Score(results.Count, correct, results.Count == 0 ? null : (double)correct / results.Count);
    }

    public static void ExportHypotheses(string outputDirectory, IReadOnlyList<BenchmarkUnit> units)
    {
        var outputs = new Dictionary<BenchmarkVariant, StringBuilder>();
        foreach (var unit in units)
        {
            var result = BenchmarkArtifacts.Read<BenchmarkResult>(Path.Combine(unit.Directory, "result.json"));
            if (result?.Answer is null)
            {
                continue;
            }
            if (!outputs.TryGetValue(unit.Variant, out var lines))
            {
                lines = new StringBuilder();
                outputs.Add(unit.Variant, lines);
            }
            lines.AppendLine(JsonSerializer.Serialize(
                new { question_id = result.QuestionId, hypothesis = result.Answer.Value }, JsonDefaults.Options));
        }
        foreach (var output in outputs)
        {
            File.WriteAllText(Path.Combine(outputDirectory, $"hypotheses-{output.Key}.jsonl"),
                output.Value.ToString(), new UTF8Encoding(false));
        }
    }
}
