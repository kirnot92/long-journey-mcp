using LongJourney.Core;

namespace LongJourney.Tests;

internal static class MemoryTestData
{
    public static string[] Ids(IReadOnlyList<MemoryRecord> memories)
    {
        var ids = new string[memories.Count];
        for (var index = 0; index < memories.Count; index++)
        {
            ids[index] = memories[index].Id;
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
