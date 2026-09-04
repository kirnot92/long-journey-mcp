using LongJourney.Benchmarks;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed class BenchmarkBudgetTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly BenchmarkUsage EmptyUsage = new(0, 0, 0, 0, 0, 0, 0, 0);

    [Fact]
    public void GlobalCapIncludesEveryCorpusAndUnknownUsageAfterRestart()
    {
        using var fixture = new BudgetFixture();
        var paid = fixture.First.ReserveUsage(null, "fake", "benchmark_answer", 0.8m, Day);
        fixture.First.CompleteUsage(paid.Id, new ApiUsage(100, 20, 40, 0.2m, 10), Day);
        fixture.Second.ReserveUsage(null, "fake", "benchmark_judge", 0.6m, Day);
        var budget = new BenchmarkBudget(fixture.DatabasePaths, 1m);
        var first = budget.ForCorpus(fixture.First);

        Assert.Equal(new BenchmarkUsage(0.2m, 0.6m, 100, 40, 20, 10, 2, 1), budget.ReadUsage());
        var failure = Assert.Throws<ExperimentBudgetExceededException>(() =>
            first.ReserveUsage(null, "fake", "benchmark_answer", 0.21m, Day));
        Assert.IsNotType<BudgetExceededException>(failure);
        first.ReserveUsage(null, "fake", "benchmark_answer", 0.2m, Day);
        Assert.Equal(0.8m, budget.ReadUsage().ReservedUsd);
        Assert.Equal(3, budget.ReadUsage().Calls);

        var restarted = new BenchmarkBudget(fixture.DatabasePaths, 1m);
        var reopenedStore = new SqliteMemoryStore(fixture.SecondOptions);
        var second = restarted.ForCorpus(reopenedStore);
        Assert.Equal(budget.ReadUsage(), restarted.ReadUsage());
        Assert.Equal(BenchmarkBudget.ReadUsage(fixture.DatabasePaths), restarted.ReadUsage());
        Assert.Throws<ExperimentBudgetExceededException>(() =>
            second.ReserveUsage(null, "fake", "benchmark_judge", 0.01m, Day));
        Assert.Equal(2, restarted.ReadUsage().UnsettledCalls);
    }

    [Fact]
    public void KnownSettlementReleasesReservedCostAndCannotBeAppliedTwice()
    {
        using var fixture = new BudgetFixture();
        var budget = new BenchmarkBudget(fixture.DatabasePaths, 1m);
        var first = budget.ForCorpus(fixture.First);
        var second = budget.ForCorpus(fixture.Second);
        var reservation = first.ReserveUsage(null, "fake", "benchmark_answer", 0.9m, Day);
        Assert.Equal(0.9m, budget.ReadUsage().ReservedUsd);
        Assert.Throws<ExperimentBudgetExceededException>(() =>
            second.ReserveUsage(null, "fake", "benchmark_judge", 0.2m, Day));

        first.CompleteUsage(reservation.Id, new ApiUsage(1000, 200, 40, 0.1m, 100), Day);
        var settled = new BenchmarkUsage(0.1m, 0, 1000, 40, 200, 100, 1, 0);
        Assert.Equal(settled, budget.ReadUsage());
        first.CompleteUsage(reservation.Id, new ApiUsage(1, 0, 0, 0), Day);
        Assert.Equal(settled, budget.ReadUsage());
        second.ReserveUsage(null, "fake", "benchmark_judge", 0.9m, Day);
        Assert.Equal(0.1m, budget.ReadUsage().ActualUsd);
        Assert.Equal(0.9m, budget.ReadUsage().ReservedUsd);
        Assert.Equal(BenchmarkBudget.ReadUsage(fixture.DatabasePaths), budget.ReadUsage());
    }

    [Fact]
    public void WeeklyRejectionDoesNotAddPhantomCostToTheGlobalBudget()
    {
        using var fixture = new BudgetFixture();
        var run = fixture.First.GetOrCreateRun(RunKind.Meditation, Day.AddDays(-7), Day, Day, 0.25m);
        var budget = new BenchmarkBudget(fixture.DatabasePaths, 1m);
        var first = budget.ForCorpus(fixture.First);
        var second = budget.ForCorpus(fixture.Second);
        first.ReserveUsage(run.Id, "fake", "meditation", 0.2m, Day);

        Assert.Throws<BudgetExceededException>(() =>
            first.ReserveUsage(run.Id, "fake", "meditation", 0.1m, Day));
        Assert.Equal(new BenchmarkUsage(0, 0.2m, 0, 0, 0, 0, 1, 1), budget.ReadUsage());
        Assert.Equal(0.2m, fixture.First.GetRunAccountedUsd(run.Id));
        second.ReserveUsage(null, "fake", "benchmark_answer", 0.8m, Day);
        Assert.Equal(1m, budget.ReadUsage().ReservedUsd);
        Assert.Equal(2, budget.ReadUsage().Calls);
        Assert.Equal(BenchmarkBudget.ReadUsage(fixture.DatabasePaths), budget.ReadUsage());
    }

    [Fact]
    public async Task EmptyMeditationCompletesWithoutReservingOrCallingTheApi()
    {
        using var fixture = new BudgetFixture();
        var budget = new BenchmarkBudget(fixture.DatabasePaths, 1m);
        using var handler = new NoRequestsHandler();
        using var http = new HttpClient(handler);
        var clock = new ConsolidationClock { Now = Day };
        var cognition = new OpenAiCognition(http, new(), fixture.FirstOptions,
            budget.ForCorpus(fixture.First), clock, () => "fake-key");
        var search = new MemorySearch(fixture.First, cognition, fixture.FirstOptions);
        var engine = new ConsolidationEngine(fixture.First, cognition, search, fixture.FirstOptions, clock);

        var summary = await engine.MeditateAsync(Day.AddDays(-7), Day);

        Assert.Equal("complete", summary.Status);
        Assert.Equal(0m, summary.AccountedUsd);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(fixture.First.GetWorkItems(summary.RunId));
        Assert.Equal(EmptyUsage, budget.ReadUsage());
        Assert.Equal(EmptyUsage, BenchmarkBudget.ReadUsage(fixture.DatabasePaths));
    }

    [Fact]
    public void RegisteredCorpusCreatedAfterBudgetConstructionIsIncluded()
    {
        using var fixture = new BudgetFixture();
        var options = new EngineOptions { DataDirectory = Path.Combine(fixture.DirectoryPath, "later") };
        var path = Path.Combine(options.DataDirectory, "memory.db");
        var budget = new BenchmarkBudget([fixture.First.DatabasePath, path], 1m);
        Assert.Equal(EmptyUsage, budget.ReadUsage());
        var store = new SqliteMemoryStore(options);
        budget.ForCorpus(store).ReserveUsage(null, "fake", "benchmark_answer", 0.5m, Day);
        Assert.Equal(0.5m, budget.ReadUsage().ReservedUsd);
        Assert.Equal(BenchmarkBudget.ReadUsage([fixture.First.DatabasePath, path]), budget.ReadUsage());
    }

    [Fact]
    public void RegistrationOwnsItsPathsAndRejectsUnknownOrDuplicateDatabases()
    {
        using var fixture = new BudgetFixture();
        var paths = new List<string> { fixture.First.DatabasePath };
        var budget = new BenchmarkBudget(paths, 1m);
        paths.Clear();
        paths.Add(fixture.Second.DatabasePath);
        Assert.Throws<InputException>(() => budget.ForCorpus(fixture.Second));
        budget.ForCorpus(fixture.First).ReserveUsage(null, "fake", "benchmark_answer", 0.1m, Day);
        Assert.Equal(0.1m, budget.ReadUsage().ReservedUsd);
        var alias = Path.Combine(Path.GetDirectoryName(fixture.First.DatabasePath)!, ".", "memory.db");
        Assert.Throws<InputException>(() => new BenchmarkBudget([fixture.First.DatabasePath, alias], 1m));
        Assert.Throws<InputException>(() => new BenchmarkBudget([" "], 1m));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<InputException>(() => new BenchmarkBudget(
                [fixture.First.DatabasePath, fixture.First.DatabasePath.ToUpperInvariant()], 1m));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExperimentCapMustBePositive(int maximumUsd)
    {
        using var fixture = new BudgetFixture();
        Assert.Throws<InputException>(() => new BenchmarkBudget(fixture.DatabasePaths, maximumUsd));
    }

    private sealed class NoRequestsHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Empty meditation must not call the API.");
        }
    }

    private sealed class BudgetFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), "long-journey-benchmark-budget-tests", Guid.NewGuid().ToString("N"));
        public EngineOptions FirstOptions { get; }
        public EngineOptions SecondOptions { get; }
        public SqliteMemoryStore First { get; }
        public SqliteMemoryStore Second { get; }
        public IReadOnlyList<string> DatabasePaths { get; }

        public BudgetFixture()
        {
            FirstOptions = new EngineOptions
            {
                DataDirectory = Path.Combine(DirectoryPath, "first"),
                TimeZoneId = "UTC",
                MeditationBudgetUsd = 0.25m
            };
            SecondOptions = new EngineOptions { DataDirectory = Path.Combine(DirectoryPath, "second") };
            First = new SqliteMemoryStore(FirstOptions);
            Second = new SqliteMemoryStore(SecondOptions);
            DatabasePaths = [First.DatabasePath, Second.DatabasePath];
        }

        public void Dispose()
        {
            // This fixture owns the uniquely named absolute temporary directory created above.
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
