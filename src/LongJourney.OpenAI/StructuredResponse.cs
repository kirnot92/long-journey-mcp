using System.Text.Json;

namespace LongJourney.OpenAI;

/// <summary>Owns the structured output document; dispose after reading the result.</summary>
public sealed record StructuredResponse(JsonDocument Document, string Model) : IDisposable
{
    public JsonElement RootElement => Document.RootElement;

    public void Dispose()
    {
        Document.Dispose();
    }
}
