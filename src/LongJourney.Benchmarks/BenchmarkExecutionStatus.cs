using System.Globalization;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Benchmarks;

public static class BenchmarkExecutionStatus
{
    // Includes in-flight questions, which have not yet produced a paired evaluation result.
    public static void Write(string outputDirectory, string state)
    {
        var rows = new List<object>();
        decimal settled = 0, reserved = 0;
        var calls = 0;
        var questionsDirectory = Path.Combine(outputDirectory, "questions");
        if (Directory.Exists(questionsDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(questionsDirectory, "memory.db", SearchOption.AllDirectories))
            {
                try
                {
                    var usage = BenchmarkUsage.ReadPath(path);
                    using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false
                    }.ToString());
                    db.Open();
                    using var command = db.CreateCommand();
                    command.CommandText = """
                        SELECT
                          (SELECT COUNT(*) FROM sources WHERE status='complete'),
                          (SELECT COUNT(*) FROM memories WHERE depth=0),
                          (SELECT COUNT(*) FROM memories WHERE depth>0),
                          (SELECT COUNT(*) FROM runs WHERE kind='dream' AND status='complete'),
                          (SELECT COUNT(*) FROM runs WHERE kind='meditation' AND status IN ('complete','budget_exhausted'))
                        """;
                    using var reader = command.ExecuteReader();
                    reader.Read();
                    rows.Add(new
                    {
                        corpus = Path.GetRelativePath(outputDirectory, Path.GetDirectoryName(path)!),
                        completed_sources = reader.GetInt32(0),
                        depth0_memories = reader.GetInt32(1),
                        higher_depth_memories = reader.GetInt32(2),
                        completed_dreams = reader.GetInt32(3),
                        completed_meditations = reader.GetInt32(4),
                        usage
                    });
                    settled += usage.SettledUsd;
                    reserved += usage.ReservedUsd;
                    calls += usage.Calls;
                }
                catch (SqliteException)
                {
                    // A newly created corpus may not have its schema yet; the next snapshot includes it.
                    rows.Add(new { corpus = Path.GetRelativePath(outputDirectory, path), status = "initializing_or_busy" });
                }
            }
        }
        BenchmarkFiles.WriteJson(Path.Combine(outputDirectory, "execution-status.json"), new
        {
            state,
            at = DateTimeOffset.UtcNow,
            api_calls = calls,
            physical_settled_usd = settled,
            physical_reserved_usd = reserved,
            corpora = rows
        });
    }
}
