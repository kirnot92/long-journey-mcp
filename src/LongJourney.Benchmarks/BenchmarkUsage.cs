using System.Globalization;
using System.Text.Json;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Benchmarks;

public static class BenchmarkUsage
{
    public static UsageTotals Read(SqliteMemoryStore store, string? operation = null)
    {
        return ReadPath(store.DatabasePath, operation);
    }

    public static UsageTotals ReadPath(string databasePath, string? operation = null)
    {
        using var db = OpenPath(databasePath);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT reserved_usd, actual_usd, usage_json
            FROM api_calls
            WHERE ($operation IS NULL OR operation = $operation)
            """;
        command.Parameters.AddWithValue("$operation", (object?)operation ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        decimal actual = 0, reserved = 0;
        long input = 0, output = 0;
        var calls = 0;
        while (reader.Read())
        {
            calls++;
            if (reader.IsDBNull(1))
            {
                reserved += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                continue;
            }
            actual += decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var usage = JsonSerializer.Deserialize<ApiUsage>(reader.GetString(2), JsonDefaults.Options)
                ?? throw new InvalidDataException("API usage row is missing.");
            input += usage.InputTokens;
            output += usage.OutputTokens;
        }
        return new UsageTotals(actual, reserved, input, output, calls);
    }

    public static UsageTotals Subtract(UsageTotals all, UsageTotals shared) => new(
        all.SettledUsd - shared.SettledUsd, all.ReservedUsd - shared.ReservedUsd,
        all.InputTokens - shared.InputTokens, all.OutputTokens - shared.OutputTokens, all.Calls - shared.Calls);

    public static CorpusMorphology Morphology(SqliteMemoryStore store, IReadOnlyDictionary<string, string> sourceMap)
    {
        var roots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sourceId in sourceMap.Keys)
        {
            roots.Add(sourceId, 0);
        }
        var depth0 = 0;
        var depth1 = 0;
        var depth2 = 0;
        var positive = 0;
        var negative = 0;
        var dream = 0;
        var meditation = 0;
        var runKinds = new Dictionary<long, RunKind>();
        foreach (var run in store.GetRuns())
        {
            runKinds.Add(run.Id, run.Kind);
            if (run.Kind == RunKind.Meditation &&
                (run.BudgetUsd is null or > 5m || store.GetRunAccountedUsd(run.Id) > 5m))
            {
                throw new InvariantException("Weekly Meditation exceeded its USD 5 accounting bound.");
            }
        }
        foreach (var memory in store.ReadSnapshot().Memories)
        {
            if (memory.Depth == 0)
            {
                depth0++;
                roots[memory.SourceRef!]++;
            }
            else
            {
                if (memory.Depth == 1)
                {
                    depth1++;
                }
                else
                {
                    depth2++;
                }
                if (runKinds[memory.DreamRevision] == RunKind.Dream)
                {
                    dream++;
                }
                else
                {
                    meditation++;
                }
            }
            positive += memory.PositiveRelated.Count;
            negative += memory.NegativeRelated.Count;
        }
        return new CorpusMorphology(roots.Count, depth0, depth1, depth2, positive, negative, dream, meditation, roots);
    }

    public static void ExportCalls(SqliteMemoryStore store, string path)
    {
        using var db = Open(store);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id, run_id, model, operation, reserved_usd, actual_usd, usage_json, created_at, completed_at
            FROM api_calls ORDER BY rowid
            """;
        using var reader = command.ExecuteReader();
        using var writer = new StreamWriter(path, false);
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            writer.WriteLine(JsonSerializer.Serialize(values, JsonDefaults.Options));
        }
    }

    private static SqliteConnection Open(SqliteMemoryStore store)
    {
        return OpenPath(store.DatabasePath);
    }

    private static SqliteConnection OpenPath(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        return db;
    }
}
