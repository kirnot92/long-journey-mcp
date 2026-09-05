using System.Text.Json;
using LongJourney.Core;
using LongJourney.OpenAI;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Theory]
    [InlineData("{\"observations\":[]}", 0)]
    [InlineData("{\"observations\":[{\"content\":\"The spare sensor was dry during the check.\"}]}", 1)]
    [InlineData("{\"observations\":[{\"content\":\"The spare sensor was dry during the check.\"},{\"content\":\"The main sensor showed condensation during the check.\"}]}", 2)]
    [InlineData("{\"observations\":[{\"content\":\"The spare sensor was dry during the check.\"},{\"content\":\"The main sensor showed condensation during the check.\"},{\"content\":\"The operator requested a manual check next time.\"}]}", 3)]
    public async Task RememberPreservesRawAndAcceptsZeroOrMultipleObservations(string response, int expectedCount)
    {
        const string raw = "During a maintenance check:\n  the spare sensor stayed dry; the main sensor showed condensation.\nThe operator requested a manual check next time.\n";
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var input = JsonDocument.Parse(body.RootElement.GetProperty("input")[0].GetProperty("content").GetString()!);
            Assert.Equal(raw, input.RootElement.GetProperty("raw").GetString());
            var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            Assert.Equal(3, schema.GetProperty("properties").GetProperty("observations").GetProperty("maxItems").GetInt32());
            var instructions = body.RootElement.GetProperty("instructions").GetString();
            Assert.Contains("Return zero to 3 observations", instructions);
            Assert.Contains("cap, not a target", instructions);
            Assert.Contains("Preserve explicitly stated preferences and constraints", instructions);
            return Response(response);
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCognition(http, new(), new EngineOptions { MaxObservations = 3 },
            ledger, TimeProvider.System, () => "test-key");

        var result = await client.ExtractAsync(raw, new(), default);

        Assert.Equal(expectedCount, result.Value.Count);
        if (expectedCount >= 2)
        {
            Assert.Equal("The spare sensor was dry during the check.", result.Value[0].Content);
            Assert.Equal("The main sensor showed condensation during the check.", result.Value[1].Content);
        }
        Assert.Equal(1, handler.CallCount);
        Assert.Single(ledger.Completed);
    }

    [Fact]
    public async Task RememberUsesCustomObservationCapInPromptSchemaAndResponseValidation()
    {
        var ledger = new Ledger();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            Assert.Equal(1, schema.GetProperty("properties").GetProperty("observations").GetProperty("maxItems").GetInt32());
            Assert.Contains("Return zero to 1 observations", body.RootElement.GetProperty("instructions").GetString());
            return Response("""{"observations":[{"content":"First observation."},{"content":"Second observation."}]}""");
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCognition(http, new(), new EngineOptions { MaxObservations = 1 },
            ledger, TimeProvider.System, () => "test-key");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExtractAsync("Two observations.", new(), default));
        Assert.Single(ledger.Completed);
    }
}
