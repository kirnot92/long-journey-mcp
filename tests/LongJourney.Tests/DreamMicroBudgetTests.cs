using System.Net;
using LongJourney.Benchmarks;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed class DreamMicroBudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "longjourney-micro-budget-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T00:00:00Z");

    [Fact]
    public void AllScopesAndNonRunCallsShareOneCapAndKnownUsageReleasesUnusedReservation()
    {
        var budget = Budget();
        var firstStore = Store("first");
        var first = budget.Scope(firstStore, "q1/remember-only");
        var dreamStore = Store("second");
        var dreamRun = dreamStore.GetOrCreateRun(RunKind.Dream, Now.AddDays(-1), Now, Now, null);
        var second = budget.Scope(dreamStore, "q1/daily-dream");
        var evaluator = budget.Scope(Store("evaluator"), "q1/evaluator");
        var extraction = first.ReserveUsage(null, "test", "extraction", 12m, Now);
        var embedding = second.ReserveUsage(dreamRun.Id, "test", "embedding", 8m, Now);
        Assert.Equal(dreamRun.Id, embedding.RunId);
        Assert.NotEqual(extraction.Id, embedding.Id);
        Assert.Throws<BudgetExceededException>(() => evaluator.ReserveUsage(null, "test", "judge", 0.01m, Now));
        first.CompleteUsage(extraction.Id, new ApiUsage(100, 0, 20, 2m), Now);
        var judge = evaluator.ReserveUsage(null, "test", "judge", 10m, Now);
        second.CompleteUsage(embedding.Id, new ApiUsage(50, 0, 0, 1m), Now);
        evaluator.CompleteUsage(judge.Id, new ApiUsage(200, 0, 10, 3m), Now);
        Assert.Equal(new UsageTotals(6m, 0m, 350, 30, 3), budget.ReadTotal());
        Assert.Equal(new UsageTotals(2m, 0m, 100, 20, 1), BenchmarkUsage.Read(firstStore));
        Assert.Equal(1m, dreamStore.GetRunAccountedUsd(dreamRun.Id));
        Assert.Equal(3, budget.ReadUsageByOperation().Count);
        Assert.Equal(1m, budget.ReadUsageByOperation()["embedding"].SettledUsd);
        var path = Path.Combine(directory, "calls.jsonl");
        budget.ExportCalls(path);
        Assert.Equal(3, File.ReadAllLines(path).Length);
        Assert.Equal(budget.ReadTotal(), Budget().ReadTotal());
    }

    [Fact]
    public void AggregatesSameOperationAcrossScopesWithoutDoubleCountingLocalLedgers()
    {
        var budget = Budget();
        foreach (var scope in new[] { "q1/remember-only", "q2/daily-dream" })
        {
            var ledger = budget.Scope(Store(scope), scope);
            var call = ledger.ReserveUsage(null, "test", "embedding", 2m, Now);
            ledger.CompleteUsage(call.Id, new ApiUsage(10, 0, 0, 1m), Now);
        }
        Assert.Equal(new UsageTotals(2m, 0m, 20, 0, 2), budget.ReadUsageByOperation()["embedding"]);
        Assert.Equal(2, budget.ReadTotal().Calls);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotTogetherExceedCap()
    {
        var budget = Budget();
        var scopes = new[] { budget.Scope(Store("a"), "a"), budget.Scope(Store("b"), "b") };
        using var ready = new Barrier(2);
        var tasks = new List<Task<bool>>();
        foreach (var scope in scopes)
        {
            tasks.Add(Task.Run(() =>
            {
                ready.SignalAndWait();
                try
                {
                    scope.ReserveUsage(null, "test", "embedding", 11m, Now);
                    return true;
                }
                catch (BudgetExceededException)
                {
                    return false;
                }
            }));
        }
        var outcomes = await Task.WhenAll(tasks);
        Assert.NotEqual(outcomes[0], outcomes[1]);
        Assert.Equal(11m, budget.ReadTotal().ReservedUsd);
        Assert.Equal(1, budget.ReadTotal().Calls);
    }

    [Fact]
    public async Task RejectedReservationPreventsEmbeddingHttpCall()
    {
        var budget = Budget();
        var ledger = budget.Scope(Store("calls"), "q/remember-only");
        ledger.ReserveUsage(null, "test", "extraction", 20m, Now);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, new OpenAiOptions(), ledger, TimeProvider.System, () => "test-key");
        await Assert.ThrowsAsync<BudgetExceededException>(() => client.EmbedAsync("query", null, default));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(1, budget.ReadTotal().Calls);
    }

    [Fact]
    public async Task UnknownHttpFailureRetainsReservationAndBlocksReopening()
    {
        var budget = Budget();
        var ledger = budget.Scope(Store("calls"), "q/daily-dream");
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiClient(http, new OpenAiOptions(), ledger, TimeProvider.System, () => "test-key");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync("query", null, default));
        Assert.Equal(1, handler.Calls);
        Assert.True(budget.ReadTotal().ReservedUsd > 0);
        Assert.Equal(0m, budget.ReadTotal().SettledUsd);
        Assert.Throws<InvariantException>(() => Budget());
    }

    [Fact]
    public void LocalReservationFailureKeepsGlobalReservationAndBlocksReopening()
    {
        var budget = Budget();
        var ledger = budget.Scope(Store("calls"), "q/daily-dream");
        Assert.Throws<InvariantException>(() => ledger.ReserveUsage(999, "test", "abstraction", 1m, Now));
        Assert.Equal(1m, budget.ReadTotal().ReservedUsd);
        Assert.Throws<InvariantException>(() => Budget());
    }

    [Fact]
    public void UnknownZeroCostReservationStillBlocksReopening()
    {
        var budget = Budget();
        var ledger = budget.Scope(Store("calls"), "scope");
        ledger.ReserveUsage(null, "test", "embedding", 0m, Now);
        Assert.Equal(new UsageTotals(0m, 0m, 0, 0, 1), budget.ReadTotal());
        Assert.Throws<InvariantException>(() => Budget());
    }

    [Fact]
    public void CrossScopeCompletionCannotSettleAnotherScopesReservation()
    {
        var budget = Budget();
        var local = Store("shared");
        var first = budget.Scope(local, "first");
        var second = budget.Scope(local, "second");
        var call = first.ReserveUsage(null, "test", "embedding", 1m, Now);
        Assert.Throws<InvariantException>(() => second.CompleteUsage(call.Id, new ApiUsage(1, 0, 0, 0.5m), Now));
        Assert.Equal(1m, budget.ReadTotal().ReservedUsd);
        first.CompleteUsage(call.Id, new ApiUsage(1, 0, 0, 0.5m), Now);
        Assert.Equal(0.5m, budget.ReadTotal().SettledUsd);
    }

    [Fact]
    public void InvalidOrChangedCapIsRejectedAndExactCapIsAllowed()
    {
        Assert.Throws<InputException>(() => Budget(0m));
        Assert.Throws<InputException>(() => Budget(-1m));
        Assert.Throws<InputException>(() => Budget(20.01m));
        var budget = Budget(2m);
        Assert.Throws<InputException>(() => Budget(3m));
        var ledger = budget.Scope(Store("calls"), "scope");
        Assert.Throws<InputException>(() => ledger.ReserveUsage(null, "test", "embedding", -1m, Now));
        var call = ledger.ReserveUsage(null, "test", "embedding", 2m, Now);
        ledger.CompleteUsage(call.Id, new ApiUsage(1, 0, 0, 2m), Now);
        Assert.Throws<BudgetExceededException>(() => ledger.ReserveUsage(null, "test", "embedding", 0.01m, Now));
        Assert.Equal(2m, Budget(2m).ReadTotal().SettledUsd);
    }

    [Fact]
    public void CostAboveReservedMaximumIsRecordedBeforeStopping()
    {
        var budget = Budget();
        var local = Store("calls");
        var ledger = budget.Scope(local, "scope");
        var call = ledger.ReserveUsage(null, "test", "embedding", 1m, Now);
        Assert.Throws<InvariantException>(() => ledger.CompleteUsage(call.Id, new ApiUsage(1, 0, 0, 2m), Now));
        Assert.Equal(2m, budget.ReadTotal().SettledUsd);
        Assert.Equal(2m, BenchmarkUsage.Read(local).SettledUsd);
    }

    private DreamMicroBudget Budget(decimal cap = 20m) => new(Path.Combine(directory, "budget"), cap);
    private SqliteMemoryStore Store(string name) => new(new EngineOptions { DataDirectory = Path.Combine(directory, name) });

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
