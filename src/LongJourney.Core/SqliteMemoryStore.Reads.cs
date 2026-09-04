using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

public sealed partial class SqliteMemoryStore
{
    public MemoryRecord? GetMemory(string id)
    {
        return ReadSingleMemory(new MemoryReadScope("m.id = $selected", id));
    }

    public MemoryRecord? GetAppliedAbstraction(long runId, string workKey, int proposalIndex)
    {
        var originKey = $"run:{runId}:{workKey}:{proposalIndex}";
        return ReadSingleMemory(new MemoryReadScope("m.origin_key = $selected", originKey));
    }

    public IReadOnlyList<MemoryRecord> GetSourceMemories(string sourceId)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var scope = new MemoryReadScope("m.source_ref = $selected", sourceId);
        var memories = ReadMemories(db, tx, scope);
        tx.Commit();

        return memories;
    }

    public RememberResult ReadRememberResult(string sourceId, bool duplicate)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var source = ReadSourceRow(db, tx, "id=$value", sourceId)
            ?? throw new InputException("Source not found.");
        var scope = new MemoryReadScope("m.source_ref = $selected", sourceId);
        var sourceMemories = ReadMemories(db, tx, scope);
        tx.Commit();

        return new RememberResult(sourceId, duplicate, sourceMemories, source.Status);
    }

    // One read transaction keeps memory rows, outgoing edges, and recall history mutually consistent.
    public GraphSnapshot ReadSnapshot(RunRecord? run = null)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var memoryHighWater = run?.MemoryHighWater ?? long.MaxValue;
        var relationHighWater = run?.RelationHighWater ?? long.MaxValue;
        var recallHighWater = run?.RecallHighWater ?? long.MaxValue;

        var scope = new MemoryReadScope(
            MemoryHighWater: memoryHighWater,
            RelationHighWater: relationHighWater);
        var memories = ReadMemories(db, tx, scope);
        var recalls = ReadRecallEvents(db, tx, recallHighWater);
        if (run is not null)
        {
            RestoreRecallTimes(memories, recalls);
        }

        tx.Commit();
        return new GraphSnapshot(memories, recalls);
    }

    private static List<RecallEvent> ReadRecallEvents(
        SqliteConnection db,
        SqliteTransaction tx,
        long recallHighWater)
    {
        using var command = CreateCommand(db, tx,
            "SELECT memory_id,recalled_at,seq FROM recall_events WHERE seq <= $max ORDER BY seq",
            ("$max", recallHighWater));
        using var reader = command.ExecuteReader();
        var recalls = new List<RecallEvent>();
        while (reader.Read())
        {
            recalls.Add(new RecallEvent(
                reader.GetString(0),
                ParseTimestamp(reader.GetString(1)),
                reader.GetInt64(2)));
        }

        return recalls;
    }

    private static void RestoreRecallTimes(
        List<MemoryRecord> memories,
        IReadOnlyList<RecallEvent> recalls)
    {
        // A frozen run must not see recalls recorded after its high-water mark.
        var latestRecallByMemory = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var recall in recalls)
        {
            if (!latestRecallByMemory.TryGetValue(recall.MemoryId, out var latestRecall) ||
                recall.RecalledAt > latestRecall)
            {
                latestRecallByMemory[recall.MemoryId] = recall.RecalledAt;
            }
        }

        for (var index = 0; index < memories.Count; index++)
        {
            var memory = memories[index];
            DateTimeOffset? lastRecalledAt = null;
            if (latestRecallByMemory.TryGetValue(memory.Id, out var recalledAt))
            {
                lastRecalledAt = recalledAt;
            }

            memories[index] = memory with
            {
                LastRecalledAt = lastRecalledAt
            };
        }
    }

    private static List<MemoryRecord> ReadMemories(
        SqliteConnection db,
        SqliteTransaction tx,
        MemoryReadScope scope)
    {
        var parentsByMemory = ReadParentIds(db, tx, scope);
        var relationsByMemory = ReadRelations(db, tx, scope);
        using var command = CreateCommand(db, tx, $"""
            SELECT m.id, m.depth, m.content, m.source_ref, m.created_at, m.dream_revision,
                m.last_recalled_at, m.created_by_model, m.seq,
                (SELECT COUNT(*) FROM memory_roots r WHERE r.memory_id = m.id)
            FROM memories m
            WHERE m.sealed = 1 AND m.seq <= $memory_max AND {scope.Predicate}
            ORDER BY m.seq
            """,
            ("$memory_max", scope.MemoryHighWater),
            ("$selected", scope.Value));
        using var reader = command.ExecuteReader();
        var memories = new List<MemoryRecord>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var depth = reader.GetInt32(1);
            var content = reader.GetString(2);
            var sourceRef = reader.IsDBNull(3) ? null : reader.GetString(3);
            var createdAt = ParseTimestamp(reader.GetString(4));
            var dreamRevision = reader.GetInt64(5);
            DateTimeOffset? lastRecalledAt = reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6));
            var createdByModel = reader.GetString(7);
            var sequence = reader.GetInt64(8);
            var uniqueSourceRootCount = reader.GetInt32(9);

            if (!parentsByMemory.TryGetValue(id, out var parentIds))
            {
                parentIds = [];
            }

            if (!relationsByMemory.TryGetValue(id, out var relations))
            {
                relations = [];
            }

            memories.Add(new MemoryRecord(
                id,
                depth,
                content,
                sourceRef,
                parentIds,
                relations,
                createdAt,
                dreamRevision,
                lastRecalledAt,
                createdByModel,
                uniqueSourceRootCount,
                sequence));
        }

        return memories;
    }

    private static Dictionary<string, List<string>> ReadParentIds(
        SqliteConnection db,
        SqliteTransaction tx,
        MemoryReadScope scope)
    {
        using var command = CreateCommand(db, tx, $"""
            SELECT d.child_id, d.parent_id
            FROM derived_from d
            JOIN memories m ON m.id = d.child_id
            WHERE m.sealed = 1 AND m.seq <= $memory_max AND {scope.Predicate}
            ORDER BY d.child_id, d.parent_id
            """,
            ("$memory_max", scope.MemoryHighWater),
            ("$selected", scope.Value));
        using var reader = command.ExecuteReader();
        var parentsByMemory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var childId = reader.GetString(0);
            var parentId = reader.GetString(1);
            if (!parentsByMemory.TryGetValue(childId, out var parentIds))
            {
                parentIds = [];
                parentsByMemory.Add(childId, parentIds);
            }

            parentIds.Add(parentId);
        }

        return parentsByMemory;
    }

    private static Dictionary<string, List<MemoryRelation>> ReadRelations(
        SqliteConnection db,
        SqliteTransaction tx,
        MemoryReadScope scope)
    {
        // The selection applies to the edge owner. Its target may be outside a point/source query.
        using var command = CreateCommand(db, tx, $"""
            SELECT r.memory_id, r.related_memory_id, r.kind, r.related_at, r.seq
            FROM relations r
            JOIN memories m ON m.id = r.memory_id
            JOIN memories target ON target.id = r.related_memory_id
            WHERE m.sealed = 1 AND m.seq <= $memory_max AND {scope.Predicate}
                AND r.seq <= $relation_max AND target.seq <= $memory_max
            ORDER BY r.seq
            """,
            ("$memory_max", scope.MemoryHighWater),
            ("$relation_max", scope.RelationHighWater),
            ("$selected", scope.Value));
        using var reader = command.ExecuteReader();
        var relationsByMemory = new Dictionary<string, List<MemoryRelation>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var memoryId = reader.GetString(0);
            var relatedMemoryId = reader.GetString(1);
            var kind = Enum.Parse<RelationKind>(reader.GetString(2), true);
            var relatedAt = ParseTimestamp(reader.GetString(3));
            var sequence = reader.GetInt64(4);
            if (!relationsByMemory.TryGetValue(memoryId, out var relations))
            {
                relations = [];
                relationsByMemory.Add(memoryId, relations);
            }

            relations.Add(new MemoryRelation(relatedMemoryId, kind, relatedAt, sequence));
        }

        return relationsByMemory;
    }

    private MemoryRecord? ReadSingleMemory(MemoryReadScope scope)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var memories = ReadMemories(db, tx, scope);
        tx.Commit();

        return memories.Count == 0 ? null : memories[0];
    }

    // Predicates are fixed application SQL; IDs and source values are always bound parameters.
    // All three reads use the same selection and transaction so a result cannot mix graph versions.
    private sealed record MemoryReadScope(
        string Predicate = "1 = 1",
        string? Value = null,
        long MemoryHighWater = long.MaxValue,
        long RelationHighWater = long.MaxValue);
}
