using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record BenchmarkDataset(string Sha256, IReadOnlyList<BenchmarkCase> Cases);

public sealed record BenchmarkCase(
    string Id,
    BenchmarkHistory History,
    BenchmarkQuestion Question,
    BenchmarkReference Reference);

public sealed record BenchmarkHistory(
    IReadOnlyList<BenchmarkTurn> Turns,
    IReadOnlyList<BenchmarkSession> Sessions);

public sealed record BenchmarkTurn(
    string SessionId, int TurnIndex, DateTimeOffset At, string Role, string Content);

public sealed record BenchmarkSession(string SessionId, DateTimeOffset At, string Raw);

public sealed record BenchmarkQuestion(string Text, DateTimeOffset At);

public sealed record BenchmarkReference(
    string Answer, string QuestionType, bool IsAbstention, IReadOnlyList<string> SessionIds);

public static class LongMemEvalDataset
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private const int MinimumRawCharacters = 43;
    private static readonly string[] DateFormats =
    [
        "yyyy/MM/dd '('ddd')' HH:mm",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    public static BenchmarkDataset Read(string path, int maxRawCharacters = 64_000)
    {
        ValidateRawLimit(maxRawCharacters);

        var bytes = File.ReadAllBytes(path);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            using var document = JsonDocument.Parse(bytes);
            RequireKind(document.RootElement, JsonValueKind.Array, "dataset");
            var cases = new List<BenchmarkCase>();
            var questionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var location = $"case[{cases.Count}]";
                RequireObject(item, location);
                var id = RequiredString(item, "question_id", location);
                if (!questionIds.Add(id))
                {
                    throw Invalid(location, "question_id is duplicated");
                }

                cases.Add(ReadCase(item, id, location, maxRawCharacters));
            }

            return new BenchmarkDataset(sha256, cases);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The benchmark dataset is not valid JSON.", exception);
        }
    }

    /// <summary>Hashes the complete file and expands only explicitly selected or ordinal-first questions.</summary>
    public static async Task<BenchmarkDataset> ReadSelectedAsync(
        string path, int maxRawCharacters, IReadOnlyList<string> questionIds, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRawLimit(maxRawCharacters);
        if (limit < 1)
        {
            throw new InputException("Question selection limit must be positive.");
        }
        if (questionIds is null)
        {
            throw new InputException("Question IDs must be a list.");
        }

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in questionIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !selectedIds.Add(id))
            {
                throw new InputException("Requested question IDs must be nonempty and unique.");
            }
        }

        // Keep one read handle across every pass so the checksum and selected contents cannot diverge.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        stream.Position = 0;
        try
        {
            var availableIds = new HashSet<string>(StringComparer.Ordinal);
            var originalIndex = 0;
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                stream, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = $"case[{originalIndex}]";
                RequireObject(item, location);
                var id = RequiredString(item, "question_id", location);
                if (!availableIds.Add(id))
                {
                    throw Invalid(location, "question_id is duplicated");
                }
                originalIndex++;
            }

            if (selectedIds.Count == 0)
            {
                var orderedIds = new List<string>(availableIds);
                orderedIds.Sort(StringComparer.Ordinal);
                if (orderedIds.Count > limit)
                {
                    orderedIds.RemoveRange(limit, orderedIds.Count - limit);
                }
                selectedIds.UnionWith(orderedIds);
            }
            else
            {
                foreach (var id in selectedIds)
                {
                    if (!availableIds.Contains(id))
                    {
                        throw new InputException("One or more requested question IDs are absent from the dataset.");
                    }
                }
            }
            if (selectedIds.Count == 0)
            {
                throw new InputException("No benchmark questions selected.");
            }

            stream.Position = 0;
            originalIndex = 0;
            var cases = new List<BenchmarkCase>();
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                stream, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = $"case[{originalIndex}]";
                var id = RequiredString(item, "question_id", location);
                if (selectedIds.Contains(id))
                {
                    // Unselected histories stay unexpanded; the first pass already validated every identity.
                    cases.Add(ReadCase(item, id, location, maxRawCharacters));
                    if (cases.Count == selectedIds.Count)
                    {
                        break;
                    }
                }
                originalIndex++;
            }
            cases.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
            return new BenchmarkDataset(sha256, cases);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The benchmark dataset is not valid JSON.", exception);
        }
    }

    private static void ValidateRawLimit(int maxRawCharacters)
    {
        // Reject unusably small raw limits before opening the dataset.
        if (maxRawCharacters < MinimumRawCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxRawCharacters), "Session raw length limit must be at least 43 UTF-16 characters.");
        }
    }

    /// <summary>Rejects future history before replay or answering without altering the source dataset.</summary>
    public static void ValidateTimeline(BenchmarkCase item)
    {
        foreach (var turn in item.History.Turns)
        {
            if (turn.At > item.Question.At)
            {
                throw new InputException("Benchmark history contains a turn after question_date.");
            }
        }

        foreach (var session in item.History.Sessions)
        {
            if (session.At > item.Question.At)
            {
                throw new InputException("Benchmark history contains a session after question_date.");
            }
        }
    }

    private static BenchmarkCase ReadCase(JsonElement item, string id, string location, int maxRawCharacters)
    {
        var questionType = RequiredString(item, "question_type", location);
        if (questionType is not ("single-session-user" or "single-session-assistant"
            or "single-session-preference" or "temporal-reasoning" or "knowledge-update" or "multi-session"))
        {
            throw Invalid(location, "question_type is unsupported");
        }

        var question = new BenchmarkQuestion(
            RequiredString(item, "question", location),
            ReadDate(RequiredString(item, "question_date", location), location + ".question_date"));
        var answerElement = RequiredProperty(item, "answer", location);
        var answer = answerElement.ValueKind switch
        {
            JsonValueKind.String => answerElement.GetString()!,
            // Preserve the culture-independent JSON number without rounding it through a floating-point type.
            JsonValueKind.Number => answerElement.GetRawText(),
            _ => throw Invalid(location, "answer must be a string or number")
        };
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw Invalid(location, "answer must not be empty");
        }

        var sessionIds = RequiredArray(item, "haystack_session_ids", location);
        var sessionDates = RequiredArray(item, "haystack_dates", location);
        var sessionContents = RequiredArray(item, "haystack_sessions", location);
        if (sessionIds.GetArrayLength() != sessionDates.GetArrayLength()
            || sessionIds.GetArrayLength() != sessionContents.GetArrayLength())
        {
            throw Invalid(location, "haystack session IDs, dates, and sessions must have aligned lengths");
        }

        var sessions = new List<Session>();
        var uniqueSessionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < sessionIds.GetArrayLength(); index++)
        {
            var sessionLocation = $"{location}.haystack_sessions[{index}]";
            var sessionId = ReadString(sessionIds[index], $"{location}.haystack_session_ids[{index}]");
            if (!uniqueSessionIds.Add(sessionId))
            {
                throw Invalid(location, "haystack_session_ids contains a duplicate");
            }

            var at = ReadDate(ReadString(sessionDates[index], sessionLocation + ".date"), sessionLocation + ".date");

            RequireKind(sessionContents[index], JsonValueKind.Array, sessionLocation);
            sessions.Add(new Session(sessionId, index, at, sessionContents[index]));
        }

        sessions.Sort(static (left, right) =>
        {
            var timestampOrder = left.At.CompareTo(right.At);
            return timestampOrder != 0 ? timestampOrder : left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var evidenceIds = RequiredArray(item, "answer_session_ids", location);
        var referenceIds = new List<string>();
        var uniqueEvidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceId in evidenceIds.EnumerateArray())
        {
            var sessionId = ReadString(evidenceId, location + ".answer_session_ids");
            if (!uniqueEvidenceIds.Add(sessionId))
            {
                throw Invalid(location, "answer_session_ids contains a duplicate");
            }

            if (!uniqueSessionIds.Contains(sessionId))
            {
                throw Invalid(location, "answer_session_ids refers to an absent history session");
            }

            referenceIds.Add(sessionId);
        }

        var turns = new List<BenchmarkTurn>();
        var historySessions = new List<BenchmarkSession>();
        foreach (var session in sessions)
        {
            if (session.Contents.GetArrayLength() == 0)
            {
                continue;
            }

            // One source retains the complete dialogue; IDs and gold labels never enter the raw input.
            var sessionRaw = new StringBuilder();
            sessionRaw.Append('[').Append(session.At.ToString(TimestampFormat, CultureInfo.InvariantCulture))
                .Append("] conversation");
            for (var turnIndex = 0; turnIndex < session.Contents.GetArrayLength(); turnIndex++)
            {
                var turnLocation = $"{location}.haystack_sessions[{session.OriginalIndex}][{turnIndex}]";
                var turnElement = session.Contents[turnIndex];
                RequireObject(turnElement, turnLocation);
                var role = RequiredString(turnElement, "role", turnLocation);
                if (role is not ("user" or "assistant"))
                {
                    throw Invalid(turnLocation, "role must be user or assistant");
                }

                if (turnElement.TryGetProperty("has_answer", out var hasAnswer)
                    && hasAnswer.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw Invalid(turnLocation, "has_answer must be a boolean when present");
                }

                var content = RequiredString(turnElement, "content", turnLocation);
                turns.Add(new BenchmarkTurn(session.Id, turnIndex, session.At, role, content));
                sessionRaw.Append("\n\n[turn ").Append((turnIndex + 1).ToString(CultureInfo.InvariantCulture))
                    .Append("] ").Append(role).Append('\n').Append(content);
            }
            if (sessionRaw.Length > maxRawCharacters)
            {
                throw new InputException(
                    $"Invalid benchmark dataset at {location}.haystack_sessions[{session.OriginalIndex}]: " +
                    $"complete session raw has {sessionRaw.Length} UTF-16 characters, exceeding max_raw_characters={maxRawCharacters}. " +
                    $"Increase max_raw_characters to at least {sessionRaw.Length} to preserve the complete session.");
            }
            historySessions.Add(new BenchmarkSession(session.Id, session.At, sessionRaw.ToString()));
        }

        return new BenchmarkCase(id, new BenchmarkHistory(turns, historySessions), question,
            new BenchmarkReference(answer, questionType, id.EndsWith("_abs", StringComparison.Ordinal), referenceIds));
    }

    private static DateTimeOffset ReadDate(string value, string location)
    {
        // Official timestamps omit a zone. UTC is the benchmark convention, independent of the host zone.
        if (!DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            throw Invalid(location, "timestamp must use yyyy/MM/dd (ddd) HH:mm or ISO 8601 with an explicit zone");
        }

        return at;
    }

    private static JsonElement RequiredProperty(JsonElement item, string property, string location)
    {
        if (!item.TryGetProperty(property, out var value))
        {
            throw Invalid(location, $"{property} is required");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement item, string property, string location)
    {
        var value = RequiredProperty(item, property, location);
        RequireKind(value, JsonValueKind.Array, location + "." + property);
        return value;
    }

    private static string RequiredString(JsonElement item, string property, string location) =>
        ReadString(RequiredProperty(item, property, location), location + "." + property);

    private static string ReadString(JsonElement value, string location)
    {
        RequireKind(value, JsonValueKind.String, location);
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(location, "must not be empty");
        }

        return text;
    }

    private static void RequireObject(JsonElement value, string location)
    {
        RequireKind(value, JsonValueKind.Object, location);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.Add(property.Name))
            {
                throw Invalid(location, $"property {property.Name} is duplicated");
            }
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string location)
    {
        if (value.ValueKind != expected)
        {
            throw Invalid(location, $"must be {expected}");
        }
    }

    private static InvalidDataException Invalid(string location, string reason) =>
        new($"Invalid benchmark dataset at {location}: {reason}.");

    private sealed record Session(string Id, int OriginalIndex, DateTimeOffset At, JsonElement Contents);
}
