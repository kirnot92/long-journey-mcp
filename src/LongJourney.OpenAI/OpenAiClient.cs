using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LongJourney.Core;

namespace LongJourney.OpenAI;

/// <summary>Direct structured Responses and Embeddings calls with shared authentication and usage accounting.</summary>
public sealed class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;
    private readonly IUsageLedger _ledger;
    private readonly TimeProvider _time;
    private readonly Func<string?> _apiKey;
    private readonly Uri _baseUri;

    public OpenAiClient(HttpClient http, OpenAiOptions options, IUsageLedger ledger,
        TimeProvider time, Func<string?>? apiKeyAccessor = null)
    {
        ValidateOptions(options);
        _http = http;
        _options = options;
        _ledger = ledger;
        _time = time;
        _apiKey = apiKeyAccessor ?? (() => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        _baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public string EmbeddingSpace => _options.EmbeddingSpace;

    public async Task<StructuredResponse> RespondAsync(ModelOptions model, string operation, string instructions,
        object data, JsonObject schema, long? runId, CancellationToken cancellationToken)
    {
        OpenAiPricing.ValidateModel(model, operation);
        var payload = BuildResponsePayload(model, operation, instructions, data, schema);
        using var request = CreateRequest("responses", payload);
        cancellationToken.ThrowIfCancellationRequested();

        var reservation = _ledger.ReserveUsage(runId, model.Model, operation,
            OpenAiPricing.Reserve(model, MaximumInputTokens(payload)), _time.GetUtcNow());
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ReadDocumentAsync(response, cancellationToken);

        // Refusal, incomplete output and invalid proposals still incur known usage.
        // Failures before accounting retain the reservation because usage is unknown.
        AccountResponseUsage(document.RootElement, model, reservation);
        return ParseStructuredResponse(document.RootElement);
    }

    public async Task<EmbeddingVector> EmbedAsync(string text, long? runId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = _options.EmbeddingModel,
            input = text,
            dimensions = _options.EmbeddingDimensions,
            encoding_format = "float"
        });
        using var request = CreateRequest("embeddings", payload);
        cancellationToken.ThrowIfCancellationRequested();

        var reservation = _ledger.ReserveUsage(runId, _options.EmbeddingModel, "embedding",
            MaximumInputTokens(payload) * _options.EmbeddingInputUsdPerMillion / 1_000_000m, _time.GetUtcNow());
        using var response = await SendAsync(request, cancellationToken);
        using var document = await ReadDocumentAsync(response, cancellationToken);

        // Known usage is charged even when the returned vector fails validation.
        AccountEmbeddingUsage(document.RootElement, reservation);
        return ParseEmbeddingVector(document.RootElement);
    }

    private static byte[] BuildResponsePayload(ModelOptions model, string operation, string instructions,
        object data, JsonObject schema)
    {
        var userMessage = new JsonObject
        {
            ["role"] = "user",
            ["content"] = JsonSerializer.Serialize(data, JsonDefaults.Options)
        };
        var structuredFormat = new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = operation,
            ["strict"] = true,
            ["schema"] = schema
        };
        var body = new JsonObject
        {
            ["model"] = model.Model,
            ["store"] = false,
            ["max_output_tokens"] = model.MaxOutputTokens,
            ["service_tier"] = "default",
            ["instructions"] = instructions,
            ["input"] = new JsonArray(userMessage),
            ["text"] = new JsonObject
            {
                ["format"] = structuredFormat
            }
        };
        if (!string.IsNullOrWhiteSpace(model.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject
            {
                ["effort"] = model.ReasoningEffort
            };
        }
        // GPT-5.6 supports explicit mode with no breakpoints: no cache writes for changing single-turn payloads.
        if (model.Model.StartsWith("gpt-5.6", StringComparison.Ordinal))
        {
            body["prompt_cache_options"] = new JsonObject
            {
                ["mode"] = "explicit"
            };
        }
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private void AccountResponseUsage(JsonElement response, ModelOptions model, UsageReservation reservation)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI returned missing or invalid token usage; usage reservation is retained.");
        }
        var inputTokens = RequiredTokens(usage, "input_tokens");
        var outputTokens = RequiredTokens(usage, "output_tokens");
        var cachedTokens = 0L;
        var cacheWriteTokens = 0L;
        if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
        {
            cachedTokens = OptionalTokens(details, "cached_tokens");
            cacheWriteTokens = OptionalTokens(details, "cache_write_tokens");
        }
        var costUsd = OpenAiPricing.Calculate(model, inputTokens, cachedTokens, cacheWriteTokens, outputTokens);
        var accountedUsage = new ApiUsage(inputTokens, cachedTokens, outputTokens, costUsd, cacheWriteTokens);
        _ledger.CompleteUsage(reservation.Id, accountedUsage, _time.GetUtcNow());
    }

    private void AccountEmbeddingUsage(JsonElement response, UsageReservation reservation)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI returned missing or invalid token usage; usage reservation is retained.");
        }
        var tokens = RequiredTokens(usage, "prompt_tokens");
        var costUsd = tokens * _options.EmbeddingInputUsdPerMillion / 1_000_000m;
        var accountedUsage = new ApiUsage(tokens, 0, 0, costUsd);
        _ledger.CompleteUsage(reservation.Id, accountedUsage, _time.GetUtcNow());
    }

    private static StructuredResponse ParseStructuredResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "completed")
        {
            throw new InvalidDataException("OpenAI response did not complete; no proposal was applied.");
        }
        if (!response.TryGetProperty("model", out var responseModel) || responseModel.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(responseModel.GetString()))
        {
            throw new InvalidDataException("OpenAI response has no model provenance.");
        }
        if (!response.TryGetProperty("output", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OpenAI response has no output.");
        }

        var texts = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("OpenAI response contains an invalid output item.");
            }
            if (item.TryGetProperty("type", out var itemType))
            {
                if (itemType.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("OpenAI response contains an invalid output item.");
                }
                if (itemType.GetString() == "refusal")
                {
                    throw new InvalidDataException("OpenAI refused this cognitive operation.");
                }
            }
            if (!item.TryGetProperty("content", out var blocks))
            {
                continue;
            }
            if (blocks.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("OpenAI response contains invalid output content.");
            }

            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("OpenAI response contains an invalid output block.");
                }
                if (!block.TryGetProperty("type", out var type))
                {
                    continue;
                }
                if (type.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("OpenAI response contains an invalid output block.");
                }
                if (type.GetString() == "refusal")
                {
                    throw new InvalidDataException("OpenAI refused this cognitive operation.");
                }
                if (type.GetString() == "output_text")
                {
                    if (!block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("OpenAI response contains invalid output text.");
                    }
                    texts.Add(text.GetString()!);
                }
            }
        }
        if (texts.Count != 1 || string.IsNullOrWhiteSpace(texts[0]))
        {
            throw new InvalidDataException("OpenAI response must contain one structured result.");
        }
        try
        {
            return new StructuredResponse(JsonDocument.Parse(texts[0]), responseModel.GetString()!);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("OpenAI returned malformed proposal JSON.");
        }
    }

    private EmbeddingVector ParseEmbeddingVector(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("model", out var model) ||
            model.ValueKind != JsonValueKind.String || model.GetString() != _options.EmbeddingModel)
        {
            throw new InvalidDataException("Embedding response uses a different model.");
        }
        if (!response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1)
        {
            throw new InvalidDataException("OpenAI returned an invalid embedding shape.");
        }

        var embeddingEntry = data[0];
        if (embeddingEntry.ValueKind != JsonValueKind.Object ||
            !embeddingEntry.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
            !index.TryGetInt32(out var entryIndex) || entryIndex != 0 ||
            !embeddingEntry.TryGetProperty("embedding", out var vector) || vector.ValueKind != JsonValueKind.Array ||
            vector.GetArrayLength() != _options.EmbeddingDimensions)
        {
            throw new InvalidDataException("OpenAI returned an invalid embedding shape.");
        }

        var values = new float[vector.GetArrayLength()];
        var valueIndex = 0;
        foreach (var value in vector.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var component))
            {
                throw new InvalidDataException("OpenAI returned an invalid embedding vector.");
            }
            values[valueIndex] = component;
            valueIndex++;
        }
        var hasNonzeroValue = false;
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("OpenAI returned an invalid embedding vector.");
            }
            if (value != 0)
            {
                hasNonzeroValue = true;
            }
        }
        if (!hasNonzeroValue)
        {
            throw new InvalidDataException("OpenAI returned an invalid embedding vector.");
        }
        return new EmbeddingVector(EmbeddingSpace, values);
    }

    private HttpRequestMessage CreateRequest(string path, byte[] payload)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InputException("An OpenAI API key is required before using cognitive operations.");
        }
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        // Unknown HTTP/network failures retain their reservation. Retrying is an explicit scheduler decision.
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"OpenAI request failed with HTTP {(int)status}; no proposal was applied.", null, status);
        }
        return response;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("OpenAI returned malformed response JSON; usage reservation is retained.");
        }
    }

    private static long MaximumInputTokens(byte[] serializedPayload)
    {
        return checked(serializedPayload.LongLength + 8192L);
    }

    private static long RequiredTokens(JsonElement usage, string name)
    {
        if (usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var tokens) || tokens < 0)
        {
            throw new InvalidDataException("OpenAI returned missing or invalid token usage; usage reservation is retained.");
        }
        return tokens;
    }

    private static long OptionalTokens(JsonElement usage, string name)
    {
        if (usage.TryGetProperty(name, out _))
        {
            return RequiredTokens(usage, name);
        }
        return 0;
    }

    public static void ValidateOptions(OpenAiOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
            uri.AbsolutePath.TrimEnd('/') != "/v1" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
        {
            throw new InputException("OpenAI BaseUrl must be the direct https://api.openai.com/v1/ endpoint.");
        }
        if (options.TimeoutSeconds < 1 || string.IsNullOrWhiteSpace(options.EmbeddingModel) ||
            options.EmbeddingDimensions < 1 || options.EmbeddingInputUsdPerMillion <= 0)
        {
            throw new InputException("Invalid OpenAI embedding or timeout configuration.");
        }
        foreach (var role in Enum.GetValues<CognitionRole>())
        {
            var model = options.For(role);
            OpenAiPricing.ValidateModel(model, role.ToString());
        }
    }
}
