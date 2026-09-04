using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkRunnerTests
{
    [Fact]
    public async Task VariantsUseIsolatedCorporaAndKeepRootInvariantThroughMeditation()
    {
        using var fixture = new Fixture();
        var report = await fixture.RunAsync();
        Assert.Equal(5, report.Completed);
        Assert.Equal(1, report.PairedQuestions);
        foreach (var result in report.Results)
        {
            Assert.Empty(result.Metrics!.Graph.InvariantFailures);
            Assert.Equal("complete", result.Status);
            if (result.Variant == BenchmarkVariant.FullHistory)
            {
                Assert.Empty(result.Metrics.Graph.DepthCounts);
                continue;
            }
            Assert.Equal(10, result.Metrics.Graph.DepthCounts[0]);
            if (result.Variant == BenchmarkVariant.Remember)
            {
                Assert.Equal(0, result.Metrics.DreamRuns);
                Assert.Single(result.Metrics.Graph.DepthCounts);
            }
            if (result.Variant == BenchmarkVariant.Dream)
            {
                Assert.Equal(0, result.Metrics.Graph.PositiveRelations);
                Assert.True(result.Metrics.Graph.DepthCounts[1] > 0);
            }
            if (result.Variant == BenchmarkVariant.Relations)
            {
                Assert.True(result.Metrics.Graph.PositiveRelations > 0);
                Assert.Equal(0, result.Metrics.MeditationRuns);
            }
            if (result.Variant == BenchmarkVariant.Meditation)
            {
                Assert.Equal(1, result.Metrics.MeditationRuns);
                Assert.True(result.Metrics.Graph.DepthCounts[2] > 0);
            }
        }
        Assert.Equal(5, fixture.AnswerCalls);
        Assert.Equal(5, fixture.JudgeCalls);

        var calls = fixture.ExtractCalls;
        var repeated = await fixture.RunAsync();
        Assert.Equal(5, repeated.Completed);
        Assert.Equal(calls, fixture.ExtractCalls);
        Assert.Equal(5, fixture.AnswerCalls);
        Assert.Equal(5, fixture.JudgeCalls);
    }

    [Theory]
    [InlineData("remember")]
    [InlineData("dream")]
    [InlineData("judge")]
    public async Task ResumeRestoresEventClockAndReusesPersistedAnswer(string failStage)
    {
        using var fixture = new Fixture();
        fixture.Options.Variants = [BenchmarkVariant.Relations];
        fixture.FailStage = failStage;
        var interrupted = await fixture.RunAsync();
        Assert.Equal("failed", Assert.Single(interrupted.Results).Status);
        var failedExtractCalls = fixture.ExtractCalls;
        var resumed = await fixture.RunAsync();
        Assert.Equal(1, resumed.Completed);
        var unit = Assert.Single(BenchmarkArtifacts.Units(
            fixture.Options, fixture.Options.SelectCases(fixture.Dataset)));
        var snapshot = BenchmarkArtifacts.Read<GraphSnapshot>(Path.Combine(unit.Directory, "graph.json"))!;
        foreach (var memory in snapshot.Memories)
        {
            if (memory.Depth == 0)
            {
                Assert.True(memory.CreatedAt == Fixture.Start || memory.CreatedAt == Fixture.Start.AddDays(2));
            }
        }
        var runs = BenchmarkArtifacts.Read<List<RunRecord>>(Path.Combine(unit.Directory, "runs.json"))!;
        Assert.Equal(Fixture.Start.Date.AddDays(1), runs[0].StartedAt.UtcDateTime);
        Assert.Empty(Assert.Single(resumed.Results).Metrics!.Graph.InvariantFailures);
        if (failStage == "judge")
        {
            Assert.Equal(failedExtractCalls, fixture.ExtractCalls);
            Assert.Equal(1, fixture.AnswerCalls);
            Assert.Equal(2, fixture.JudgeCalls);
        }
    }

    [Fact]
    public async Task GlobalStopIsResumableAndIsNotAWeeklyBudgetExhaustion()
    {
        using var fixture = new Fixture();
        fixture.Options.Variants = [BenchmarkVariant.Meditation];
        fixture.Options.ExperimentBudgetUsd = 0.001m;
        var report = await fixture.RunAsync();
        Assert.Equal("budget_exhausted", Assert.Single(report.Results).Status);
        Assert.Equal(0, report.Completed);
        Assert.Equal(0, report.Usage.Calls);
        Assert.Equal(0, Assert.Single(report.Results).Metrics!.ExhaustedMeditations);
        Assert.Equal(0, fixture.AnswerCalls);
        var again = await fixture.RunAsync();
        Assert.Equal(0, again.Usage.Calls);
    }

    [Fact]
    public async Task ChangedConfigurationCannotResetExistingExperimentBudget()
    {
        using var fixture = new Fixture();
        fixture.Options.Variants = [BenchmarkVariant.FullHistory];
        await fixture.RunAsync();
        fixture.Options.ExperimentBudgetUsd++;
        await Assert.ThrowsAsync<InputException>(() => fixture.RunAsync());
        Assert.Equal(1, fixture.AnswerCalls);
    }

    [Fact]
    public async Task FutureEvidenceFailsBeforeMemoryAnswerOrJudgeCalls()
    {
        using var fixture = new Fixture();
        fixture.Options.Variants = [BenchmarkVariant.FullHistory, BenchmarkVariant.Meditation];
        var original = fixture.Dataset.Cases[0];
        fixture.Dataset = new BenchmarkDataset("future", [
            original with { Question = original.Question with { At = Fixture.Start.AddDays(-1) } }
        ]);
        var report = await fixture.RunAsync();
        Assert.Equal(0, report.Completed);
        Assert.Equal(2, report.Results.Count);
        Assert.All(report.Results, result => Assert.Equal("invalid_timeline", result.Status));
        Assert.Equal(0, fixture.ExtractCalls);
        Assert.Equal(0, fixture.AnswerCalls);
        Assert.Equal(0, report.Usage.Calls);
    }

    [Fact]
    public async Task PairedScoresExcludeCasesMissingOneVariant()
    {
        using var fixture = new Fixture();
        fixture.FailStage = "remember";
        var report = await fixture.RunAsync();
        Assert.Equal(1, report.Completed);
        Assert.Equal(0, report.PairedQuestions);
        Assert.Equal(1, report.Variants[0].Score.Completed);
        Assert.Equal(0, report.Variants[0].PairedScore.Completed);
    }

    [Fact]
    public void AbstractionExecutionDateIsNotPresentedAsAnEventDate()
    {
        var memory = new MemoryRecord("m", 1, "event occurred yesterday", null,
            ["a", "b", "c"], [], Fixture.Start.AddDays(5), 1, null, "fake", 3, 4);
        var item = Assert.Single(BenchmarkEvidence.Recall([memory], 4000));
        Assert.Null(item.CreatedAt);
    }

    [Fact]
    public void InvalidModelsAndRawLimitsFailBeforeExperimentArtifacts()
    {
        using var fixture = new Fixture();
        fixture.Options.AnswerModel.OutputUsdPerMillion = 0;
        Assert.Throws<InputException>(fixture.Options.Validate);
        fixture.Options.AnswerModel.OutputUsdPerMillion = 12;
        fixture.Options.MaxRawCharacters = 1001;
        Assert.Throws<InputException>(fixture.Options.Validate);
        Assert.False(Directory.Exists(fixture.Options.OutputDirectory));
    }

    private sealed class Fixture : IDisposable
    {
        public static DateTimeOffset Start => new(2023, 5, 1, 9, 0, 0, TimeSpan.Zero);
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "benchmark-runner-" + Guid.NewGuid().ToString("N"));
        public BenchmarkOptions Options { get; }
        public BenchmarkDataset Dataset { get; set; }
        public string? FailStage { get; set; }
        public int ExtractCalls { get; private set; }
        public int AnswerCalls { get; private set; }
        public int JudgeCalls { get; private set; }

        public Fixture()
        {
            Options = new BenchmarkOptions
            {
                DatasetPath = Path.Combine(_directory, "input.json"),
                OutputDirectory = Path.Combine(_directory, "experiment"),
                ExperimentBudgetUsd = 100
            };
            var turns = new List<BenchmarkTurn>();
            var observations = new List<BenchmarkObservation>();
            for (var index = 0; index < 10; index++)
            {
                var at = index == 9 ? Start.AddDays(2) : Start;
                var raw = $"A recorded experience number {index}.";
                turns.Add(new BenchmarkTurn($"session-{index}", 0, at, "user", raw));
                observations.Add(new BenchmarkObservation($"session-{index}", 0, 0, at, raw));
            }
            Dataset = new BenchmarkDataset("synthetic-v1", [
                new BenchmarkCase("case-1", new BenchmarkHistory(turns, observations),
                    new BenchmarkQuestion("What was recorded?", Start.AddDays(8)),
                    new BenchmarkReference("SECRET_REFERENCE_ONLY_FOR_JUDGE", "multi-session", false, ["session-0"]))
            ]);
        }

        public Task<BenchmarkReport> RunAsync()
        {
            var runner = new BenchmarkRunner(Options,
                (_, ledger, clock) => new Cognition(this, ledger, clock),
                (ledger, clock) => new LanguageModel(this, ledger, clock));
            return runner.RunAsync(Dataset);
        }

        private void FailOnce(string stage)
        {
            if (FailStage == stage)
            {
                FailStage = null;
                throw new IOException("synthetic interruption");
            }
        }

        private static void Charge(IUsageLedger ledger, TimeProvider clock, CallContext context, string operation)
        {
            var call = ledger.ReserveUsage(context.RunId, "fake", operation, 0.01m, clock.GetUtcNow());
            ledger.CompleteUsage(call.Id, new ApiUsage(1, 0, 1, 0.001m), clock.GetUtcNow());
        }

        private sealed class Cognition(Fixture fixture, IUsageLedger ledger, TimeProvider clock) : ICognition
        {
            public string EmbeddingSpace => "test:3";
            public Task<CognitiveResult<IReadOnlyList<ObservationProposal>>> ExtractAsync(
                string raw, CallContext context, CancellationToken cancellationToken)
            {
                Assert.DoesNotContain("SECRET_REFERENCE", raw);
                Charge(ledger, clock, context, "remember");
                fixture.ExtractCalls++;
                fixture.FailOnce("remember");
                return Task.FromResult(new CognitiveResult<IReadOnlyList<ObservationProposal>>(
                    [new ObservationProposal(raw)], "fake"));
            }
            public Task<EmbeddingVector> EmbedAsync(string text, CallContext context, CancellationToken cancellationToken)
            {
                Charge(ledger, clock, context, "embedding");
                return Task.FromResult(new EmbeddingVector(EmbeddingSpace, [1, 0.5f, 0.2f]));
            }
            public Task<CognitiveResult<IReadOnlyList<string>>> SelectAsync(string query, string? context,
                IReadOnlyList<MemoryRecord> candidates, CallContext call, CancellationToken cancellationToken)
            {
                Charge(ledger, clock, call, "recall");
                return Task.FromResult(new CognitiveResult<IReadOnlyList<string>>(MemoryTestData.Ids(candidates), "fake"));
            }
            public Task<CognitiveResult<IReadOnlyList<RelationProposal>>> AssimilateAsync(MemoryRecord observation,
                IReadOnlyList<MemoryRecord> candidates, CallContext context, CancellationToken cancellationToken)
            {
                Charge(ledger, clock, context, "assimilation");
                IReadOnlyList<RelationProposal> proposals = candidates.Count == 0 ? [] :
                    [new RelationProposal(candidates[0].Id, observation.Id, RelationKind.Positive)];
                return Task.FromResult(new CognitiveResult<IReadOnlyList<RelationProposal>>(proposals, "fake"));
            }
            public Task<CognitiveResult<IReadOnlyList<AbstractionProposal>>> AbstractAsync(
                IReadOnlyList<MemoryRecord> neighborhood, IReadOnlyList<SourceArtifact> sources,
                CognitionRole role, CallContext context, CancellationToken cancellationToken)
            {
                Charge(ledger, clock, context, role == CognitionRole.Dream ? "dream" : "meditation");
                if (role == CognitionRole.Dream)
                {
                    fixture.FailOnce("dream");
                }
                var parents = new List<string>();
                var depth = role == CognitionRole.Dream ? 0 : 1;
                foreach (var memory in neighborhood)
                {
                    if (memory.Depth == depth)
                    {
                        parents.Add(memory.Id);
                    }
                }
                IReadOnlyList<AbstractionProposal> proposals = parents.Count < 3 ? [] :
                    [new AbstractionProposal($"A supported pattern at depth {depth + 1}.", parents)];
                return Task.FromResult(new CognitiveResult<IReadOnlyList<AbstractionProposal>>(proposals, "fake"));
            }
        }

        private sealed class LanguageModel(Fixture fixture, IUsageLedger ledger, TimeProvider clock) : IBenchmarkLanguageModel
        {
            public Task<CognitiveResult<string>> AnswerAsync(string question, DateTimeOffset questionDate,
                IReadOnlyList<AnswerEvidence> evidence, CancellationToken cancellationToken)
            {
                foreach (var item in evidence)
                {
                    Assert.DoesNotContain("SECRET_REFERENCE", item.Content);
                }
                Charge(ledger, clock, new(), "benchmark_answer");
                fixture.AnswerCalls++;
                return Task.FromResult(new CognitiveResult<string>("Some experiences were recorded.", "fake"));
            }
            public Task<CognitiveResult<BenchmarkJudgment>> JudgeAsync(string question, string referenceAnswer,
                string questionType, bool isAbstention, string hypothesis, CancellationToken cancellationToken)
            {
                Assert.Equal("SECRET_REFERENCE_ONLY_FOR_JUDGE", referenceAnswer);
                Charge(ledger, clock, new(), "benchmark_judge");
                fixture.JudgeCalls++;
                fixture.FailOnce("judge");
                return Task.FromResult(new CognitiveResult<BenchmarkJudgment>(new(true, "synthetic judgment"), "fake"));
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
