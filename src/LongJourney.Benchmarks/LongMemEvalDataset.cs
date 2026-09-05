using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LongJourney.Benchmarks;

public static class LongMemEvalDataset
{
    private static readonly JsonSerializerOptions SourceJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<BenchmarkQuestion> Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var questions = new List<BenchmarkQuestion>();
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var questionId = ReadString(item, "question_id");
            if (!questionIds.Add(questionId))
            {
                throw new InvalidDataException($"Duplicate LongMemEval question ID: {questionId}");
            }

            var questionDate = ParseTimestamp(ReadString(item, "question_date"));
            var sessions = ReadSessions(item);
            var answerSessions = new List<string>();
            foreach (var answerSession in item.GetProperty("answer_session_ids").EnumerateArray())
            {
                answerSessions.Add(answerSession.GetString()
                    ?? throw new InvalidDataException("A gold session ID is null."));
            }

            var answer = item.GetProperty("answer");
            questions.Add(new BenchmarkQuestion(
                questionId, ReadString(item, "question_type"), ReadString(item, "question"),
                answer.ValueKind == JsonValueKind.String ? answer.GetString()! : answer.GetRawText(),
                questionDate, answerSessions, sessions));
        }

        return questions;
    }

    private static IReadOnlyList<BenchmarkSession> ReadSessions(JsonElement question)
    {
        var histories = question.GetProperty("haystack_sessions");
        var dates = question.GetProperty("haystack_dates");
        var ids = question.GetProperty("haystack_session_ids");
        if (histories.GetArrayLength() != dates.GetArrayLength() ||
            histories.GetArrayLength() != ids.GetArrayLength())
        {
            throw new InvalidDataException("LongMemEval session histories, dates, and IDs have different lengths.");
        }

        var sessions = new List<(BenchmarkSession Session, int Ordinal)>();
        for (var ordinal = 0; ordinal < histories.GetArrayLength(); ordinal++)
        {
            var id = ids[ordinal].GetString()
                ?? throw new InvalidDataException("A session ID is null.");
            var timestamp = ParseTimestamp(dates[ordinal].GetString()
                ?? throw new InvalidDataException("A session timestamp is null."));
            var turns = new List<SessionTurn>();
            foreach (var turn in histories[ordinal].EnumerateArray())
            {
                // Copy only dialogue fields. Dataset annotations (including has_answer) never reach cognition.
                turns.Add(new SessionTurn(ReadString(turn, "role"), ReadString(turn, "content")));
            }

            // Session IDs may encode gold labels (answer_*). An opaque ordinal also prevents
            // exact-raw deduplication from collapsing distinct but identical dataset sessions.
            var raw = JsonSerializer.Serialize(new
            {
                session_ordinal = ordinal,
                timestamp = timestamp.ToString("O", CultureInfo.InvariantCulture),
                turns
            }, SourceJson);
            sessions.Add((new BenchmarkSession(id, timestamp, raw), ordinal));
        }

        sessions.Sort((left, right) =>
        {
            var timestampOrder = left.Session.Timestamp.CompareTo(right.Session.Timestamp);
            return timestampOrder != 0 ? timestampOrder : left.Ordinal.CompareTo(right.Ordinal);
        });
        var ordered = new List<BenchmarkSession>(sessions.Count);
        foreach (var session in sessions)
        {
            ordered.Add(session.Session);
        }

        return ordered;
    }

    private static string ReadString(JsonElement item, string name)
    {
        return item.GetProperty(name).GetString()
            ?? throw new InvalidDataException($"LongMemEval field {name} is null.");
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        // LongMemEval timestamps have no timezone. Both corpora use the same explicit UTC convention.
        if (!DateTimeOffset.TryParseExact(value, "yyyy/MM/dd (ddd) HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new InvalidDataException($"Invalid LongMemEval timestamp: {value}");
        }

        return timestamp;
    }

    private sealed record SessionTurn(string role, string content);
}
