using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Benchmarks;

public sealed class BenchmarkRunner(BenchmarkOptions options, HttpClient http, Func<string?> apiKey)
{
    private readonly object reportGate = new();
    private readonly ConcurrentDictionary<string, QuestionResult> completed = new(StringComparer.Ordinal);
    private int firstFailure;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        options.Validate();
        var questions = LongMemEvalDataset.Load(options.DatasetPath);
        if (questions.Count != 500)
        {
            throw new InputException("This experiment requires all 500 LongMemEval-S questions.");
        }
        Directory.CreateDirectory(options.OutputDirectory);
        using var lease = new CorpusLease(new EngineOptions { DataDirectory = options.OutputDirectory });
        var maximumRaw = 4000;
        var sessions = 0;
        foreach (var question in questions)
        {
            foreach (var session in question.Sessions)
            {
                maximumRaw = Math.Max(maximumRaw, session.Raw.Length);
                sessions++;
            }
        }
        ValidateManifest(maximumRaw, sessions);
        using var statusCancellation = new CancellationTokenSource();
        var statusTask = MonitorExecutionAsync(statusCancellation.Token);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.Workers, CancellationToken = cancellationToken };
        using var failureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        parallel.CancellationToken = failureCancellation.Token;
        var errors = new ConcurrentQueue<Exception>();
        Console.WriteLine($"LongMemEval-S: {questions.Count} questions, {sessions} sessions, {options.Workers} workers; Meditation <= ${options.MeditationBudgetUsd} each.");
        try
        {
            await Parallel.ForEachAsync(questions, parallel, async (question, token) =>
            {
                try
                {
                    var result = await RunQuestionAsync(question, maximumRaw, token);
                    completed[question.QuestionId] = result;
                    WriteReport(questions.Count);
                    Console.WriteLine($"Completed {completed.Count}/{questions.Count}: {question.QuestionId}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    errors.Enqueue(error);
                    // Stop dispatching new paid work after an actual execution failure.
                    if (Interlocked.CompareExchange(ref firstFailure, 1, 0) == 0)
                    {
                        BenchmarkFiles.WriteJson(Path.Combine(options.OutputDirectory, "failure.json"), new
                        {
                            question_id = question.QuestionId,
                            error_type = error.GetType().Name,
                            message = SafeError(error),
                            at = DateTimeOffset.UtcNow
                        });
                    }
                    await failureCancellation.CancelAsync();
                    throw;
                }
            });
        }
        catch (Exception) when (!errors.IsEmpty)
        {
            errors.TryPeek(out var failure);
            throw new InputException($"Benchmark execution failed: {SafeError(failure!)}");
        }
        finally
        {
            await statusCancellation.CancelAsync();
            await statusTask;
            WriteReport(questions.Count);
            BenchmarkExecutionStatus.Write(options.OutputDirectory, "stopped");
        }
    }

    private async Task<QuestionResult> RunQuestionAsync(BenchmarkQuestion question, int maximumRaw, CancellationToken token)
    {
        var directory = Path.Combine(options.OutputDirectory, "questions", SafeId(question.QuestionId));
        Directory.CreateDirectory(directory);
        var resultPath = Path.Combine(directory, "result.json");
        if (File.Exists(resultPath))
        {
            return BenchmarkFiles.ReadJson<QuestionResult>(resultPath);
        }
        var baselineOptions = options.CreateEngineOptions(Path.Combine(directory, "remember-only"), maximumRaw);
        var fullOptions = options.CreateEngineOptions(Path.Combine(directory, "full-long-journey"), maximumRaw);
        using var baselineLease = new CorpusLease(baselineOptions);
        using var fullLease = new CorpusLease(fullOptions);
        var baseline = new SqliteMemoryStore(baselineOptions);
        var full = new SqliteMemoryStore(fullOptions);
        var baselineClock = new BenchmarkClock();
        var fullClock = new BenchmarkClock();
        var baselineCognition = new CachedIngestionCognition(
            new OpenAiCognition(http, options.OpenAi, baselineOptions, baseline, baselineClock, apiKey),
            Path.Combine(directory, "shared-extraction"));
        var fullCognition = new OpenAiCognition(http, options.OpenAi, fullOptions, full, fullClock, apiKey);
        var baselineSearch = new BenchmarkSearch(baseline, baselineCognition, baselineOptions);
        var fullSearch = new BenchmarkSearch(full, fullCognition, fullOptions);
        var baselineEngine = new MemoryEngine(baseline, baselineCognition, baselineSearch, baselineOptions, baselineClock);
        var fullEngine = new MemoryEngine(full, fullCognition, fullSearch, fullOptions, fullClock);
        var consolidation = new ConsolidationEngine(full, fullCognition, fullSearch, fullOptions, fullClock);
        var scheduler = new MemoryScheduler(full, consolidation, fullOptions, fullClock);
        Console.WriteLine($"Starting {question.QuestionId}: {question.Sessions.Count} sessions");
        try
        {
            var sourceMap = await BenchmarkReplay.ReplayAsync(question.Sessions, question.QuestionDate,
                baseline, full, baselineEngine, scheduler, baselineClock, fullClock, baselineCognition.EmbeddingSpace, token);
            var sharedPath = Path.Combine(directory, "shared-ingestion-usage.json");
            var shared = File.Exists(sharedPath)
                ? BenchmarkFiles.ReadJson<UsageTotals>(sharedPath)
                : BenchmarkUsage.Read(baseline);
            BenchmarkFiles.WriteJson(sharedPath, shared);
            BenchmarkFiles.WriteJson(Path.Combine(directory, "source-sessions.json"), sourceMap);
            var baselineResult = await EvaluateAsync(question, "remember-only", directory, baseline,
                baselineEngine, baselineSearch, baselineClock, sourceMap, shared, token);
            var fullResult = await EvaluateAsync(question, "full-long-journey", directory, full,
                fullEngine, fullSearch, fullClock, sourceMap, new UsageTotals(0, 0, 0, 0, 0), token);
            var result = new QuestionResult(question.QuestionId, question.QuestionType, question.Question,
                question.Answer, question.AnswerSessionIds, shared, baselineResult, fullResult);
            BenchmarkFiles.WriteJson(resultPath, result);
            return result;
        }
        finally
        {
            BenchmarkUsage.ExportCalls(baseline, Path.Combine(directory, "remember-only-api-calls.jsonl"));
            BenchmarkUsage.ExportCalls(full, Path.Combine(directory, "full-long-journey-api-calls.jsonl"));
        }
    }

    private async Task<ConditionResult> EvaluateAsync(BenchmarkQuestion question, string condition, string directory,
        SqliteMemoryStore store, MemoryEngine engine, BenchmarkSearch search, BenchmarkClock clock,
        IReadOnlyDictionary<string, string> sourceMap, UsageTotals shared, CancellationToken token)
    {
        var stageDirectory = Path.Combine(directory, condition);
        var recallPath = Path.Combine(stageDirectory, "recall.json");
        RecallArtifact recall;
        if (File.Exists(recallPath))
        {
            recall = BenchmarkFiles.ReadJson<RecallArtifact>(recallPath);
        }
        else
        {
            var beforeTokens = BenchmarkUsage.Read(store, "recall").InputTokens;
            var result = await engine.RecallAsync(question.Question,
                $"Question date: {question.QuestionDate.ToString("O", CultureInfo.InvariantCulture)}", token);
            var inputTokens = BenchmarkUsage.Read(store, "recall").InputTokens - beforeTokens;
            recall = new RecallArtifact(search.LastCandidates, result.Memories,
                store.ReadSnapshot().Memories, sourceMap, inputTokens, search.LastTrace);
            BenchmarkFiles.WriteJson(recallPath, recall);
        }
        var languageModel = new BenchmarkLanguageModel(http, options.OpenAi, store, clock, apiKey);
        var answerPath = Path.Combine(stageDirectory, "answer.json");
        var answer = File.Exists(answerPath)
            ? BenchmarkFiles.ReadJson<AnswerArtifact>(answerPath)
            : await languageModel.AnswerAsync(question, recall.Selected, token);
        BenchmarkFiles.WriteJson(answerPath, answer);
        var judgePath = Path.Combine(stageDirectory, "judge.json");
        var judge = File.Exists(judgePath)
            ? BenchmarkFiles.ReadJson<JudgeArtifact>(judgePath)
            : await languageModel.JudgeAsync(question, answer, token);
        BenchmarkFiles.WriteJson(judgePath, judge);
        return new ConditionResult(condition, recall, answer, judge, BenchmarkUsage.Morphology(store, sourceMap),
            BenchmarkUsage.Subtract(BenchmarkUsage.Read(store), shared), store.GetRuns());
    }

    private void ValidateManifest(int maximumRaw, int sessions)
    {
        var configuration = JsonSerializer.Serialize(new
        {
            options.DatasetSha256,
            options.MeditationBudgetUsd,
            options.MaxObservationsPerSession,
            options.OpenAi,
            maximumRaw,
            sessions,
            recall_limit = 5,
            timezone = "UTC",
            primary_denominator = 500,
            replay_cutoff = "max(question_date,last_session_timestamp); original question date retained in query",
            pass_percentage_points = 3,
            category_regression_percentage_points = 5,
            evaluator_commit = "9e0b455f4ef0e2ab8f2e582289761153549043fc",
            evaluator_sha256 = "5085eb9ae08b91c6c4f97c6bea9b8c2d6e55f9ec72c27734cafa0a04f00a430a",
            implementation_sha256 = ImplementationHash(),
            source_commit = "d98e17b",
            protocol = 1
        }, JsonDefaults.Options);
        var path = Path.Combine(options.OutputDirectory, "manifest.json");
        if (File.Exists(path) && File.ReadAllText(path) != configuration)
        {
            throw new InputException("Saved run configuration differs. Resume only with the original frozen settings.");
        }
        File.WriteAllText(path + ".tmp", configuration);
        File.Move(path + ".tmp", path, true);
        var snapshotDirectory = Path.Combine(options.OutputDirectory, "implementation");
        if (!Directory.Exists(snapshotDirectory))
        {
            foreach (var project in new[] { "LongJourney.Core", "LongJourney.OpenAI", "LongJourney.Benchmarks" })
            {
                var sourceDirectory = Path.Combine("src", project);
                var destination = Path.Combine(snapshotDirectory, "src", project);
                Directory.CreateDirectory(destination);
                foreach (var source in Directory.GetFiles(sourceDirectory, "*.cs", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(source, Path.Combine(destination, Path.GetFileName(source)));
                }
            }
        }
    }

    private static string ImplementationHash()
    {
        var paths = new List<string>();
        foreach (var project in new[] { "LongJourney.Core", "LongJourney.OpenAI", "LongJourney.Benchmarks" })
        {
            paths.AddRange(Directory.GetFiles(Path.Combine("src", project), "*.cs", SearchOption.TopDirectoryOnly));
        }
        paths.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task MonitorExecutionAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                BenchmarkExecutionStatus.Write(options.OutputDirectory, "running");
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The final synchronous snapshot is written after all workers have stopped.
        }
    }

    private void WriteReport(int expected)
    {
        lock (reportGate)
        {
            var results = new List<QuestionResult>(completed.Values);
            results.Sort((left, right) => StringComparer.Ordinal.Compare(left.QuestionId, right.QuestionId));
            BenchmarkReport.Write(options.OutputDirectory, results, expected);
            BenchmarkFiles.WriteJson(Path.Combine(options.OutputDirectory, "progress.json"), new
            {
                completed_questions = results.Count,
                expected_questions = expected,
                status = results.Count == expected ? "complete" : "incomplete",
                updated_at = DateTimeOffset.UtcNow
            });
        }
    }

    public static string SafeError(Exception error) => error switch
    {
        HttpRequestException httpError => $"OpenAI HTTP request failed ({(int?)httpError.StatusCode}).",
        OperationCanceledException => "Execution canceled or timed out; completed work is preserved.",
        InputException or InvariantException or InvalidDataException => error.Message,
        _ => error.GetType().Name
    };

    private static string SafeId(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..24];
}
