using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Benchmarks;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("plan" or "run" or "report"))
        {
            Console.WriteLine("Usage: LongJourney.Benchmarks <plan|run|report> <config.json>");
            return 1;
        }
        try
        {
            var options = BenchmarkOptions.Read(args[1]);
            if (args[0] == "report")
            {
                var manifest = BenchmarkArtifacts.Read<ExperimentManifest>(
                    Path.Combine(options.OutputDirectory, "manifest.json"))
                    ?? throw new InputException("This experiment has no manifest.");
                var report = BenchmarkReport.Create(manifest.Units,
                    BenchmarkBudget.ReadUsage(BenchmarkArtifacts.DatabasePaths(manifest.Units)));
                PrintReport(report);
                return 0;
            }
            var dataset = await LongMemEvalDataset.ReadSelectedAsync(
                options.DatasetPath, options.MaxRawCharacters, options.QuestionIds, options.Limit);
            var selected = options.SelectCases(dataset);
            if (args[0] == "plan")
            {
                var questions = new List<object>();
                foreach (var item in selected)
                {
                    var valid = true;
                    try
                    {
                        LongMemEvalDataset.ValidateTimeline(item);
                    }
                    catch (InputException)
                    {
                        valid = false;
                    }
                    questions.Add(new
                    {
                        id = item.Id,
                        turns = item.History.Turns.Count,
                        sessions = item.History.Sessions.Count,
                        valid_timeline = valid
                    });
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    protocol = BenchmarkOptions.ProtocolVersion,
                    split = options.Split,
                    dataset_hash = dataset.Sha256,
                    questions,
                    variants = options.Variants,
                    experiment_budget_usd = options.ExperimentBudgetUsd,
                    meditation_budget_usd = options.MeditationBudgetUsd,
                    output_directory = options.OutputDirectory
                }, JsonDefaults.Options));
                return 0;
            }
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            var key = new OpenAiApiKeySource(Directory.GetCurrentDirectory());
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var runner = new BenchmarkRunner(options,
                (engine, ledger, clock) => new OpenAiCognition(http, options.OpenAI, engine, ledger, clock, key.Read),
                (ledger, clock) => new BenchmarkLanguageModel(
                    http, options.OpenAI, options.AnswerModel, options.JudgeModel, ledger, clock, key.Read),
                Console.WriteLine);
            var result = await runner.RunAsync(dataset, cancellation.Token);
            PrintReport(result);
            return result.Completed == result.Planned ? 0 : 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark cancelled; run the same configuration to resume.");
            return 2;
        }
        catch (Exception error)
        {
            // External errors may contain secret values or raw provider response bodies.
            Console.Error.WriteLine(error is InputException ? error.Message :
                $"Benchmark failed ({error.GetType().Name}). Inspect the saved result status.");
            return 1;
        }
    }

    private static void PrintReport(BenchmarkReport report)
    {
        Console.WriteLine($"Completed {report.Completed}/{report.Planned}; paired questions: {report.PairedQuestions}");
        Console.WriteLine($"Actual USD: {report.Usage.ActualUsd:F6}; unsettled reservations: {report.Usage.ReservedUsd:F6}");
        foreach (var variant in report.Variants)
        {
            Console.WriteLine($"{variant.Variant}: {variant.Score.Correct}/{variant.Completed} correct; " +
                $"paired {variant.PairedScore.Correct}/{variant.PairedScore.Completed}");
        }
        foreach (var result in report.Results)
        {
            if (result.Status != "complete")
            {
                Console.WriteLine($"{result.QuestionId}/{result.Variant}: {result.Status} ({result.ErrorType})");
            }
        }
    }
}
