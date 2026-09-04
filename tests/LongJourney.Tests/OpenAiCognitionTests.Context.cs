using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Fact]
    public async Task RememberReceivesEntireDialogueAndAcceptsMultipleContextualObservations()
    {
        const string raw = "[2023-05-23] conversation\n\n[turn 1] user\nThat is longer.\n\n" +
                           "[turn 2] assistant\nHere is another term.\n\n[turn 3] user\nTry something better.\n\n" +
                           "[turn 4] assistant\n1. First alternative.\n2. Second alternative.";
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal(raw, input.RootElement.GetProperty("raw").GetString());
            var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            Assert.Equal(32, schema.GetProperty("properties").GetProperty("observations").GetProperty("maxItems").GetInt32());
            return Response("""
                {"observations":[
                  {"content":"The user asked for a better suggestion after another term was proposed."},
                  {"content":"The assistant then offered first alternative and second alternative together."}
                ]}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCognition(http, new(), new EngineOptions { MaxObservations = 32 },
            ledger, TimeProvider.System, () => "test-key");

        var result = await client.ExtractAsync(raw, new(), default);

        Assert.Equal(2, result.Value.Count);
        Assert.Equal("The user asked for a better suggestion after another term was proposed.", result.Value[0].Content);
        Assert.Equal("The assistant then offered first alternative and second alternative together.", result.Value[1].Content);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(ledger.Completed);
    }
}
