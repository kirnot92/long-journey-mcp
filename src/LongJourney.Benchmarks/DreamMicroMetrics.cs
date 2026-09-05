using System.Text.Json;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Benchmarks;

public static class DreamMicroMetrics
{
    public static DreamMicroRetrievalMetrics Evaluate(RecallArtifact recall, DreamMicroEvidenceArtifact evidence)
    {
        var gold = new HashSet<string>(StringComparer.Ordinal);
        foreach (var judgment in evidence.Judgments)
        {
            if (judgment.AnswerBearing)
            {
                gold.Add(judgment.MemoryId);
            }
        }
        var graph = new Dictionary<string, MemoryRecord>(StringComparer.Ordinal);
        foreach (var memory in recall.ProvenanceMemories)
        {
            graph.Add(memory.Id, memory);
        }
        foreach (var memory in recall.Candidates)
        {
            graph.TryAdd(memory.Id, memory);
        }
        foreach (var memory in recall.Selected)
        {
            graph.TryAdd(memory.Id, memory);
        }
        var selectedCoverage = new HashSet<string>(StringComparer.Ordinal);
        var candidateCoverage = new HashSet<string>(StringComparer.Ordinal);
        var selected = Match(recall.Selected, Math.Min(5, recall.Selected.Count), graph, gold, selectedCoverage);
        var candidates = Match(recall.Candidates, recall.Candidates.Count, graph, gold, candidateCoverage);
        var missingEvidence = gold.Count == 0;
        var hit = selectedCoverage.Count > 0;
        var candidateHit = candidateCoverage.Count > 0;
        return new DreamMicroRetrievalMetrics(hit, candidateHit,
            missingEvidence ? 0 : selectedCoverage.Count / (decimal)gold.Count,
            missingEvidence ? 0 : candidateCoverage.Count / (decimal)gold.Count,
            !missingEvidence && selectedCoverage.SetEquals(gold), !missingEvidence && candidateCoverage.SetEquals(gold),
            missingEvidence, !missingEvidence && !candidateHit, !missingEvidence && candidateHit && !hit,
            selected, candidates);
    }

    private static IReadOnlyList<DreamMicroMemoryMatch> Match(IReadOnlyList<MemoryRecord> memories, int count,
        Dictionary<string, MemoryRecord> graph, HashSet<string> gold, HashSet<string> coverage)
    {
        var matches = new List<DreamMicroMemoryMatch>(count);
        for (var index = 0; index < count; index++)
        {
            var matched = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(memories[index].Id);
            while (pending.TryPop(out var id))
            {
                if (!visited.Add(id))
                {
                    continue;
                }
                if (!graph.TryGetValue(id, out var memory))
                {
                    throw new InvalidDataException("A micro benchmark provenance parent is missing.");
                }
                if (memory.Depth == 0)
                {
                    if (gold.Contains(id))
                    {
                        matched.Add(id);
                    }
                    continue;
                }
                if (memory.DerivedFrom.Count == 0)
                {
                    throw new InvalidDataException("A micro benchmark abstraction has no provenance parents.");
                }
                foreach (var parent in memory.DerivedFrom)
                {
                    if (!graph.TryGetValue(parent, out var ancestor) || ancestor.Depth != memory.Depth - 1)
                    {
                        throw new InvalidDataException("Micro benchmark ancestry must contain every parent at the preceding depth.");
                    }
                    pending.Push(parent);
                }
            }
            var sorted = new List<string>(matched);
            sorted.Sort(StringComparer.Ordinal);
            coverage.UnionWith(matched);
            matches.Add(new DreamMicroMemoryMatch(memories[index].Id, sorted));
        }
        return matches;
    }

    public static DreamMicroPruning CapturePruning(SqliteMemoryStore store)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var work = db.CreateCommand();
        work.CommandText = """
            SELECT w.model, w.proposal_json
            FROM run_work w JOIN runs r ON r.id = w.run_id
            WHERE w.phase = 'consolidation' AND r.kind = 'dream'
            """;
        var total = 0;
        var impossible = 0;
        var duplicate = 0;
        var zero = 0;
        using (var reader = work.ExecuteReader())
        {
            while (reader.Read())
            {
                total++;
                var model = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (model == "consolidation-ineligible")
                {
                    impossible++;
                }
                else if (model == "dream-neighborhood-deduplicated")
                {
                    duplicate++;
                }
                else if (model is not null && !reader.IsDBNull(1))
                {
                    using var proposal = JsonDocument.Parse(reader.GetString(1));
                    if (proposal.RootElement.GetProperty("abstractions").GetArrayLength() == 0)
                    {
                        zero++;
                    }
                }
            }
        }
        using var calls = db.CreateCommand();
        calls.CommandText = "SELECT COUNT(*) FROM api_calls WHERE operation = 'consolidation'";
        var actualCalls = checked((int)(long)calls.ExecuteScalar()!);
        using var created = db.CreateCommand();
        created.CommandText = """
            SELECT COUNT(*) FROM memories m JOIN runs r ON r.id = m.dream_revision
            WHERE m.depth > 0 AND r.kind = 'dream'
            """;
        return new DreamMicroPruning(total, impossible, duplicate, actualCalls, zero,
            checked((int)(long)created.ExecuteScalar()!));
    }
}
