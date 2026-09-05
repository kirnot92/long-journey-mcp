using System.Net;
using System.Text;
using System.Text.Json;
using LongJourney.Benchmarks;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed class BenchmarkInfrastructureTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("5.01")]
    public void RejectsMeditationBudgetOutsideAuthorizedBound(string amount)
    {
        var options = new BenchmarkOptions
        {
            MeditationBudgetUsd = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)
        };
        Assert.Throws<InputException>(options.Validate);
    }

    [Fact]
    public async Task RestartAfterEmbeddingFailureReusesExtractedObservationsAndAccountsPendingCost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "longjourney-benchmark-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var corpus = Path.Combine(directory, "questions", "case", "remember-only");
            var engineOptions = new EngineOptions { DataDirectory = corpus, MaxObservations = 128 };
            var models = new OpenAiOptions { EmbeddingDimensions = 2 };
            var clock = new BenchmarkClock { UtcNow = DateTimeOffset.Parse("2023-01-01T12:00:00Z") };
            var store = new SqliteMemoryStore(engineOptions);
            using var handler = new InterruptedEmbeddingHandler();
            using var http = new HttpClient(handler);
            MemoryEngine CreateEngine()
            {
                var cognition = new CachedIngestionCognition(
                    new OpenAiCognition(http, models, engineOptions, store, clock, () => "test-key"),
                    Path.Combine(directory, "cache"));
                return new MemoryEngine(store, cognition, new MemorySearch(store, cognition, engineOptions), engineOptions, clock);
            }
            await Assert.ThrowsAsync<HttpRequestException>(() => CreateEngine().RememberAsync("complete session"));
            Assert.Equal("failed", Assert.Single(store.GetIncompleteSources()).Status);
            var result = await CreateEngine().RememberAsync("complete session");
            Assert.Single(result.Memories);
            Assert.Equal(1, handler.Extractions);
            Assert.Equal("The user plans a spring trip.", result.Memories[0].Content);
            var usage = BenchmarkUsage.Read(store);
            Assert.True(usage.SettledUsd > 0);
            Assert.True(usage.ReservedUsd > 0);
            BenchmarkExecutionStatus.Write(directory, "stopped");
            using var status = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "execution-status.json")));
            Assert.Equal(usage.SettledUsd, status.RootElement.GetProperty("physical_settled_usd").GetDecimal());
            Assert.Equal(usage.ReservedUsd, status.RootElement.GetProperty("physical_reserved_usd").GetDecimal());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private sealed class InterruptedEmbeddingHandler : HttpMessageHandler
    {
        public int Extractions { get; private set; }
        private int embeddings;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string response;
            if (request.RequestUri!.AbsolutePath.EndsWith("responses", StringComparison.Ordinal))
            {
                Extractions++;
                response = JsonSerializer.Serialize(new
                {
                    status = "completed",
                    model = "gpt-5.6-terra",
                    usage = new { input_tokens = 100, output_tokens = 30 },
                    output = new[] { new { type = "message", content = new[]
                    {
                        new { type = "output_text", text = "{\"observations\":[{\"content\":\"The user plans a spring trip.\"}]}" }
                    } } }
                });
            }
            else
            {
                embeddings++;
                if (embeddings == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                response = """
                    {"model":"text-embedding-3-large","usage":{"prompt_tokens":10},"data":[{"index":0,"embedding":[1,1]}]}
                    """;
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
