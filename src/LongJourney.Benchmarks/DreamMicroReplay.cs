using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

/// <summary>Replays shared observations chronologically, closing only days with real Dream work.</summary>
public static class DreamMicroReplay
{
    private const string CheckpointKey = "benchmark.dream-micro.next-session.v1";

    public static async Task<IReadOnlyDictionary<string, string>> ReplayAsync(
        IReadOnlyList<BenchmarkSession> sessions, DateTimeOffset questionDate,
        SqliteMemoryStore baseline, SqliteMemoryStore dream, ConsolidationEngine consolidation,
        BenchmarkClock clock, string embeddingSpace, CancellationToken cancellationToken)
    {
        var sourceMap = BenchmarkReplay.CreateSourceMap(sessions);
        var evaluationTime = BenchmarkReplay.EvaluationTime(sessions, questionDate);
        var next = int.Parse(dream.GetState(CheckpointKey) ?? "0", CultureInfo.InvariantCulture);
        if (next < 0 || next > sessions.Count)
        {
            throw new InvalidDataException("Invalid Dream micro replay checkpoint.");
        }

        for (var index = next; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = sessions[index];
            if (index > 0)
            {
                var previous = sessions[index - 1];
                if (previous.Timestamp > session.Timestamp)
                {
                    throw new InvalidDataException("Micro replay sessions must remain chronologically ordered.");
                }
                if (previous.Timestamp.UtcDateTime.Date != session.Timestamp.UtcDateTime.Date)
                {
                    await CloseDayAsync(previous.Timestamp, evaluationTime, dream, consolidation, clock, cancellationToken);
                }
            }

            clock.UtcNow = session.Timestamp;
            var sourceId = "src_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Raw)));
            var remembered = baseline.ReadRememberResult(sourceId, true);
            if (remembered.Status != "complete")
            {
                throw new InvariantException("Micro Dream requires completed shared extraction.");
            }
            BenchmarkReplay.ImportSharedSession(session, remembered, baseline, dream, embeddingSpace);
            dream.SetState(CheckpointKey, (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        if (sessions.Count > 0)
        {
            await CloseDayAsync(sessions[^1].Timestamp, evaluationTime, dream, consolidation, clock, cancellationToken);
        }
        clock.UtcNow = evaluationTime;
        BenchmarkReplay.VerifySharedObservations(baseline, dream, sourceMap, embeddingSpace);
        return sourceMap;
    }

    private static async Task CloseDayAsync(DateTimeOffset sessionTime, DateTimeOffset evaluationTime,
        SqliteMemoryStore store, ConsolidationEngine consolidation, BenchmarkClock clock,
        CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(sessionTime.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(1);
        if (end > evaluationTime)
        {
            return;
        }
        var snapshot = store.ReadSnapshot();
        var hasWork = false;
        foreach (var memory in snapshot.Memories)
        {
            if (memory.Depth == 0 && memory.CreatedAt >= start && memory.CreatedAt < end)
            {
                hasWork = true;
                break;
            }
        }
        if (!hasWork)
        {
            foreach (var recall in snapshot.RecallEvents)
            {
                if (recall.RecalledAt >= start && recall.RecalledAt < end)
                {
                    hasWork = true;
                    break;
                }
            }
        }
        if (hasWork)
        {
            clock.UtcNow = end;
            await consolidation.DreamAsync(start, end, cancellationToken);
            Console.WriteLine($"Micro Dream closed {start:yyyy-MM-dd}");
        }
    }
}
