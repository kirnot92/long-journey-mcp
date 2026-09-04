using System.Text.Json;
using System.Text.Json.Nodes;
using LongJourney.Core;

namespace LongJourney.Tests;

public sealed class ModelOwnershipTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MemoryOwnsItsInputsAndKeepsReadOnlyRelatedViewsStable()
    {
        var parentIds = new List<string> { "parent-a", "parent-b", "parent-c" };
        var relations = CreateRelations();
        var memory = CreateMemory("memory", parentIds, relations);
        var positiveIds = memory.PositiveRelated;
        var negativeIds = memory.NegativeRelated;

        parentIds.Clear();
        parentIds.Add("changed-parent");
        relations[0] = new MemoryRelation("changed-relation", RelationKind.Negative, CreatedAt, 99);

        Assert.Equal(new[] { "parent-a", "parent-b", "parent-c" }, memory.DerivedFrom);
        Assert.Equal("related-a", memory.Relations[0].RelatedMemoryId);
        Assert.Equal(new[] { "related-a", "related-c" }, memory.PositiveRelated);
        Assert.Equal(new[] { "related-b" }, memory.NegativeRelated);
        Assert.Same(positiveIds, memory.PositiveRelated);
        Assert.Same(negativeIds, memory.NegativeRelated);

        var parentView = (IList<string>)memory.DerivedFrom;
        var relationView = (IList<MemoryRelation>)memory.Relations;
        var positiveView = (IList<string>)memory.PositiveRelated;
        var negativeView = (IList<string>)memory.NegativeRelated;
        Assert.Throws<NotSupportedException>(() => parentView[0] = "changed-parent");
        Assert.Throws<NotSupportedException>(() => relationView[0] = relations[0]);
        Assert.Throws<NotSupportedException>(() => positiveView[0] = "changed-positive");
        Assert.Throws<NotSupportedException>(() => negativeView[0] = "changed-negative");
    }

    [Fact]
    public void SnapshotOwnsItsInputsAndKeepsACaseSensitiveReadOnlyIndex()
    {
        var lowerCaseMemory = CreateMemory("memory");
        var upperCaseMemory = CreateMemory("MEMORY");
        var memories = new List<MemoryRecord> { lowerCaseMemory, upperCaseMemory };
        var recall = new RecallEvent(lowerCaseMemory.Id, CreatedAt.AddHours(1), 1);
        var recallEvents = new[] { recall };
        var snapshot = new GraphSnapshot(memories, recallEvents);
        var memoryIndex = snapshot.ById;

        memories.Clear();
        recallEvents[0] = new RecallEvent("changed-memory", CreatedAt.AddHours(2), 2);

        Assert.Equal(2, snapshot.Memories.Count);
        Assert.Same(lowerCaseMemory, snapshot.Memories[0]);
        Assert.Same(upperCaseMemory, snapshot.Memories[1]);
        Assert.Same(lowerCaseMemory, snapshot.ById["memory"]);
        Assert.Same(upperCaseMemory, snapshot.ById["MEMORY"]);
        Assert.Equal(recall, Assert.Single(snapshot.RecallEvents));
        Assert.Same(memoryIndex, snapshot.ById);

        var memoryView = (IList<MemoryRecord>)snapshot.Memories;
        var recallView = (IList<RecallEvent>)snapshot.RecallEvents;
        var indexView = (IDictionary<string, MemoryRecord>)snapshot.ById;
        Assert.Throws<NotSupportedException>(() => memoryView[0] = upperCaseMemory);
        Assert.Throws<NotSupportedException>(() => recallView[0] = recallEvents[0]);
        Assert.Throws<NotSupportedException>(() => indexView["memory"] = upperCaseMemory);
    }

    [Fact]
    public void SnapshotRejectsDuplicateIdsDuringConstruction()
    {
        var first = CreateMemory("same-id");
        var second = CreateMemory("same-id");

        Assert.Throws<ArgumentException>(() => new GraphSnapshot([first, second], []));
    }

    [Fact]
    public void UpdatingRecallTimeSharesFrozenCollectionsAndLeavesOriginalUnchanged()
    {
        var memory = CreateMemory("memory");
        var originalRecallTime = memory.LastRecalledAt;
        var newRecallTime = CreatedAt.AddDays(2);

        var recalled = memory with { LastRecalledAt = newRecallTime };

        Assert.NotSame(memory, recalled);
        Assert.Equal(originalRecallTime, memory.LastRecalledAt);
        Assert.Equal(newRecallTime, recalled.LastRecalledAt);
        Assert.Equal(memory.Id, recalled.Id);
        Assert.Same(memory.DerivedFrom, recalled.DerivedFrom);
        Assert.Same(memory.Relations, recalled.Relations);
        Assert.Same(memory.PositiveRelated, recalled.PositiveRelated);
        Assert.Same(memory.NegativeRelated, recalled.NegativeRelated);
    }

    [Fact]
    public void JsonRoundTripPreservesEvidenceOrderAndRebuildsDerivedViews()
    {
        var memory = CreateMemory("memory");
        var recall = new RecallEvent(memory.Id, CreatedAt.AddHours(3), 1);
        var snapshot = new GraphSnapshot([memory], [recall]);
        var json = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);
        var payload = JsonNode.Parse(json)!.AsObject();
        var serializedMemory = payload["memories"]![0]!.AsObject();

        Assert.Equal(memory.CreatedAt, serializedMemory["created_at"]!.GetValue<DateTimeOffset>());
        Assert.Equal("parent-a", serializedMemory["derived_from"]![0]!.GetValue<string>());
        serializedMemory["positive_related"] = new JsonArray("forged-positive");
        serializedMemory["negative_related"] = new JsonArray("forged-negative");
        payload["by_id"] = new JsonObject { ["forged-id"] = serializedMemory.DeepClone() };

        var restored = JsonSerializer.Deserialize<GraphSnapshot>(
            payload.ToJsonString(), JsonDefaults.Options)!;
        var restoredMemory = Assert.Single(restored.Memories);

        Assert.Equal(memory.Id, restoredMemory.Id);
        Assert.Equal(memory.Depth, restoredMemory.Depth);
        Assert.Equal(memory.Content, restoredMemory.Content);
        Assert.Equal(memory.SourceRef, restoredMemory.SourceRef);
        Assert.Equal(memory.CreatedAt, restoredMemory.CreatedAt);
        Assert.Equal(memory.DreamRevision, restoredMemory.DreamRevision);
        Assert.Equal(memory.LastRecalledAt, restoredMemory.LastRecalledAt);
        Assert.Equal(memory.CreatedByModel, restoredMemory.CreatedByModel);
        Assert.Equal(memory.UniqueSourceRootCount, restoredMemory.UniqueSourceRootCount);
        Assert.Equal(memory.Sequence, restoredMemory.Sequence);
        Assert.Equal(new[] { "parent-a", "parent-b", "parent-c" }, restoredMemory.DerivedFrom);
        Assert.Equal(memory.Relations.Count, restoredMemory.Relations.Count);
        for (var index = 0; index < memory.Relations.Count; index++)
        {
            Assert.Equal(memory.Relations[index], restoredMemory.Relations[index]);
        }
        Assert.Equal(new[] { "related-a", "related-c" }, restoredMemory.PositiveRelated);
        Assert.Equal(new[] { "related-b" }, restoredMemory.NegativeRelated);
        Assert.Equal(recall, Assert.Single(restored.RecallEvents));
        Assert.Single(restored.ById);
        Assert.Same(restoredMemory, restored.ById[memory.Id]);
        Assert.False(restored.ById.ContainsKey("forged-id"));
        Assert.Same(restored.ById, restored.ById);
        Assert.Same(restoredMemory.PositiveRelated, restoredMemory.PositiveRelated);
        Assert.Same(restoredMemory.NegativeRelated, restoredMemory.NegativeRelated);
    }

    [Fact]
    public void RepeatedIndexAndRelationGetterReadsDoNotAllocateAfterWarmup()
    {
        var memory = CreateMemory("memory");
        var snapshot = new GraphSnapshot([memory], []);
        const int iterations = 10_000;

        _ = ReadGetterCounts(snapshot, memory, iterations);
        _ = GC.GetAllocatedBytesForCurrentThread();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var observedCount = ReadGetterCounts(snapshot, memory, iterations);
        var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(iterations * 4, observedCount);
        Assert.Equal(0L, allocatedAfter - allocatedBefore);
        GC.KeepAlive(snapshot);
        GC.KeepAlive(memory);
    }

    private static int ReadGetterCounts(GraphSnapshot snapshot, MemoryRecord memory, int iterations)
    {
        var observedCount = 0;
        for (var index = 0; index < iterations; index++)
        {
            observedCount += snapshot.ById.Count;
            observedCount += memory.PositiveRelated.Count;
            observedCount += memory.NegativeRelated.Count;
        }

        return observedCount;
    }

    private static MemoryRecord CreateMemory(
        string id,
        IReadOnlyList<string>? parentIds = null,
        IReadOnlyList<MemoryRelation>? relations = null)
    {
        return new MemoryRecord(
            id,
            1,
            "A provisional pattern.",
            null,
            parentIds ?? ["parent-a", "parent-b", "parent-c"],
            relations ?? CreateRelations(),
            CreatedAt,
            2,
            CreatedAt.AddHours(1),
            "test-model",
            3,
            17);
    }

    private static MemoryRelation[] CreateRelations()
    {
        return
        [
            new MemoryRelation("related-a", RelationKind.Positive, CreatedAt.AddHours(1), 1),
            new MemoryRelation("related-b", RelationKind.Negative, CreatedAt.AddHours(2), 2),
            new MemoryRelation("related-c", RelationKind.Positive, CreatedAt.AddHours(3), 3)
        ];
    }
}
