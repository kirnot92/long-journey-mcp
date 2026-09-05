using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Benchmarks;

public sealed class DreamMicroRunner(DreamMicroOptions options, HttpClient http, Func<string?> apiKey)
{
    private readonly ConcurrentDictionary<string, DreamMicroQuestionResult> completed = new(StringComparer.Ordinal);
    private readonly object reportGate = new();

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var questions = options.ValidateAndSelect();
        Directory.CreateDirectory(options.OutputDirectory);
        using var lease = new CorpusLease(new EngineOptions { DataDirectory = options.OutputDirectory });
        FreezeManifest(questions);
        var budget = new DreamMicroBudget(Path.Combine(options.OutputDirectory, "budget"), options.TotalBudgetUsd);
        var status = "running";
        string? error = null;
        WriteReport(questions, budget, status, error);
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Parallel.ForEachAsync(questions, new ParallelOptions
            {
                MaxDegreeOfParallelism = options.Workers,
                CancellationToken = stopped.Token
            }, async (question, token) =>
            {
                try
                {
                    completed[question.QuestionId] = await RunQuestionAsync(question, budget, token);
                    WriteReport(questions, budget, "running", null);
                    Console.WriteLine($"Completed micro pair {completed.Count}/8: {question.QuestionId}; accounted ${budget.ReadTotal().SettledUsd + budget.ReadTotal().ReservedUsd:F4}");
                }
                catch (Exception failure)
                {
                    lock (reportGate)
                    {
                        error ??= $"{question.QuestionId}: {BenchmarkRunner.SafeError(failure)}";
                    }
                    await stopped.CancelAsync();
                    throw;
                }
            });
            status = "complete";
        }
        catch (Exception failure)
        {
            status = "incomplete";
            error ??= BenchmarkRunner.SafeError(failure);
            Console.Error.WriteLine(error);
        }
        finally
        {
            WriteReport(questions, budget, status, error);
            budget.ExportCalls(Path.Combine(options.OutputDirectory, "global-api-calls.jsonl"));
        }
        return status == "complete";
    }

    private async Task<DreamMicroQuestionResult> RunQuestionAsync(
        BenchmarkQuestion question, DreamMicroBudget budget, CancellationToken token)
    {
        if (question.QuestionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            question.QuestionId is "." or "..")
        {
            throw new InvalidDataException("Question ID is not a safe artifact name.");
        }
        var directory = Path.Combine(options.OutputDirectory, "questions", question.QuestionId);
        Directory.CreateDirectory(directory);
        var resultPath = Path.Combine(directory, "result.json");
        if (File.Exists(resultPath))
        {
            return BenchmarkFiles.ReadJson<DreamMicroQuestionResult>(resultPath);
        }
        var maximumRaw = 4000;
        foreach (var session in question.Sessions)
        {
            maximumRaw = Math.Max(maximumRaw, session.Raw.Length);
        }
        var baselineOptions = options.CreateEngineOptions(Path.Combine(directory, "remember-only"), maximumRaw);
        var dreamOptions = options.CreateEngineOptions(Path.Combine(directory, "daily-dream"), maximumRaw);
        var evaluatorOptions = options.CreateEngineOptions(Path.Combine(directory, "evaluator"), maximumRaw);
        var baseline = new SqliteMemoryStore(baselineOptions);
        var dream = new SqliteMemoryStore(dreamOptions);
        var evaluator = new SqliteMemoryStore(evaluatorOptions);
        var baselineClock = new BenchmarkClock();
        var dreamClock = new BenchmarkClock();
        var baselineCognition = new CachedIngestionCognition(new OpenAiCognition(http, options.OpenAi,
            baselineOptions, budget.Scope(baseline, question.QuestionId + "/remember-only"), baselineClock, apiKey),
            Path.Combine(directory, "shared-extraction"));
        var dreamCognition = new OpenAiCognition(http, options.OpenAi, dreamOptions,
            budget.Scope(dream, question.QuestionId + "/daily-dream"), dreamClock, apiKey);
        var baselineSearch = new BenchmarkSearch(baseline, baselineCognition, baselineOptions);
        var dreamSearch = new BenchmarkSearch(dream, dreamCognition, dreamOptions);
        var baselineEngine = new MemoryEngine(baseline, baselineCognition, baselineSearch, baselineOptions, baselineClock);
        var dreamEngine = new MemoryEngine(dream, dreamCognition, dreamSearch, dreamOptions, dreamClock);
        var consolidation = new ConsolidationEngine(dream, dreamCognition, dreamSearch, dreamOptions, dreamClock);
        try
        {
            SetStage(directory, question.QuestionId, "shared-extraction");
            foreach (var session in question.Sessions)
            {
                token.ThrowIfCancellationRequested();
                baselineClock.UtcNow = session.Timestamp;
                var result = await baselineEngine.RememberAsync(session.Raw, token);
                if (result.Status != "complete")
                {
                    throw new InvariantException("Micro shared extraction did not complete.");
                }
                Console.WriteLine($"{question.QuestionId}: shared Source {session.Timestamp:yyyy-MM-dd}, {result.Memories.Count} D0");
            }
            var sharedPath = Path.Combine(directory, "shared-ingestion-usage.json");
            var shared = File.Exists(sharedPath)
                ? BenchmarkFiles.ReadJson<UsageTotals>(sharedPath) : BenchmarkUsage.Read(baseline);
            BenchmarkFiles.WriteJson(sharedPath, shared);
            var sourceMap = BenchmarkReplay.CreateSourceMap(question.Sessions);
            BenchmarkFiles.WriteJson(Path.Combine(directory, "source-sessions.json"), sourceMap);
            baselineClock.UtcNow = BenchmarkReplay.EvaluationTime(question.Sessions, question.QuestionDate);

            SetStage(directory, question.QuestionId, "evidence-labeling");
            var evidencePath = Path.Combine(directory, "evidence.json");
            DreamMicroEvidenceArtifact evidence;
            if (File.Exists(evidencePath))
            {
                evidence = BenchmarkFiles.ReadJson<DreamMicroEvidenceArtifact>(evidencePath);
            }
            else
            {
                var goldSessions = new HashSet<string>(question.AnswerSessionIds, StringComparer.Ordinal);
                var goldDepth0 = new List<MemoryRecord>();
                foreach (var memory in baseline.ReadSnapshot().Memories)
                {
                    if (memory.SourceRef is not null && goldSessions.Contains(sourceMap[memory.SourceRef]))
                    {
                        goldDepth0.Add(memory);
                    }
                }
                var labeler = new DreamMicroEvidence(http, options.OpenAi, options.EvidenceModel,
                    budget.Scope(evaluator, question.QuestionId + "/evaluator"), baselineClock, apiKey);
                evidence = await labeler.LabelAsync(question, goldDepth0, token);
                BenchmarkFiles.WriteJson(evidencePath, evidence);
            }

            SetStage(directory, question.QuestionId, "daily-dream");
            await DreamMicroReplay.ReplayAsync(question.Sessions, question.QuestionDate, baseline, dream,
                consolidation, dreamClock, dreamCognition.EmbeddingSpace, token);
            SetStage(directory, question.QuestionId, "recall-remember-only");
            var baselineResult = await EvaluateAsync(question, "remember-only", directory, baseline,
                baselineEngine, baselineSearch, sourceMap, shared, evidence, token);
            SetStage(directory, question.QuestionId, "recall-daily-dream");
            var dreamResult = await EvaluateAsync(question, "daily-dream", directory, dream,
                dreamEngine, dreamSearch, sourceMap, new UsageTotals(0, 0, 0, 0, 0), evidence, token);
            var paired = new DreamMicroQuestionResult(question.QuestionId, question.QuestionType,
                question.Question, question.Answer, question.AnswerSessionIds, evidence, shared,
                baselineResult, dreamResult);
            BenchmarkFiles.WriteJson(resultPath, paired);
            SetStage(directory, question.QuestionId, "complete");
            return paired;
        }
        finally
        {
            BenchmarkUsage.ExportCalls(baseline, Path.Combine(directory, "remember-only-api-calls.jsonl"));
            BenchmarkUsage.ExportCalls(dream, Path.Combine(directory, "daily-dream-api-calls.jsonl"));
            BenchmarkUsage.ExportCalls(evaluator, Path.Combine(directory, "evaluator-api-calls.jsonl"));
            var sourceMap = BenchmarkReplay.CreateSourceMap(question.Sessions);
            var baselineSources = checked((int)baseline.BrowseMemories(new InspectionMemoryQuery()).Statistics.Sources);
            var dreamSources = checked((int)dream.BrowseMemories(new InspectionMemoryQuery()).Statistics.Sources);
            BenchmarkFiles.WriteJson(Path.Combine(directory, "partial-corpus.json"), new
            {
                planned_sources = question.Sessions.Count,
                remember_only_incomplete_sources = baseline.GetIncompleteSources().Count,
                daily_dream_incomplete_sources = dream.GetIncompleteSources().Count,
                remember_only = BenchmarkUsage.Morphology(baseline, sourceMap) with { Sources = baselineSources },
                daily_dream = BenchmarkUsage.Morphology(dream, sourceMap) with { Sources = dreamSources },
                pruning = DreamMicroMetrics.CapturePruning(dream)
            });
        }
    }

    private static async Task<DreamMicroConditionResult> EvaluateAsync(BenchmarkQuestion question,
        string condition, string directory, SqliteMemoryStore store, MemoryEngine engine, BenchmarkSearch search,
        IReadOnlyDictionary<string, string> sourceMap, UsageTotals shared, DreamMicroEvidenceArtifact evidence,
        CancellationToken token)
    {
        var recallPath = Path.Combine(directory, condition, "recall.json");
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
            recall = new RecallArtifact(search.LastCandidates, result.Memories, store.ReadSnapshot().Memories,
                sourceMap, BenchmarkUsage.Read(store, "recall").InputTokens - beforeTokens, search.LastTrace);
            BenchmarkFiles.WriteJson(recallPath, recall);
        }
        return new DreamMicroConditionResult(condition, recall, DreamMicroMetrics.Evaluate(recall, evidence),
            BenchmarkUsage.Morphology(store, sourceMap), BenchmarkUsage.Subtract(BenchmarkUsage.Read(store), shared),
            DreamMicroMetrics.CapturePruning(store));
    }

    private void WriteReport(IReadOnlyList<BenchmarkQuestion> questions, DreamMicroBudget budget, string status, string? error)
    {
        lock (reportGate)
        {
            var ordered = new List<DreamMicroQuestionResult>();
            foreach (var question in questions)
            {
                if (completed.TryGetValue(question.QuestionId, out var result))
                {
                    ordered.Add(result);
                }
            }
            DreamMicroReport.Write(options.OutputDirectory, ordered, questions.Count, status, budget.ReadUsageByOperation(), error);
            BenchmarkFiles.WriteJson(Path.Combine(options.OutputDirectory, "status.json"), new
            {
                status,
                completed_questions = ordered.Count,
                expected_questions = questions.Count,
                budget_usd = options.TotalBudgetUsd,
                usage = budget.ReadTotal(),
                error,
                updated_at = DateTimeOffset.UtcNow
            });
        }
    }

    private static void SetStage(string directory, string questionId, string stage)
    {
        BenchmarkFiles.WriteJson(Path.Combine(directory, "stage.json"), new
        {
            question_id = questionId,
            stage,
            updated_at = DateTimeOffset.UtcNow
        });
        Console.WriteLine($"{questionId}: {stage}");
    }

    private void FreezeManifest(IReadOnlyList<BenchmarkQuestion> questions)
    {
        var paths = new List<string>();
        foreach (var project in new[] { "LongJourney.Core", "LongJourney.OpenAI", "LongJourney.Benchmarks" })
        {
            paths.AddRange(Directory.GetFiles(Path.Combine("src", project), "*.cs"));
        }
        paths.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(path));
        }
        var selectionBytes = JsonSerializer.SerializeToUtf8Bytes(questions, JsonDefaults.Options);
        var manifest = JsonSerializer.Serialize(new
        {
            protocol = "dream-retrieval-micro-v1",
            options,
            implementation_sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
            selection_sha256 = Convert.ToHexStringLower(SHA256.HashData(selectionBytes)),
            source_commit = "fe28d0d",
            question_count = 8,
            session_limit = 10,
            recall_limit = 5,
            sampling = "type ordinal round robin; SHA256 UTF8 ID within type; nongold floor(i*(n-1)/(k-1)), singleton midpoint",
            replay_cutoff = "max(question_date,last_session_timestamp); only fully closed active days",
            shared_extraction = "once per source, identical D0 IDs/content/time/provenance/embeddings",
            primary_metric = "any answer-bearing gold D0 in selected@5 or derived_from ancestry; no evidence is failure",
            meditation = false,
            answer_generation = false,
            official_judge = false,
            pricing_verified_at = "2026-09-05",
            pricing_sources = new[] { "https://developers.openai.com/api/docs/models/gpt-5.6-terra", "https://developers.openai.com/api/docs/models/text-embedding-3-large" }
        }, JsonDefaults.Options);
        var manifestPath = Path.Combine(options.OutputDirectory, "manifest.json");
        if (File.Exists(manifestPath) && File.ReadAllText(manifestPath) != manifest)
        {
            throw new InputException("Micro manifest differs. Existing paid artifacts require their frozen implementation and settings.");
        }
        File.WriteAllText(manifestPath, manifest);
        BenchmarkFiles.WriteJson(Path.Combine(options.OutputDirectory, "selection.json"), questions);
        foreach (var path in paths)
        {
            var destination = Path.Combine(options.OutputDirectory, "implementation", path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination))
            {
                File.Copy(path, destination);
            }
        }
    }
}
