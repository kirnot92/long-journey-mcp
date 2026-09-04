using System.Numerics;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record AbstractionSupport(string MemoryId, int Depth, int SourceRoots, int Sessions);
public sealed record BenchmarkGraphMetrics(
    IReadOnlyDictionary<int, int> DepthCounts, int PositiveRelations, int NegativeRelations,
    int DuplicateAbstractionContents, IReadOnlyList<AbstractionSupport> AbstractionSupport,
    IReadOnlyList<string> InvariantFailures);
public sealed record RetrievalCoverage(int ExpectedSessions, int RecoveredSessions, double Fraction);
public sealed record BenchmarkMetrics(
    BenchmarkGraphMetrics Graph, RetrievalCoverage? AdmittedSessionCoverage,
    int RecalledCount, int AdmittedCount, int DreamRuns, int MeditationRuns,
    int ExhaustedMeditations, int RejectedProposals);

public static class BenchmarkMeasurements
{
    public static BenchmarkMetrics Measure(
        IMemoryStore store, GraphSnapshot snapshot, IReadOnlyList<SourceSession> mappings,
        BenchmarkReference reference, BenchmarkVariant variant,
        IReadOnlyList<AnswerEvidence> admitted, int recalledCount, DateTimeOffset cutoff)
    {
        var sessionsBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            if (!sessionsBySource.TryGetValue(mapping.SourceId, out var sessions))
            {
                sessions = new HashSet<string>(StringComparer.Ordinal);
                sessionsBySource.Add(mapping.SourceId, sessions);
            }
            sessions.Add(mapping.SessionId);
        }
        var rootsByMemory = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ordered = new List<MemoryRecord>(snapshot.Memories);
        ordered.Sort((left, right) =>
        {
            var depth = left.Depth.CompareTo(right.Depth);
            return depth != 0 ? depth : string.CompareOrdinal(left.Id, right.Id);
        });
        var depths = new SortedDictionary<int, int>();
        var supports = new List<AbstractionSupport>();
        var failures = new List<string>();
        var abstractionContents = new HashSet<string>(StringComparer.Ordinal);
        var duplicateContents = 0;
        var positive = 0;
        var negative = 0;
        foreach (var memory in ordered)
        {
            depths.TryGetValue(memory.Depth, out var count);
            depths[memory.Depth] = count + 1;
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (memory.Depth == 0)
            {
                if (memory.SourceRef is null || memory.DerivedFrom.Count != 0)
                {
                    failures.Add($"{memory.Id}: invalid observation provenance.");
                }
                else
                {
                    roots.Add(memory.SourceRef);
                }
            }
            else
            {
                var distinctParents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parentId in memory.DerivedFrom)
                {
                    if (!distinctParents.Add(parentId) ||
                        !snapshot.ById.TryGetValue(parentId, out var parent) ||
                        parent.Depth != memory.Depth - 1 ||
                        !rootsByMemory.TryGetValue(parentId, out var parentRoots))
                    {
                        failures.Add($"{memory.Id}: invalid parent {parentId}.");
                        continue;
                    }
                    roots.UnionWith(parentRoots);
                }
                if (distinctParents.Count < 3 || new BigInteger(roots.Count) < BigInteger.Pow(3, memory.Depth))
                {
                    failures.Add($"{memory.Id}: insufficient geometric support.");
                }
                if (!abstractionContents.Add(memory.Content.Trim()))
                {
                    duplicateContents++;
                }
                supports.Add(new AbstractionSupport(
                    memory.Id, memory.Depth, roots.Count, Sessions(roots, sessionsBySource).Count));
            }
            if (roots.Count != memory.UniqueSourceRootCount)
            {
                failures.Add($"{memory.Id}: stored and traced root counts differ.");
            }
            if (memory.CreatedAt > cutoff)
            {
                failures.Add($"{memory.Id}: created after the question cutoff.");
            }
            rootsByMemory.Add(memory.Id, roots);
            foreach (var relation in memory.Relations)
            {
                if (!snapshot.ById.ContainsKey(relation.RelatedMemoryId) || relation.RelatedAt > cutoff)
                {
                    failures.Add($"{memory.Id}: invalid outgoing relation.");
                }
                if (relation.Kind == RelationKind.Positive)
                {
                    positive++;
                }
                else
                {
                    negative++;
                }
            }
        }
        var recoveredSessions = new HashSet<string>(StringComparer.Ordinal);
        if (variant == BenchmarkVariant.FullHistory)
        {
            foreach (var mapping in mappings)
            {
                recoveredSessions.Add(mapping.SessionId);
            }
        }
        else
        {
            foreach (var evidence in admitted)
            {
                if (rootsByMemory.TryGetValue(evidence.Id, out var roots))
                {
                    recoveredSessions.UnionWith(Sessions(roots, sessionsBySource));
                }
            }
        }
        RetrievalCoverage? coverage = null;
        if (!reference.IsAbstention && reference.SessionIds.Count > 0)
        {
            var recovered = 0;
            foreach (var session in reference.SessionIds)
            {
                if (recoveredSessions.Contains(session))
                {
                    recovered++;
                }
            }
            coverage = new RetrievalCoverage(
                reference.SessionIds.Count, recovered, (double)recovered / reference.SessionIds.Count);
        }
        var dreams = 0;
        var meditations = 0;
        var exhausted = 0;
        var rejected = 0;
        foreach (var run in store.GetRuns())
        {
            if (run.Kind == RunKind.Dream)
            {
                dreams++;
            }
            else
            {
                meditations++;
                if (run.Status == "budget_exhausted")
                {
                    exhausted++;
                }
            }
            rejected += store.GetRejectedProposalCount(run.Id);
        }
        return new BenchmarkMetrics(new BenchmarkGraphMetrics(
            depths, positive, negative, duplicateContents, supports, failures),
            coverage, recalledCount, admitted.Count, dreams, meditations, exhausted, rejected);
    }

    private static HashSet<string> Sessions(
        HashSet<string> roots, Dictionary<string, HashSet<string>> sessionsBySource)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (sessionsBySource.TryGetValue(root, out var sessions))
            {
                result.UnionWith(sessions);
            }
        }
        return result;
    }
}
