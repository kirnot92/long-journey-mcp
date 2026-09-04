using System.Text.Json;
using System.Text.Json.Nodes;

namespace LongJourney.OpenAI;

public static class StructuredOutputSchema
{
    public static JsonObject Text(int maximumLength)
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["minLength"] = 1,
            ["maxLength"] = maximumLength
        };
    }

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
            ["type"] = "object",
            ["properties"] = fields,
            ["required"] = required,
            ["additionalProperties"] = false
        };
    }

    public static JsonObject Array(JsonObject item, int maximumItems)
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = item,
            ["maxItems"] = maximumItems
        };
    }

    public static void RequireObject(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OpenAI proposal must be an object.");
        }

        var actualFields = new HashSet<string>(StringComparer.Ordinal);
        var propertyCount = 0;
        foreach (var property in value.EnumerateObject())
        {
            propertyCount++;
            actualFields.Add(property.Name);
        }
        if (propertyCount != fields.Length || actualFields.Count != fields.Length)
        {
            throw new InvalidDataException("OpenAI proposal has missing, duplicate, or unexpected fields.");
        }
        foreach (var field in fields)
        {
            if (!value.TryGetProperty(field, out _))
            {
                throw new InvalidDataException("OpenAI proposal has missing, duplicate, or unexpected fields.");
            }
        }
    }

    // The array view is borrowed from the response document and consumed before that document is disposed.
    public static JsonElement ReadArray(JsonElement value, string name, int limit)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() > limit)
        {
            throw new InvalidDataException("OpenAI proposal has an invalid array.");
        }
        return array;
    }

    public static string ReadText(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("OpenAI proposal contains invalid text.");
        }
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength)
        {
            throw new InvalidDataException("OpenAI proposal contains invalid text.");
        }
        return text;
    }
}
