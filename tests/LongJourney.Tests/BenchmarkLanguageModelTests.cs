using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Benchmarks;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class BenchmarkLanguageModelTests
{
    [Fact]
    public async Task AnswerUsesIdenticalPromptWithoutEvaluationLabelsOrExpandedProvenance()
    {
        var payloads = new List<string>();
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            Assert.NotEmpty(ledger.Reservations);
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            var json = await request.Content!.ReadAsStringAsync();
            payloads.Add(json);
            using var body = JsonDocument.Parse(json);
            Assert.Equal("gpt-5.6-terra", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal(BenchmarkLanguageModel.AnswerInstructions, body.RootElement.GetProperty("instructions").GetString());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal("Question?", input.RootElement.GetProperty("question").GetString());
            Assert.Equal(Question().QuestionDate, input.RootElement.GetProperty("question_date").GetDateTimeOffset());
            var memory = Assert.Single(input.RootElement.GetProperty("memories").EnumerateArray());
            Assert.Equal("A selected memory.", memory.GetProperty("content").GetString());
            Assert.Equal(2, memory.GetProperty("depth").GetInt32());
            Assert.Equal(Question().QuestionDate, memory.GetProperty("created_at").GetDateTimeOffset());
            Assert.Equal(3, new List<JsonProperty>(memory.EnumerateObject()).Count);
            Assert.Equal(3, new List<JsonProperty>(input.RootElement.EnumerateObject()).Count);
            return JsonResponse("""
                {"model":"gpt-5.6-terra","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"answer\":\"Model answer.\"}"}]}],"usage":{"input_tokens":100,"output_tokens":20}}
                """);
        });
        using var http = new HttpClient(handler);
        var client = Client(http, ledger);
        var selected = new MemoryRecord("secret-memory-id", 2, "A selected memory.", null,
            ["secret-parent"], [new("secret-relation", RelationKind.Positive, Question().QuestionDate, 1)],
            Question().QuestionDate, 0, null, "secret-model", 9, 1);
        var answer = await client.AnswerAsync(Question(), [selected], default);
        var changedAnnotations = Question() with
        {
            QuestionId = "another-id_abs",
            QuestionType = "knowledge-update",
            Answer = "A different hidden gold answer.",
            AnswerSessionIds = ["another-gold-session"],
            Sessions = [new("another-gold-session", Question().QuestionDate, "A hidden raw source.")]
        };
        await client.AnswerAsync(changedAnnotations, [selected], default);
        Assert.Equal(payloads[0], payloads[1]);
        Assert.DoesNotContain("secret-", payloads[0]);
        Assert.DoesNotContain("Gold answer.", payloads[0]);
        Assert.Equal("Model answer.", answer.Hypothesis);
        Assert.Equal("gpt-5.6-terra", answer.Model);
        Assert.Equal(2, ledger.Completed.Count);
    }

    [Fact]
    public async Task AnswerRejectsMoreThanFiveMemoriesBeforePaidCall()
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected HTTP call."));
        using var http = new HttpClient(handler);
        var memory = new MemoryRecord("m", 0, "Memory.", "s", [], [], Question().QuestionDate, 0, null, "test", 1, 1);
        await Assert.ThrowsAsync<InputException>(() => Client(http, ledger).AnswerAsync(Question(),
            [memory, memory, memory, memory, memory, memory], default));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, handler.Calls);
    }

    // SHA256 fixtures were generated directly from the checked-out official
    // evaluate_qa.py:get_anscheck_prompt via Python AST, without importing its API client.
    [Theory]
    [InlineData("single-session-user", "0646abcd80e2569ad684c998e59d42357858ece8bfa892cd5ecabb401a785b5d")]
    [InlineData("single-session-assistant", "0646abcd80e2569ad684c998e59d42357858ece8bfa892cd5ecabb401a785b5d")]
    [InlineData("multi-session", "0646abcd80e2569ad684c998e59d42357858ece8bfa892cd5ecabb401a785b5d")]
    [InlineData("temporal-reasoning", "90aef51f7d30c7e63858c5b9e16034e56c37d596ef919ea7a80aa756e60ce0a5")]
    [InlineData("knowledge-update", "e413885e4d9b5810a1482e5547befb1a630e8937bc1206f8a7a97e597be9da49")]
    [InlineData("single-session-preference", "4b88d49eaa4f098103898d435d61590225ac8c66c9d8322ab71ca3a88d4f2335")]
    [InlineData("abstention", "71da46d1f79302d67211b8cdbe707cd125534b12972afc8291f9ea8abc9f9ca5")]
    public void JudgePromptsMatchOfficialEvaluatorExactly(string category, string expectedHash)
    {
        var question = Question() with
        {
            QuestionType = category,
            QuestionId = category == "abstention" ? "qid_abs_suffix" : "qid"
        };
        var prompt = BenchmarkLanguageModel.BuildJudgePrompt(question, "Model answer.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        Assert.Equal(expectedHash, hash);
    }

    [Fact]
    public async Task JudgeUsesOfficialChatSettingsAndSettlesCachedUsageBeforeScoring()
    {
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            var reservation = Assert.Single(ledger.Reservations);
            Assert.Equal("benchmark_judge", reservation.Operation);
            Assert.Equal("gpt-4o-2024-08-06", reservation.Model);
            Assert.Empty(ledger.Completed);
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("gpt-4o-2024-08-06", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal(0, payload.RootElement.GetProperty("temperature").GetInt32());
            Assert.Equal(10, payload.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("n").GetInt32());
            var message = Assert.Single(payload.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString());
            Assert.Equal(BenchmarkLanguageModel.BuildJudgePrompt(Question(), "Model answer."), message.GetProperty("content").GetString());
            return JudgeResponse("Yesterday");
        });
        using var http = new HttpClient(handler);
        var result = await Client(http, ledger).JudgeAsync(Question(), new("Model answer.", "test"), default);
        Assert.True(result.Correct); // Official evaluator deliberately uses substring 'yes'.
        Assert.Equal("Yesterday", result.Response);
        Assert.Equal("gpt-4o-2024-08-06", result.Model);
        var usage = Assert.Single(ledger.Completed);
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(200, usage.CachedInputTokens);
        Assert.Equal(2, usage.OutputTokens);
        Assert.Equal(0.00227m, usage.CostUsd);
        Assert.True(ledger.Reservations[0].ReservedUsd >= usage.CostUsd);
    }

    [Fact]
    public async Task InvalidJudgeOutputStillSettlesKnownUsage()
    {
        var ledger = new Ledger();
        using var http = new HttpClient(new Handler(_ => Task.FromResult(JsonResponse("""
            {"model":"gpt-4o-2024-08-06","choices":[{"message":{"content":null,"refusal":"hidden refusal body"}}],"usage":{"prompt_tokens":100,"completion_tokens":2}}
            """))));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Client(http, ledger)
            .JudgeAsync(Question(), new("Model answer.", "test"), default));
        Assert.Single(ledger.Completed);
        Assert.DoesNotContain("hidden refusal body", error.ToString());
    }

    [Theory]
    [InlineData(200, "malformed-secret-response")]
    [InlineData(429, "rate-limit-secret-response")]
    [InlineData(500, "service-secret-response")]
    [InlineData(200, "{\"choices\":[],\"usage\":{\"prompt_tokens\":10}}")]
    public async Task UnknownJudgeUsageRetainsReservationWithoutAutomaticRetry(int status, string responseBody)
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Client(http, ledger)
            .JudgeAsync(Question(), new("Model answer.", "test"), default));
        Assert.Single(ledger.Reservations);
        Assert.Empty(ledger.Completed);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(responseBody, error.ToString());
        Assert.DoesNotContain("test-key", error.ToString());
    }

    [Fact]
    public async Task MissingJudgeKeyDoesNotReserveOrCallNetwork()
    {
        var ledger = new Ledger();
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected HTTP call."));
        using var http = new HttpClient(handler);
        var client = new BenchmarkLanguageModel(http, new(), ledger, TimeProvider.System, () => null);
        await Assert.ThrowsAsync<InputException>(() => client.JudgeAsync(Question(), new("Answer.", "test"), default));
        Assert.Empty(ledger.Reservations);
        Assert.Equal(0, handler.Calls);
    }

    private static BenchmarkQuestion Question() => new("qid", "multi-session", "Question?", "Gold answer.",
        DateTimeOffset.Parse("2024-01-02T03:04:05Z"), ["gold-session"], []);

    private static BenchmarkLanguageModel Client(HttpClient http, Ledger ledger) =>
        new(http, new(), ledger, TimeProvider.System, () => "test-key");

    private static HttpResponseMessage JudgeResponse(string response) => JsonResponse(JsonSerializer.Serialize(new
    {
        model = "gpt-4o-2024-08-06",
        choices = new[] { new { message = new { content = response } } },
        usage = new { prompt_tokens = 1000, completion_tokens = 2, prompt_tokens_details = new { cached_tokens = 200 } }
    }));

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request);
        }
    }

    private sealed class Ledger : IUsageLedger
    {
        public List<UsageReservation> Reservations { get; } = [];
        public List<ApiUsage> Completed { get; } = [];
        public UsageReservation ReserveUsage(long? runId, string model, string operation, decimal maximumUsd, DateTimeOffset now)
        {
            var reservation = new UsageReservation(Guid.NewGuid().ToString("N"), runId, model, operation, maximumUsd);
            Reservations.Add(reservation);
            return reservation;
        }
        public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now) => Completed.Add(usage);
    }
}
