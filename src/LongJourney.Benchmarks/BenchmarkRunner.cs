using System.Diagnostics;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record BenchmarkResult(
    string QuestionId, BenchmarkVariant Variant, string QuestionType, string Status, string? ErrorType,
    CognitiveResult<string>? Answer, CognitiveResult<BenchmarkJudgment>? Judgment,
    BenchmarkMetrics? Metrics, BenchmarkUsage Usage, long ElapsedMilliseconds, bool IsAbstention = false);

/// <summary>The only writer of an experiment; its factories share the supplied budget ledger.</summary>
public sealed class BenchmarkRunner(
    BenchmarkOptions options,
    Func<EngineOptions, IUsageLedger, TimeProvider, ICognition> createCognition,
    Func<IUsageLedger, TimeProvider, IBenchmarkLanguageModel> createLanguageModel,
    Action<string>? log = null)
{
    public async Task<BenchmarkReport> RunAsync(BenchmarkDataset dataset, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var cases = options.SelectCases(dataset);
        var units = BenchmarkArtifacts.Units(options, cases);
        using var experimentLease = BenchmarkArtifacts.AcquireExperiment(options.OutputDirectory);
        PrepareManifest(dataset, units);
        var corpusLeases = new List<CorpusLease>();
        try
        {
            // Hold all registered corpora so another server cannot spend outside this experiment's guard.
            foreach (var unit in units)
            {
                corpusLeases.Add(new CorpusLease(options.CreateEngineOptions(unit.CorpusDirectory, unit.Variant)));
            }
            var budget = new BenchmarkBudget(BenchmarkArtifacts.DatabasePaths(units), options.ExperimentBudgetUsd);
            var casesById = new Dictionary<string, BenchmarkCase>(StringComparer.Ordinal);
            foreach (var item in cases)
            {
                casesById.Add(item.Id, item);
            }
            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log?.Invoke($"{unit.QuestionId} / {unit.Variant}");
                var result = await RunUnitAsync(casesById[unit.QuestionId], unit, budget, cancellationToken);
                BenchmarkArtifacts.Write(Path.Combine(unit.Directory, "result.json"), result);
                var report = BenchmarkReport.Create(units, budget.ReadUsage());
                BenchmarkArtifacts.Write(Path.Combine(options.OutputDirectory, "report.json"), report);
                if (result.Status is "budget_exhausted" or "failed" or "cancelled")
                {
                    break;
                }
            }
            var finalReport = BenchmarkReport.Create(units, budget.ReadUsage());
            BenchmarkArtifacts.Write(Path.Combine(options.OutputDirectory, "report.json"), finalReport);
            BenchmarkReport.ExportHypotheses(options.OutputDirectory, units);
            return finalReport;
        }
        finally
        {
            foreach (var lease in corpusLeases)
            {
                lease.Dispose();
            }
        }
    }

    private void PrepareManifest(BenchmarkDataset dataset, IReadOnlyList<BenchmarkUnit> units)
    {
        var path = Path.Combine(options.OutputDirectory, "manifest.json");
        var fingerprint = options.Fingerprint(dataset.Sha256);
        var existing = BenchmarkArtifacts.Read<ExperimentManifest>(path);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint)
            {
                throw new InputException("Dataset, configuration or implementation differs from this experiment. Use a new output directory.");
            }
            return;
        }
        // A directory with prior output must never gain a fresh budget by deleting just its manifest.
        foreach (var entry in Directory.EnumerateFileSystemEntries(options.OutputDirectory))
        {
            if (Path.GetFileName(entry) != ".experiment.lock")
            {
                throw new InputException("Output directory must be empty for a new experiment.");
            }
        }
        BenchmarkArtifacts.Write(path, new ExperimentManifest(
            fingerprint, dataset.Sha256, BenchmarkOptions.ProtocolVersion,
            BenchmarkLanguageModel.PromptVersion, DateTimeOffset.UtcNow, options, units));
    }

    private async Task<BenchmarkResult> RunUnitAsync(
        BenchmarkCase item, BenchmarkUnit unit, BenchmarkBudget budget, CancellationToken cancellationToken)
    {
        var engineOptions = options.CreateEngineOptions(unit.CorpusDirectory, unit.Variant);
        var store = new SqliteMemoryStore(engineOptions);
        var saved = store.GetState(BenchmarkReplay.ProgressKey);
        var progress = saved is null ? new BenchmarkProgress
        {
            Clock = item.History.Observations.Count == 0 ? item.Question.At : item.History.Observations[0].At
        } : JsonSerializer.Deserialize<BenchmarkProgress>(saved, JsonDefaults.Options)
            ?? throw new InvariantException("Invalid replay checkpoint.");
        var clock = new ReplayClock(progress.Clock);
        var ledger = budget.ForCorpus(store);
        var cognition = createCognition(engineOptions, ledger, clock);
        var search = new MemorySearch(store, cognition, engineOptions);
        var memory = new MemoryEngine(store, cognition, search, engineOptions, clock);
        var consolidation = new ConsolidationEngine(store, cognition, search, engineOptions, clock);
        var scheduler = new MemoryScheduler(store, consolidation, engineOptions, clock);
        var replay = new BenchmarkReplay(store, memory, scheduler, clock, progress);
        var languageModel = createLanguageModel(ledger, clock);
        var mappings = BenchmarkEvidence.MapSources(item.History);
        BenchmarkArtifacts.Write(Path.Combine(unit.Directory, "source-sessions.json"), mappings);
        var elapsed = Stopwatch.StartNew();
        try
        {
            try
            {
                LongMemEvalDataset.ValidateTimeline(item);
            }
            catch (InputException)
            {
                progress.Status = "invalid_timeline";
                progress.ErrorType = "FutureHistoryEvidence";
                return Finish();
            }
            if (progress.Status != "complete")
            {
                progress.Status = "running";
                progress.ErrorType = null;
                if (unit.Variant != BenchmarkVariant.FullHistory)
                {
                    await replay.IngestAsync(item.History, item.Question.At, cancellationToken);
                }
                else
                {
                    progress.Clock = item.Question.At;
                    clock.Now = item.Question.At;
                    progress.IngestionComplete = true;
                }
                if (progress.Evidence is null)
                {
                    if (unit.Variant == BenchmarkVariant.FullHistory)
                    {
                        progress.RecalledIds = [];
                        progress.Evidence = BenchmarkEvidence.FullHistory(
                            item.History, options.FullHistoryContextCharacters);
                    }
                    else
                    {
                        var recalled = await memory.RecallAsync(
                            item.Question.Text, $"Question date: {item.Question.At:O}", cancellationToken);
                        var ids = new List<string>();
                        foreach (var recalledMemory in recalled.Memories)
                        {
                            ids.Add(recalledMemory.Id);
                        }
                        progress.RecalledIds = ids;
                        progress.Evidence = BenchmarkEvidence.Recall(
                            recalled.Memories, options.RetrievalContextCharacters);
                    }
                    replay.Save();
                }
                if (progress.Answer is null)
                {
                    progress.Answer = await languageModel.AnswerAsync(
                        item.Question.Text, item.Question.At, progress.Evidence, cancellationToken);
                    replay.Save();
                }
                if (progress.Judgment is null)
                {
                    progress.Judgment = await languageModel.JudgeAsync(
                        item.Question.Text, item.Reference.Answer, item.Reference.QuestionType,
                        item.Reference.IsAbstention, progress.Answer.Value, cancellationToken);
                    replay.Save();
                }
                progress.Status = "complete";
            }
        }
        catch (ExperimentBudgetExceededException)
        {
            progress.Status = "budget_exhausted";
            progress.ErrorType = nameof(ExperimentBudgetExceededException);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            progress.Status = "cancelled";
            progress.ErrorType = nameof(OperationCanceledException);
        }
        catch (Exception error)
        {
            progress.Status = "failed";
            // HTTP response bodies, keys and external exception messages never enter artifacts.
            progress.ErrorType = error is HttpRequestException httpError
                ? $"HttpRequestException:{(int?)httpError.StatusCode}" : error.GetType().Name;
        }
        return Finish();

        BenchmarkResult Finish()
        {
            elapsed.Stop();
            progress.ElapsedMilliseconds += elapsed.ElapsedMilliseconds;
            var snapshot = store.ReadSnapshot();
            var metrics = BenchmarkMeasurements.Measure(store, snapshot, mappings,
                item.Reference, unit.Variant, progress.Evidence ?? [],
                progress.RecalledIds?.Count ?? 0, item.Question.At);
            if (progress.Status == "complete" && metrics.Graph.InvariantFailures.Count > 0)
            {
                progress.Status = "failed";
                progress.ErrorType = "GraphInvariantViolation";
            }
            replay.Save();
            BenchmarkArtifacts.Write(Path.Combine(unit.Directory, "graph.json"), snapshot);
            BenchmarkArtifacts.Write(Path.Combine(unit.Directory, "evidence.json"), progress.Evidence ?? []);
            BenchmarkArtifacts.Write(Path.Combine(unit.Directory, "runs.json"), store.GetRuns());
            return new BenchmarkResult(item.Id, unit.Variant, item.Reference.QuestionType,
                progress.Status, progress.ErrorType, progress.Answer, progress.Judgment,
                metrics, BenchmarkBudget.ReadUsage([unit.DatabasePath]), progress.ElapsedMilliseconds, item.Reference.IsAbstention);
        }
    }
}
