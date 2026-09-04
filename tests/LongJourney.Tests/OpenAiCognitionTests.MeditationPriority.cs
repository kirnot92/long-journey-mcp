using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Fact]
    public async Task MeditationPriorityUsesWorkKeysAndOriginalEvidenceWithTheCurrentRunsModelAndBudget()
    {
        var periodStart = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var relatedAt = periodStart.AddDays(2);
        var evidence = new MemoryRecord("evidence", 0, "The spare sensor stayed dry despite the humidity.",
            "source", [], [], relatedAt, 0, null, "test", 1, 1);
        var originalMemory = new MemoryRecord("pattern", 1, "Humidity may explain sensor condensation.", null,
            ["a", "b", "c"], [], periodStart.AddDays(-2), 1, null, "test", 3, 2);
        var changedMemory = new MemoryRecord("pattern", 1, originalMemory.Content, null,
            originalMemory.DerivedFrom, [new("evidence", RelationKind.Negative, relatedAt, 3)],
            originalMemory.CreatedAt, 1, null, "test", 3, 2);
        IReadOnlyList<MeditationPriorityCandidate> candidates =
        [
            new("carry:5:pattern", originalMemory, periodStart.AddDays(-7), periodStart, []),
            new("meditation:pattern", changedMemory, periodStart, periodStart.AddDays(7), [evidence])
        ];
        var options = new OpenAiOptions();
        options.Meditation.Model = "gpt-5.6-sol-priority";
        options.Meditation.ReasoningEffort = "medium";
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            var reservation = Assert.Single(ledger.Reservations);
            Assert.Equal(17, reservation.RunId);
            Assert.Equal("meditation_priority", reservation.Operation);
            Assert.Equal(options.Meditation.Model, reservation.Model);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(options.Meditation.Model, body.RootElement.GetProperty("model").GetString());
            Assert.Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            var format = body.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("meditation_priority", format.GetProperty("name").GetString());
            var keySchema = format.GetProperty("schema").GetProperty("properties").GetProperty("work_keys");
            Assert.Equal(2, keySchema.GetProperty("minItems").GetInt32());
            Assert.Equal(2, keySchema.GetProperty("maxItems").GetInt32());
            Assert.Equal(256, keySchema.GetProperty("items").GetProperty("maxLength").GetInt32());
            Assert.Contains("complete permutation", body.RootElement.GetProperty("instructions").GetString());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            var promptCandidates = input.RootElement.GetProperty("candidates");
            Assert.Equal(2, promptCandidates.GetArrayLength());
            Assert.Equal("carry:5:pattern", promptCandidates[0].GetProperty("work_key").GetString());
            Assert.Equal(periodStart.AddDays(-7), promptCandidates[0].GetProperty("period_start").GetDateTimeOffset());
            Assert.Equal(periodStart, promptCandidates[0].GetProperty("period_end").GetDateTimeOffset());
            Assert.Equal("pattern", promptCandidates[0].GetProperty("memory").GetProperty("id").GetString());
            Assert.Equal(0, promptCandidates[0].GetProperty("memory").GetProperty("outgoing_relations").GetArrayLength());
            var changedPrompt = promptCandidates[1].GetProperty("memory");
            Assert.Equal("pattern", changedPrompt.GetProperty("id").GetString());
            Assert.Equal(changedMemory.Content, changedPrompt.GetProperty("content").GetString());
            Assert.Equal(originalMemory.CreatedAt, changedPrompt.GetProperty("created_at").GetDateTimeOffset());
            var relation = changedPrompt.GetProperty("outgoing_relations")[0];
            Assert.Equal("evidence", relation.GetProperty("related_memory_id").GetString());
            Assert.Equal("negative", relation.GetProperty("kind").GetString());
            Assert.Equal(relatedAt, relation.GetProperty("related_at").GetDateTimeOffset());
            Assert.Equal(evidence.Content, relation.GetProperty("related_content").GetString());
            Assert.Equal(0, relation.GetProperty("related_depth").GetInt32());
            return Response("""{"work_keys":["meditation:pattern","carry:5:pattern"]}""", model: "gpt-5.6-sol-priority-snapshot");
        });
        using var http = new HttpClient(handler);

        var result = await Client(http, ledger, options).PrioritizeMeditationAsync(candidates, new(17), default);

        Assert.Equal(["meditation:pattern", "carry:5:pattern"], result.Value);
        Assert.Equal("gpt-5.6-sol-priority-snapshot", result.Model);
        Assert.Equal(1, handler.CallCount);
        Assert.True(Assert.Single(ledger.Completed).Usage.CostUsd > 0);
    }

    [Fact]
    public async Task MeditationPrioritySendsAllCandidatesInOneRequestBeyondRetrievalAndGraphLimits()
    {
        var candidates = new List<MeditationPriorityCandidate>();
        for (var index = 0; index < 4; index++)
        {
            candidates.Add(PriorityCandidate($"work:{index}", $"memory{index}"));
        }
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal(4, input.RootElement.GetProperty("candidates").GetArrayLength());
            return Response("""{"work_keys":["work:3","work:2","work:1","work:0"]}""");
        });
        using var http = new HttpClient(handler);
        var engine = new EngineOptions { SearchCandidates = 1, RecallLimit = 1, NeighborhoodSize = 3, MeditationGraphLimit = 3 };
        var client = new OpenAiCognition(http, new(), engine, ledger, TimeProvider.System, () => "test-key");

        var result = await client.PrioritizeMeditationAsync(candidates, new(18), default);

        Assert.Equal(["work:3", "work:2", "work:1", "work:0"], result.Value);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(ledger.Completed);
    }

    [Theory]
    [InlineData("{\"work_keys\":[\"work:1\",\"unknown\"]}")]
    [InlineData("{\"work_keys\":[\"work:1\",\"work:1\"]}")]
    [InlineData("{\"work_keys\":[\"work:1\"]}")]
    [InlineData("{\"work_keys\":[]}")]
    public async Task InvalidMeditationPriorityIsBilledAndRejectedWithoutFallback(string proposal)
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => Task.FromResult(Response(proposal)));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => Client(http, ledger).PrioritizeMeditationAsync(
            [PriorityCandidate("work:1"), PriorityCandidate("work:2", "m2")], new(19), default));

        Assert.Equal(1, handler.CallCount);
        Assert.True(Assert.Single(ledger.Completed).Usage.CostUsd > 0);
    }

    [Fact]
    public async Task EmptyMeditationPriorityDoesNotReserveOrCallNetwork()
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => throw new Exception("HTTP must not be called."));
        using var http = new HttpClient(handler);

        var result = await Client(http, ledger).PrioritizeMeditationAsync([], new(20), default);

        Assert.Empty(result.Value);
        Assert.Empty(ledger.Reservations);
        Assert.Empty(ledger.Completed);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task OneMeditationCandidateStillRequiresThePriorityPass()
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => Task.FromResult(Response("""{"work_keys":["only"]}""")));
        using var http = new HttpClient(handler);

        var result = await Client(http, ledger).PrioritizeMeditationAsync([PriorityCandidate("only")], new(21), default);

        Assert.Equal("only", Assert.Single(result.Value));
        Assert.Equal(1, handler.CallCount);
        Assert.Single(ledger.Completed);
    }

    [Fact]
    public async Task MeditationPriorityBudgetRejectionPrecedesTheNetworkCall()
    {
        var ledger = new Ledger { RejectReservation = true };
        using var handler = new Handler(_ => throw new Exception("HTTP must not be called."));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<BudgetExceededException>(() => Client(http, ledger).PrioritizeMeditationAsync(
            [PriorityCandidate("only")], new(22), default));

        Assert.Empty(ledger.Completed);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("key", 0)]
    public async Task InvalidMeditationCandidateIsRejectedBeforeBilling(string key, int depth)
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => throw new Exception("HTTP must not be called."));
        using var http = new HttpClient(handler);
        var candidate = PriorityCandidate(key) with { Memory = Memory("m1", depth) };

        await Assert.ThrowsAsync<InputException>(() => Client(http, ledger).PrioritizeMeditationAsync([candidate], new(23), default));

        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, handler.CallCount);
    }

    private static MeditationPriorityCandidate PriorityCandidate(string key, string memoryId = "m1")
    {
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        return new MeditationPriorityCandidate(key, Memory(memoryId, 1), start, start.AddDays(7), []);
    }
}
