using LongJourney.Benchmarks;
using LongJourney.OpenAI;

var command = args.Length == 0 ? "run" : args[0];
var configPath = args.Length > 1 ? args[1] : "benchmarks/longmemeval-s.json";
try
{
    var options = BenchmarkOptions.Load(configPath);
    if (command == "validate")
    {
        options.Validate();
        var dataset = LongMemEvalDataset.Load(options.DatasetPath);
        Console.WriteLine($"Validated pinned LongMemEval-S dataset: {dataset.Count} questions; Meditation ${options.MeditationBudgetUsd}/run.");
        return 0;
    }
    if (command == "report")
    {
        var results = new List<QuestionResult>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(options.OutputDirectory, "questions"), "result.json", SearchOption.AllDirectories))
        {
            results.Add(BenchmarkFiles.ReadJson<QuestionResult>(path));
        }
        BenchmarkReport.Write(options.OutputDirectory, results, 500);
        return 0;
    }
    if (command != "run")
    {
        throw new ArgumentException("Usage: LongJourney.Benchmarks [run|validate|report] [configuration.json]");
    }
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    var keySource = new OpenAiApiKeySource(Directory.GetCurrentDirectory());
    if (string.IsNullOrWhiteSpace(keySource.Read()))
    {
        throw new LongJourney.Core.InputException("Set OPENAI_API_KEY or place one API key in key.txt.");
    }
    using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    await new BenchmarkRunner(options, http, keySource.Read).RunAsync(cancellation.Token);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(BenchmarkRunner.SafeError(error));
    return 1;
}
