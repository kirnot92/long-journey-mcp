using System.Security.Cryptography;
using System.Text;

namespace LongJourney.Benchmarks;

public static class DreamMicroSelection
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "58bf7951", "51a45a95", "e47becba", "118b2229"
    };

    public static IReadOnlyList<BenchmarkQuestion> Select(IReadOnlyList<BenchmarkQuestion> questions)
    {
        var groups = new SortedDictionary<string, List<RankedQuestion>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (!seen.Add(question.QuestionId))
            {
                throw new InvalidDataException("Micro benchmark input contains duplicate question IDs.");
            }
            if (Excluded.Contains(question.QuestionId))
            {
                continue;
            }
            if (!groups.TryGetValue(question.QuestionType, out var group))
            {
                group = [];
                groups.Add(question.QuestionType, group);
            }
            group.Add(new RankedQuestion(question, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(question.QuestionId)))));
        }
        foreach (var group in groups.Values)
        {
            group.Sort((left, right) =>
            {
                var comparison = StringComparer.Ordinal.Compare(left.Hash, right.Hash);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Question.QuestionId, right.Question.QuestionId);
            });
        }
        var selected = new List<BenchmarkQuestion>(8);
        for (var round = 0; selected.Count < 8; round++)
        {
            var added = false;
            foreach (var group in groups.Values)
            {
                if (round < group.Count && selected.Count < 8)
                {
                    selected.Add(TrimSessions(group[round].Question));
                    added = true;
                }
            }
            if (!added)
            {
                throw new InvalidDataException("Micro benchmark requires eight eligible questions.");
            }
        }
        return selected;
    }

    private static BenchmarkQuestion TrimSessions(BenchmarkQuestion question)
    {
        var goldIds = new HashSet<string>(question.AnswerSessionIds, StringComparer.Ordinal);
        if (goldIds.Count != question.AnswerSessionIds.Count)
        {
            throw new InvalidDataException($"Question {question.QuestionId} contains duplicate gold session IDs.");
        }
        var available = new HashSet<string>(StringComparer.Ordinal);
        var indexed = new List<IndexedSession>(question.Sessions.Count);
        for (var index = 0; index < question.Sessions.Count; index++)
        {
            var session = question.Sessions[index];
            if (!available.Add(session.SessionId))
            {
                throw new InvalidDataException($"Question {question.QuestionId} contains duplicate session IDs.");
            }
            indexed.Add(new IndexedSession(index, session));
        }
        if (!goldIds.IsSubsetOf(available) || goldIds.Count > 10)
        {
            throw new InvalidDataException($"Question {question.QuestionId} has missing gold sessions or more than ten gold sessions.");
        }
        indexed.Sort((left, right) =>
        {
            var comparison = left.Session.Timestamp.CompareTo(right.Session.Timestamp);
            return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
        });
        var distractors = new List<IndexedSession>();
        var selectedIds = new HashSet<string>(goldIds, StringComparer.Ordinal);
        foreach (var item in indexed)
        {
            if (!goldIds.Contains(item.Session.SessionId))
            {
                distractors.Add(item);
            }
        }
        var count = Math.Min(10 - goldIds.Count, distractors.Count);
        for (var index = 0; index < count; index++)
        {
            var position = count == 1 ? (distractors.Count - 1) / 2 : index * (distractors.Count - 1) / (count - 1);
            selectedIds.Add(distractors[position].Session.SessionId);
        }
        var sessions = new List<BenchmarkSession>(selectedIds.Count);
        foreach (var item in indexed)
        {
            if (selectedIds.Contains(item.Session.SessionId))
            {
                sessions.Add(item.Session);
            }
        }
        return question with { Sessions = sessions };
    }

    private sealed record RankedQuestion(BenchmarkQuestion Question, string Hash);
    private sealed record IndexedSession(int Index, BenchmarkSession Session);
}
