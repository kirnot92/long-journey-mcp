using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public static class BenchmarkReplay
{
    private const string CheckpointKey = "benchmark.replay.v1";

    public static DateTimeOffset EvaluationTime(IReadOnlyList<BenchmarkSession> sessions, DateTimeOffset questionDate)
    {
        var evaluationTime = questionDate;
        foreach (var session in sessions)
        {
            if (session.Timestamp > evaluationTime)
            {
                evaluationTime = session.Timestamp;
            }
        }

        return evaluationTime;
    }

    public static async Task<IReadOnlyDictionary<string, string>> ReplayAsync(
        IReadOnlyList<BenchmarkSession> sessions,
        DateTimeOffset questionDate,
        SqliteMemoryStore baseline,
        SqliteMemoryStore full,
        MemoryEngine baselineEngine,
        MemoryScheduler scheduler,
        BenchmarkClock baselineClock,
        BenchmarkClock fullClock,
        string embeddingSpace,
        CancellationToken ct)
    {
        var ordered = OrderSessions(sessions);
        // Some official histories contain sessions later on the question's own calendar day.
        // Preserve them all and their original timestamps; the question date in prompts is unchanged.
        var evaluationTime = EvaluationTime(ordered, questionDate);
        var sourceToSession = CreateSourceMap(ordered);
        var fingerprint = Fingerprint(ordered, questionDate, embeddingSpace);
        var checkpoint = ReadCheckpoint(full, baseline, ordered, evaluationTime, fingerprint);
        baselineClock.UtcNow = checkpoint.ClockAt;
        fullClock.UtcNow = checkpoint.ClockAt;

        while (checkpoint.NextSessionIndex < ordered.Count)
        {
            ct.ThrowIfCancellationRequested();
            var session = ordered[checkpoint.NextSessionIndex];
            checkpoint = await CloseDaysAsync(
                session.Timestamp, checkpoint, full, scheduler, baselineClock, fullClock, ct);
            checkpoint = AdvanceClock(checkpoint, session.Timestamp, full, baselineClock, fullClock);

            // The only extraction occurs in the baseline. The full corpus receives each completed
            // session immediately, before the next calendar boundary can see it.
            var remembered = await baselineEngine.RememberAsync(session.Raw, ct);
            if (remembered.Status != "complete")
            {
                throw new InvariantException("Shared extraction did not complete.");
            }

            ImportSharedSession(session, remembered, baseline, full, embeddingSpace);
            checkpoint = checkpoint with { NextSessionIndex = checkpoint.NextSessionIndex + 1 };
            SaveCheckpoint(full, checkpoint);
            Console.WriteLine($"Replay session {checkpoint.NextSessionIndex}/{ordered.Count} at {session.Timestamp:O}");
        }

        checkpoint = await CloseDaysAsync(
            evaluationTime, checkpoint, full, scheduler, baselineClock, fullClock, ct);
        _ = AdvanceClock(checkpoint, evaluationTime, full, baselineClock, fullClock);
        VerifySharedObservations(baseline, full, sourceToSession, embeddingSpace);
        return sourceToSession;
    }

    private static IReadOnlyList<BenchmarkSession> OrderSessions(IReadOnlyList<BenchmarkSession> sessions)
    {
        var indexed = new List<(BenchmarkSession Session, int Ordinal)>(sessions.Count);
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            indexed.Add((session, index));
        }

        indexed.Sort((left, right) =>
        {
            var timeOrder = left.Session.Timestamp.CompareTo(right.Session.Timestamp);
            return timeOrder != 0 ? timeOrder : left.Ordinal.CompareTo(right.Ordinal);
        });
        var ordered = new List<BenchmarkSession>(indexed.Count);
        foreach (var item in indexed)
        {
            ordered.Add(item.Session);
        }

        return ordered;
    }

    internal static Dictionary<string, string> CreateSourceMap(IReadOnlyList<BenchmarkSession> sessions)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var sourceId = "src_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Raw)));
            if (!map.TryAdd(sourceId, session.SessionId))
            {
                throw new InvalidDataException("Session raw must include its opaque ordinal to preserve distinct Sources.");
            }
        }

        return map;
    }

    private static string Fingerprint(
        IReadOnlyList<BenchmarkSession> sessions, DateTimeOffset questionDate, string embeddingSpace)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { questionDate, embeddingSpace }));
        foreach (var session in sessions)
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(session));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static ReplayCheckpoint ReadCheckpoint(
        SqliteMemoryStore full, SqliteMemoryStore baseline,
        IReadOnlyList<BenchmarkSession> sessions, DateTimeOffset questionDate, string fingerprint)
    {
        var saved = full.GetState(CheckpointKey);
        if (saved is not null)
        {
            var checkpoint = JsonSerializer.Deserialize<ReplayCheckpoint>(saved, JsonDefaults.Options)
                ?? throw new InvalidDataException("The replay checkpoint is empty.");
            if (checkpoint.Fingerprint != fingerprint ||
                checkpoint.NextSessionIndex < 0 || checkpoint.NextSessionIndex > sessions.Count ||
                checkpoint.ClockAt > questionDate)
            {
                throw new InvalidDataException("Replay inputs differ from the persisted history checkpoint.");
            }

            return checkpoint;
        }

        if (full.GetState("corpus.first_source_at") is not null ||
            baseline.GetState("corpus.first_source_at") is not null)
        {
            throw new InvalidDataException("An existing corpus has no benchmark replay checkpoint.");
        }

        var firstTimestamp = sessions.Count == 0 ? questionDate : sessions[0].Timestamp;
        var nextMidnight = new DateTimeOffset(firstTimestamp.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        var initial = new ReplayCheckpoint(fingerprint, 0, nextMidnight, firstTimestamp);
        SaveCheckpoint(full, initial);
        return initial;
    }

    private static async Task<ReplayCheckpoint> CloseDaysAsync(
        DateTimeOffset until, ReplayCheckpoint checkpoint, SqliteMemoryStore full,
        MemoryScheduler scheduler, BenchmarkClock baselineClock, BenchmarkClock fullClock, CancellationToken ct)
    {
        while (checkpoint.NextMidnight <= until)
        {
            ct.ThrowIfCancellationRequested();
            checkpoint = AdvanceClock(checkpoint, checkpoint.NextMidnight, full, baselineClock, fullClock);
            var results = await scheduler.TickAsync(ct);
            Console.WriteLine($"Replay closed day {checkpoint.NextMidnight.AddDays(-1):yyyy-MM-dd}; {results.Count} scheduler runs");
            // Leave the current boundary persisted until all its runs finish. A crash between
            // scheduler commits and this write repeats an idempotent tick at the same instant.
            checkpoint = checkpoint with { NextMidnight = checkpoint.NextMidnight.AddDays(1) };
            SaveCheckpoint(full, checkpoint);
        }

        return checkpoint;
    }

    private static ReplayCheckpoint AdvanceClock(
        ReplayCheckpoint checkpoint, DateTimeOffset timestamp,
        SqliteMemoryStore full, BenchmarkClock baselineClock, BenchmarkClock fullClock)
    {
        if (timestamp < checkpoint.ClockAt)
        {
            throw new InvariantException("Replay cannot reverse its persisted simulated clock.");
        }

        checkpoint = checkpoint with { ClockAt = timestamp };
        // Persist the event time before extraction/import or scheduler execution. Interrupted
        // events resume here, without exposing sessions from a later event to earlier runs.
        SaveCheckpoint(full, checkpoint);
        baselineClock.UtcNow = timestamp;
        fullClock.UtcNow = timestamp;
        return checkpoint;
    }

    private static void SaveCheckpoint(SqliteMemoryStore full, ReplayCheckpoint checkpoint)
    {
        full.SetState(CheckpointKey, JsonSerializer.Serialize(checkpoint, JsonDefaults.Options));
    }

    internal static void ImportSharedSession(
        BenchmarkSession session, RememberResult remembered,
        SqliteMemoryStore baseline, SqliteMemoryStore full, string embeddingSpace)
    {
        var originalSource = baseline.ReadSource(remembered.SourceId);
        var destination = full.SaveSource(session.Raw, session.Timestamp);
        if (destination.Source.Id != originalSource.Source.Id ||
            destination.Source.CreatedAt != originalSource.Source.CreatedAt)
        {
            throw new InvariantException("The two corpora must preserve identical Source identities and timestamps.");
        }

        if (!full.ClaimSource(destination.Source.Id))
        {
            if (full.ReadRememberResult(destination.Source.Id, true).Status != "complete")
            {
                throw new InvariantException("The shared Source is already being imported.");
            }

            return;
        }

        try
        {
            var observations = new List<NewObservation>(remembered.Memories.Count);
            var memoryIds = new List<string>(remembered.Memories.Count);
            foreach (var memory in remembered.Memories)
            {
                if (memory.Depth != 0 || memory.SourceRef != destination.Source.Id ||
                    memory.CreatedAt != session.Timestamp)
                {
                    throw new InvariantException("Shared observations must belong to this session and its simulated timestamp.");
                }

                var embedding = baseline.GetEmbedding(memory.Id, embeddingSpace)
                    ?? throw new InvariantException("A shared observation has no embedding in the configured space.");
                observations.Add(new NewObservation(memory.Content, memory.CreatedByModel, embedding));
                memoryIds.Add(memory.Id);
            }

            full.CompleteSource(destination.Source.Id, observations, session.Timestamp, memoryIds);
        }
        catch
        {
            full.FailSource(destination.Source.Id);
            throw;
        }
    }

    internal static void VerifySharedObservations(
        SqliteMemoryStore baseline, SqliteMemoryStore full,
        IReadOnlyDictionary<string, string> sourceToSession, string embeddingSpace)
    {
        var baselineMemories = baseline.ReadSnapshot();
        var fullMemories = full.ReadSnapshot();
        var fullDepth0 = 0;
        foreach (var memory in fullMemories.Memories)
        {
            if (memory.Depth == 0)
            {
                fullDepth0++;
            }
        }

        if (baselineMemories.Memories.Count != fullDepth0)
        {
            throw new InvariantException("The two corpora have different depth-0 memory counts.");
        }

        var fullEmbeddings = new Dictionary<string, EmbeddingVector>(StringComparer.Ordinal);
        foreach (var embedding in full.GetEmbeddings(embeddingSpace))
        {
            fullEmbeddings.Add(embedding.MemoryId, embedding.Embedding);
        }

        foreach (var memory in baselineMemories.Memories)
        {
            if (memory.Depth != 0 || memory.SourceRef is null || !sourceToSession.ContainsKey(memory.SourceRef) ||
                !fullMemories.ById.TryGetValue(memory.Id, out var imported) || imported.Depth != 0 ||
                imported.SourceRef != memory.SourceRef || imported.Content != memory.Content ||
                imported.CreatedAt != memory.CreatedAt || imported.CreatedByModel != memory.CreatedByModel)
            {
                throw new InvariantException("The two corpora do not share identical direct observations.");
            }

            var baselineEmbedding = baseline.GetEmbedding(memory.Id, embeddingSpace)
                ?? throw new InvariantException("A direct observation has no shared embedding.");
            if (!fullEmbeddings.TryGetValue(memory.Id, out var fullEmbedding) ||
                !baselineEmbedding.Values.AsSpan().SequenceEqual(fullEmbedding.Values))
            {
                throw new InvariantException("The two corpora do not share identical direct-observation embeddings.");
            }
        }
    }

    private sealed record ReplayCheckpoint(
        string Fingerprint, int NextSessionIndex, DateTimeOffset NextMidnight, DateTimeOffset ClockAt);
}
