using LongJourney.Core;

namespace LongJourney.Tests;

internal static class MemoryTestData
{
    public static List<string> Ids(IReadOnlyList<MemoryRecord> memories)
    {
        var ids = new List<string>(memories.Count);
        foreach (var memory in memories)
        {
            ids.Add(memory.Id);
        }

        return ids;
    }

    public static int CountAtDepth(IReadOnlyList<MemoryRecord> memories, int depth)
    {
        var count = 0;
        foreach (var memory in memories)
        {
            if (memory.Depth == depth)
            {
                count++;
            }
        }

        return count;
    }
}
