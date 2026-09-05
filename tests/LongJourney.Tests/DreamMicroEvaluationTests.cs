using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class DreamMicroEvaluationTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2024-01-01T00:00:00Z");

    [Fact]
    public void SelectionIsCategoryRoundRobinByHashIndependentOfInputOrderAndAnnotations()
    {
        var questions = new List<BenchmarkQuestion>();
        foreach (var category in new[] { "c", "a", "b" })
        {
            for (var index = 0; index < 4; index++)
            {
                questions.Add(Question(category + index, category));
            }
        }
        questions.Add(Question("58bf7951", "a"));
        var selected = DreamMicroSelection.Select(questions);
        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c", "a", "b" }, Categories(selected));
        foreach (var category in new[] { "a", "b", "c" })
        {
            var ordered = new List<string>();
            for (var index = 0; index < 4; index++)
            {
                ordered.Add(category + index);
            }
            ordered.Sort((left, right) => StringComparer.Ordinal.Compare(Hash(left), Hash(right)));
            var actual = new List<string>();
            foreach (var question in selected)
            {
                if (question.QuestionType == category)
                {
                    actual.Add(question.QuestionId);
                }
            }
            Assert.Equal(ordered.GetRange(0, actual.Count), actual);
        }
        questions.Reverse();
        Assert.Equal(Ids(selected), Ids(DreamMicroSelection.Select(questions)));
        Assert.DoesNotContain(selected, question => question.QuestionId == "58bf7951");
    }

    [Fact]
    public void SessionSelectionKeepsGoldAndUniformChronologicalEndpointsWithoutChangingRaw()
    {
        var sessions = new List<BenchmarkSession>();
        for (var index = 0; index < 20; index++)
        {
            sessions.Add(new BenchmarkSession("s" + index, Time.AddDays(index), " raw\r\n" + index + "  "));
        }
        var questions = EightQuestions(sessions, ["s5"]);
        var selected = DreamMicroSelection.Select(questions)[0];
        var actual = new List<string>();
        foreach (var session in selected.Sessions)
        {
            actual.Add(session.SessionId);
            Assert.Same(sessions[int.Parse(session.SessionId[1..])], session);
        }
        Assert.Equal(new[] { "s0", "s2", "s4", "s5", "s7", "s10", "s12", "s14", "s16", "s19" }, actual);
    }

    [Fact]
    public void SessionSelectionUsesMidpointForOneDistractorAndRetainsTimestampTies()
    {
        var sessions = new List<BenchmarkSession>();
        var gold = new List<string>();
        for (var index = 0; index < 14; index++)
        {
            sessions.Add(new("s" + index, Time, "raw" + index));
            if (index < 9)
            {
                gold.Add("s" + index);
            }
        }
        var selected = DreamMicroSelection.Select(EightQuestions(sessions, gold))[0];
        Assert.Equal(10, selected.Sessions.Count);
        for (var index = 0; index < 9; index++)
        {
            Assert.Same(sessions[index], selected.Sessions[index]);
        }
        Assert.Same(sessions[11], selected.Sessions[9]);
    }

    [Fact]
    public void MissingOrTooManyGoldSessionsFailsInsteadOfReplacingQuestion()
    {
        Assert.Throws<InvalidDataException>(() => DreamMicroSelection.Select(EightQuestions([], ["missing"])));
        Assert.Throws<InvalidDataException>(() => DreamMicroSelection.Select(EightQuestions([new("s", Time, "raw")], ["s", "s"])));
        var sessions = new List<BenchmarkSession>();
        var gold = new List<string>();
        for (var index = 0; index < 11; index++)
        {
            sessions.Add(new("s" + index, Time, "raw"));
            gold.Add("s" + index);
        }
        Assert.Throws<InvalidDataException>(() => DreamMicroSelection.Select(EightQuestions(sessions, gold)));
    }

    [Fact]
    public void MetricsRequireActualAnswerBearingD0AncestryAndExposePartialCoverage()
    {
        var gold = Memory("gold", "same-source");
        var gold2 = Memory("gold2", "other-source");
        var irrelevant = Memory("irrelevant", "same-source");
        var falseHit = Memory("false-hit", null, 1, ["irrelevant"], [new("gold", RelationKind.Positive, Time, 1)]);
        var trueHit = Memory("true-hit", null, 1, ["gold", "irrelevant"]);
        var graph = new[] { gold, gold2, irrelevant, falseHit, trueHit };
        var evidence = Evidence([gold, gold2], true);
        var miss = DreamMicroMetrics.Evaluate(new([falseHit, trueHit], [falseHit], graph, new Dictionary<string, string>(), 1), evidence);
        Assert.False(miss.HitAt5);
        Assert.True(miss.GoldInCandidates);
        Assert.True(miss.RecallSelectionFailure);
        Assert.Empty(Assert.Single(miss.SelectedMatches).GoldDepth0Ids);
        var hit = DreamMicroMetrics.Evaluate(new([trueHit], [trueHit], graph, new Dictionary<string, string>(), 1), evidence);
        Assert.True(hit.HitAt5);
        Assert.Equal(0.5m, hit.SelectedEvidenceCoverage);
        Assert.False(hit.AllEvidenceAt5);
        Assert.Equal("gold", Assert.Single(Assert.Single(hit.SelectedMatches).GoldDepth0Ids));
    }

    [Fact]
    public void EmptyPositiveEvidenceIsExtractionFailureForBothConditions()
    {
        var memory = Memory("m", "source");
        var metrics = DreamMicroMetrics.Evaluate(new([memory], [memory], [memory], new Dictionary<string, string>(), 1), Evidence([memory], false));
        Assert.True(metrics.RememberExtractionFailure);
        Assert.False(metrics.HitAt5);
        Assert.False(metrics.GoldInCandidates);
        Assert.False(metrics.AllEvidenceAt5);
        Assert.False(metrics.CandidateRetrievalFailure);
        Assert.False(metrics.RecallSelectionFailure);
    }

    [Fact]
    public async Task EvidenceLabelsCoverEveryD0AndPromptOmitsEvaluationIdentifiersAndSourceMetadata()
    {
        var ledger = new Ledger();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(payload.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal(3, new List<JsonProperty>(input.RootElement.EnumerateObject()).Count);
            Assert.Equal("Question?", input.RootElement.GetProperty("question").GetString());
            Assert.Equal("Answer.", input.RootElement.GetProperty("answer").GetString());
            var memory = Assert.Single(input.RootElement.GetProperty("memories").EnumerateArray());
            Assert.Equal(3, new List<JsonProperty>(memory.EnumerateObject()).Count);
            Assert.Equal("m", memory.GetProperty("id").GetString());
            Assert.DoesNotContain("secret-", input.RootElement.GetRawText());
            return Response("""{"judgments":[{"memory_id":"m","answer_bearing":true,"reason":"It states the needed fact."}]}""");
        }));
        var client = new DreamMicroEvidence(http, new(), new(), ledger, TimeProvider.System, () => "test");
        var result = await client.LabelAsync(Question("secret-question", "secret-type") with { AnswerSessionIds = ["secret-session"] },
            [Memory("m", "secret-source")], default);
        Assert.True(Assert.Single(result.Judgments).AnswerBearing);
        Assert.Equal("gpt-5.6-terra", result.Model);
        Assert.Single(result.OfferedDepth0);
        Assert.Single(ledger.Completed);
    }

    [Theory]
    [InlineData("{\"judgments\":[]}")]
    [InlineData("{\"judgments\":[{\"memory_id\":\"invented\",\"answer_bearing\":true,\"reason\":\"fact\"}]}")]
    [InlineData("{\"judgments\":[{\"memory_id\":\"m\",\"answer_bearing\":true,\"reason\":\" \"}]}")]
    [InlineData("{\"judgments\":[{\"memory_id\":\"m\",\"answer_bearing\":true,\"reason\":\"fact\"},{\"memory_id\":\"m\",\"answer_bearing\":false,\"reason\":\"other\"}]}")]
    public async Task InvalidEvidenceLabelsAreRejectedAfterKnownUsageIsSettled(string body)
    {
        var ledger = new Ledger();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Response(body))));
        var client = new DreamMicroEvidence(http, new(), new(), ledger, TimeProvider.System, () => "test");
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LabelAsync(Question(), [Memory("m", "source")], default));
        Assert.Single(ledger.Completed);
    }

    [Fact]
    public async Task NoExtractedGoldD0SkipsApiAndRetainsAbstention()
    {
        var ledger = new Ledger();
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No paid call expected.")));
        var client = new DreamMicroEvidence(http, new(), new(), ledger, TimeProvider.System, () => "test");
        var result = await client.LabelAsync(Question("q_abs"), [], default);
        Assert.True(result.IsDatasetAbstention);
        Assert.Empty(result.Judgments);
        Assert.Empty(ledger.Completed);
        Assert.Equal("evidence-no-depth0", result.Model);
    }

    [Fact]
    public async Task PruningDistinguishesImpossibleAndDuplicateWorkFromEmptySuccessfulCalls()
    {
        using var impossible = new ConsolidationFixture();
        var start = impossible.Clock.Now.AddDays(-1);
        impossible.Observations(2, start.AddHours(1));
        await impossible.Engine.DreamAsync(start, start.AddDays(1));
        Assert.Equal(new DreamMicroPruning(2, 1, 1, 0, 0, 0), DreamMicroMetrics.CapturePruning(impossible.Store));

        using var empty = new ConsolidationFixture();
        empty.Observations(3, start.AddHours(1));
        await empty.Engine.DreamAsync(start, start.AddDays(1));
        var reservation = empty.Store.ReserveUsage(null, "fake", "consolidation", 0.1m, empty.Clock.Now);
        empty.Store.CompleteUsage(reservation.Id, new ApiUsage(10, 0, 1, 0.01m), empty.Clock.Now);
        Assert.Equal(new DreamMicroPruning(3, 0, 2, 1, 1, 0), DreamMicroMetrics.CapturePruning(empty.Store));
    }

    [Fact]
    public void ReportRetainsPartialStatusPhysicalCostsAndActualEvidenceProvenance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "long-journey-micro-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gold = Memory("gold", "source");
            var other = Memory("other", "source");
            var abstraction = Memory("abstract", null, 1, ["gold", "other"]);
            var evidence = Evidence([gold], true);
            var graph = new[] { gold, other, abstraction };
            var baseline = new RecallArtifact([other], [other], graph, new Dictionary<string, string>(), 12);
            var dream = new RecallArtifact([abstraction], [abstraction], graph, new Dictionary<string, string>(), 25);
            var morphology = new CorpusMorphology(1, 2, 1, 0, 0, 0, 1, 0, new Dictionary<string, int> { ["source"] = 2 });
            var a = new DreamMicroConditionResult("Remember Only", baseline, DreamMicroMetrics.Evaluate(baseline, evidence),
                morphology, new(0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0));
            var b = new DreamMicroConditionResult("Daily Dream", dream, DreamMicroMetrics.Evaluate(dream, evidence),
                morphology, new(0, 0, 0, 0, 0), new(3, 0, 2, 1, 0, 1));
            var result = new DreamMicroQuestionResult("qid", "type", "Question?", "Answer.", ["session"], evidence,
                new(0.1m, 0, 10, 2, 1), a, b);
            DreamMicroReport.Write(directory, [result], 8, "budget_exhausted",
                new Dictionary<string, UsageTotals> { ["observations"] = new(0.1m, 0.2m, 10, 2, 2) });
            var report = File.ReadAllText(Path.Combine(directory, "report.md"));
            Assert.Contains("INCOMPLETE / INCONCLUSIVE", report);
            Assert.Contains("Dream wins: 1", report);
            Assert.Contains("derived_from [gold, other]; matching gold D0 [gold]", report);
            Assert.Contains("answer-bearing: True. Content gold", report);
            Assert.Contains("| Total | 2 | 0.1 | 0.2 |", report);
            using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "metrics.json")));
            Assert.False(metrics.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal("budget_exhausted", metrics.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static BenchmarkQuestion Question(string id = "q", string type = "a") =>
        new(id, type, "Question?", "Answer.", Time, [], []);
    private static MemoryRecord Memory(string id, string? source, int depth = 0,
        IReadOnlyList<string>? parents = null, IReadOnlyList<MemoryRelation>? relations = null) =>
        new(id, depth, "Content " + id, source, parents ?? [], relations ?? [], Time, 0, null, "test", 1, 1);
    private static List<BenchmarkQuestion> EightQuestions(IReadOnlyList<BenchmarkSession> sessions, IReadOnlyList<string> gold)
    {
        var questions = new List<BenchmarkQuestion>();
        for (var index = 0; index < 8; index++)
        {
            questions.Add(Question("q" + index) with { Sessions = sessions, AnswerSessionIds = gold });
        }
        return questions;
    }
    private static List<string> Ids(IReadOnlyList<BenchmarkQuestion> questions)
    {
        var ids = new List<string>();
        foreach (var question in questions)
        {
            ids.Add(question.QuestionId);
        }
        return ids;
    }
    private static List<string> Categories(IReadOnlyList<BenchmarkQuestion> questions)
    {
        var categories = new List<string>();
        foreach (var question in questions)
        {
            categories.Add(question.QuestionType);
        }
        return categories;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static DreamMicroEvidenceArtifact Evidence(IReadOnlyList<MemoryRecord> memories, bool bearing)
    {
        var offered = new List<DreamMicroEvidenceMemory>();
        var judgments = new List<DreamMicroEvidenceJudgment>();
        foreach (var memory in memories)
        {
            offered.Add(new(memory.Id, memory.Content, memory.CreatedAt));
            judgments.Add(new(memory.Id, bearing, "test reason"));
        }
        return new("test", offered, judgments, false, "test note");
    }
    private static HttpResponseMessage Response(string output) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = "gpt-5.6-terra",
            status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = output } } } },
            usage = new { input_tokens = 100, output_tokens = 20 }
        }), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
    private sealed class Ledger : IUsageLedger
    {
        public List<ApiUsage> Completed { get; } = [];
        public UsageReservation ReserveUsage(long? runId, string model, string operation, decimal maximumUsd, DateTimeOffset now) =>
            new(Guid.NewGuid().ToString("N"), runId, model, operation, maximumUsd);
        public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now) => Completed.Add(usage);
    }
}
