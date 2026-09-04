using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Theory]
    [InlineData("{\"observations\":[]}", 0)]
    [InlineData("{\"observations\":[{\"content\":\"The spare sensor was dry during the check.\"},{\"content\":\"The main sensor showed condensation during the check.\"}]}", 2)]
    public async Task RememberPreservesRawAndAcceptsZeroOrMultipleObservations(string response, int expectedCount)
    {
        const string raw = "During a maintenance check:\n  the spare sensor stayed dry; the main sensor showed condensation.\n";
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal(raw, input.RootElement.GetProperty("raw").GetString());
            var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            Assert.Equal(2, schema.GetProperty("properties").GetProperty("observations").GetProperty("maxItems").GetInt32());
            return Response(response);
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCognition(http, new(), new EngineOptions { MaxObservations = 2 },
            ledger, TimeProvider.System, () => "test-key");

        var result = await client.ExtractAsync(raw, new(), default);

        Assert.Equal(expectedCount, result.Value.Count);
        if (expectedCount == 2)
        {
            Assert.Equal("The spare sensor was dry during the check.", result.Value[0].Content);
            Assert.Equal("The main sensor showed condensation during the check.", result.Value[1].Content);
        }
        Assert.Equal(1, handler.CallCount);
        Assert.Single(ledger.Completed);
    }
}
