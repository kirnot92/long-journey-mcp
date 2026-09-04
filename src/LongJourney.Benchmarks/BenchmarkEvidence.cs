using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record SourceSession(string SourceId, string SessionId, int TurnIndex, int PartIndex);

public static class BenchmarkEvidence
{
    public static IReadOnlyList<SourceSession> MapSources(BenchmarkHistory history)
    {
        var mappings = new List<SourceSession>();
        foreach (var observation in history.Observations)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observation.Raw)));
            mappings.Add(new SourceSession("src_" + hash, observation.SessionId,
                observation.TurnIndex, observation.PartIndex));
        }
        return mappings;
    }

    public static IReadOnlyList<AnswerEvidence> FullHistory(BenchmarkHistory history, int maximumCharacters)
    {
        var evidence = new List<AnswerEvidence>();
        var characters = 2;
        foreach (var turn in history.Turns)
        {
            // Dataset session IDs can encode evidence labels. Use neutral sequential IDs in prompts.
            var item = new AnswerEvidence($"turn-{evidence.Count + 1}", $"{turn.Role}: {turn.Content}", turn.At, null);
            characters = checked(characters + SerializedCharacters(item) + 1);
            if (characters > maximumCharacters)
            {
                throw new InputException("Full history exceeds the configured context limit; no history was truncated.");
            }
            evidence.Add(item);
        }
        return evidence;
    }

    public static IReadOnlyList<AnswerEvidence> Recall(
        IReadOnlyList<MemoryRecord> recalled, int maximumCharacters)
    {
        var evidence = new List<AnswerEvidence>();
        var characters = 2;
        foreach (var memory in recalled)
        {
            var item = new AnswerEvidence(memory.Id, memory.Content, memory.Depth == 0 ? memory.CreatedAt : null, memory.Depth);
            var size = SerializedCharacters(item) + 1;
            if (characters + size > maximumCharacters)
            {
                continue;
            }
            characters += size;
            evidence.Add(item);
        }
        return evidence;
    }

    private static int SerializedCharacters(AnswerEvidence item) =>
        JsonSerializer.Serialize(item, JsonDefaults.Options).Length;
}
