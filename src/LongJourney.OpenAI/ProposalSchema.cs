using System.Text.Json;
using System.Text.Json.Nodes;

namespace LongJourney.OpenAI;

internal static class ProposalSchema
{
    public static JsonObject Text(int maximumLength) => new()
    {
        ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximumLength
    };

    public static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
    {
        var fields = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema) in properties)
        {
            fields[name] = schema;
            required.Add(name);
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = fields, ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    public static JsonObject Array(JsonObject item, int maximumItems) => new()
    {
        ["type"] = "array", ["items"] = item, ["maxItems"] = maximumItems
    };

    public static void RequireObject(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("OpenAI proposal must be an object.");
        var actual = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != fields.Length || actual.Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(x => !value.TryGetProperty(x, out _)))
            throw new InvalidDataException("OpenAI proposal has missing, duplicate, or unexpected fields.");
    }

    public static JsonElement[] ReadArray(JsonElement value, string name, int limit)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > limit)
            throw new InvalidDataException("OpenAI proposal has an invalid array.");
        return array.EnumerateArray().ToArray();
    }

    public static string ReadText(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Length > maximumLength)
            throw new InvalidDataException("OpenAI proposal contains invalid text.");
        return value.GetString()!;
    }
}
