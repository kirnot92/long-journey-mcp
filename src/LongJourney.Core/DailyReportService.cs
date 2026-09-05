using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

public sealed record DailyReportExport(DateOnly Date, string MarkdownPath, string JsonPath, string SnapshotId);

/// <summary>Reads the activity ledger without initializing, recovering, or changing the corpus.</summary>
public sealed partial class DailyReportService
{
    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private string? _lastDatabaseStamp;
    private DateOnly? _lastClosedDate;
    private readonly HashSet<string> _closedReportPaths = [];
    private readonly Dictionary<string, string> _closedReportStamps = [];

    public DailyReportService(string dataDirectory, string timeZoneId, TimeProvider? timeProvider = null)
    {
        _directory = Path.GetFullPath(dataDirectory);
        _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        _clock = timeProvider ?? TimeProvider.System;
    }

    public DailyReportExport Export(DateOnly date)
    {
        lock (_gate)
        {
            using var snapshot = ReadSnapshot();
            return Write(Build(snapshot, date));
        }
    }

    /// <summary>Fills closed dates and refreshes only reports whose underlying data changed.</summary>
    public IReadOnlyList<DailyReportExport> ExportClosedDays()
    {
        lock (_gate)
        {
            var lastDate = LocalDate(_clock.GetUtcNow()).AddDays(-1);
            var stamp = DatabaseStamp();
            if (stamp == _lastDatabaseStamp && lastDate == _lastClosedDate &&
                _closedReportPaths.All(path => File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".md")) &&
                    _closedReportStamps.TryGetValue(path, out var savedStamp) && savedStamp == ReportStamp(path)))
            {
                return [];
            }

            using var snapshot = ReadSnapshot();
            var firstAt = snapshot.CoverageStart;
            List<DailyReportExport> exports = [];
            if (firstAt is not null)
            {
                for (var day = LocalDate(firstAt.Value); day <= lastDate; day = day.AddDays(1))
                {
                    var report = Build(snapshot, day);
                    var path = Path.Combine(_directory, "reports", "daily", $"{day:yyyy-MM-dd}.json");
                    _closedReportPaths.Add(path);
                    if (!MatchesExisting(path, Text(report, "data_fingerprint")))
                    {
                        exports.Add(Write(report));
                    }
                    _closedReportStamps[path] = ReportStamp(path);
                }
            }

            _lastDatabaseStamp = stamp;
            _lastClosedDate = lastDate;
            return exports;
        }
    }

    private string DatabaseStamp()
    {
        return string.Join("|", new[] { "memory.db", "memory.db-wal" }.Select(name =>
        {
            var file = new FileInfo(Path.Combine(_directory, name));
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }));
    }

    private static string ReportStamp(string jsonPath) =>
        string.Join("|", new[] { jsonPath, Path.ChangeExtension(jsonPath, ".md") }.Select(path =>
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }));

    private static bool MatchesExisting(string path, string? fingerprint)
    {
        if (!File.Exists(path) || !File.Exists(Path.ChangeExtension(path, ".md")))
        {
            return false;
        }

        try
        {
            var prior = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var snapshot = Text(prior, "snapshot_id");
            return Text(prior, "data_fingerprint") == fingerprint && snapshot is not null &&
                File.ReadLines(Path.ChangeExtension(path, ".md")).Take(6).Any(line => line.Contains(snapshot, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Snapshot ReadSnapshot()
    {
        var path = Path.Combine(_directory, "memory.db");
        if (!File.Exists(path))
        {
            throw new InputException($"Daily report corpus does not contain memory.db: {_directory}");
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        try
        {
            db.Open();
            var tx = db.BeginTransaction(deferred: true);
            using var tablesCommand = db.CreateCommand();
            tablesCommand.Transaction = tx;
            tablesCommand.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            HashSet<string> tables = [];
            using (var reader = tablesCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Dictionary<string, List<JsonObject>> rows = [];
            foreach (var table in new[] { "state", "activity_operations", "activity_relation_results", "activity_api_calls", "sources", "memories", "derived_from", "runs", "run_work", "api_calls" })
            {
                if (!tables.Contains(table))
                {
                    continue;
                }

                using var command = db.CreateCommand();
                command.Transaction = tx;
                // Table names are fixed above, never supplied by the caller.
                command.CommandText = table == "memories"
                    ? "SELECT id, depth, source_ref, created_at, dream_revision, created_by_model, origin_key, seq, sealed FROM memories"
                    : $"SELECT * FROM {table}";
                using var reader = command.ExecuteReader();
                List<JsonObject> values = [];
                while (reader.Read())
                {
                    JsonObject row = [];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        if (name.EndsWith("_json", StringComparison.Ordinal))
                        {
                            row[name[..^5]] = value is string json ? JsonNode.Parse(json) : null;
                        }
                        else
                        {
                            row[name] = value is null ? null : JsonSerializer.SerializeToNode(value);
                        }
                    }

                    values.Add(row);
                }

                rows.Add(table, values.OrderBy(row => Text(row, "id") ?? Text(row, "key") ??
                    WorkKey(row) + "/" + Text(row, "proposal_index") + "/" + row.ToJsonString(), StringComparer.Ordinal).ToList());
            }

            var coverage = rows.GetValueOrDefault("state", []).FirstOrDefault(row => Text(row, "key") == "activity.started_at");
            return new Snapshot(_clock.GetUtcNow(), rows, Timestamp(coverage, "value"), db, tx);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private JsonObject Build(Snapshot snapshot, DateOnly date)
    {
        bool OnDay(JsonObject row, string key) => Timestamp(row, key) is { } at && LocalDate(at) == date;
        var allOperations = snapshot.Rows("activity_operations");
        var operations = allOperations.Where(row => OnDay(row, "started_at")).ToList();
        var apiCalls = snapshot.Rows("api_calls").Where(row => OnDay(row, "created_at")).ToList();
        var apiLinks = snapshot.Rows("activity_api_calls").ToDictionary(row => Text(row, "api_call_id")!);
        var operationMap = allOperations.ToDictionary(row => Text(row, "id")!);
        foreach (var call in apiCalls)
        {
            if (apiLinks.TryGetValue(Text(call, "id")!, out var link))
            {
                call["activity_id"] = Text(link, "activity_id");
                call["settings"] = link["settings"]?.DeepClone();
                if (Text(link, "activity_id") is { } linkedId && operationMap.TryGetValue(linkedId, out var owner))
                {
                    call["parent_activity_kind"] = Text(owner, "kind");
                    call["parent_work_key"] = Text(owner, "work_key");
                    call["parent_run_id"] = owner["run_id"]?.DeepClone();
                }
            }
        }

        var operationIds = operations.Select(row => Text(row, "id")!).ToHashSet();
        foreach (var call in apiCalls)
        {
            if (Text(call, "activity_id") is { } id)
            {
                operationIds.Add(id);
            }
        }

        // Include cross-midnight parents/children so API and extraction links remain traversable.
        bool added;
        do
        {
            added = false;
            foreach (var row in allOperations)
            {
                var id = Text(row, "id")!;
                var parent = Text(row, "parent_id");
                if (parent is not null && (operationIds.Contains(id) || operationIds.Contains(parent)))
                {
                    added |= operationIds.Add(id);
                    added |= operationIds.Add(parent);
                }
            }
        } while (added);

        var linkedOperations = allOperations.Where(row => operationIds.Contains(Text(row, "id")!)).ToList();
        var workKeys = linkedOperations.Where(row => Text(row, "run_id") is not null && Text(row, "work_key") is not null)
            .Select(WorkKey).ToHashSet();
        var outcomes = snapshot.Rows("activity_relation_results")
            .Where(row => OnDay(row, "at") || workKeys.Contains(WorkKey(row))).ToList();
        var memories = snapshot.Rows("memories").ToDictionary(row => Text(row, "id")!);
        foreach (var outcome in outcomes)
        {
            outcome["category"] = RelationCategory(outcome, memories);
        }

        var runIds = linkedOperations.Select(row => Text(row, "run_id"))
            .Concat(linkedOperations.Select(row => Text(row, "charged_run_id")))
            .Concat(apiCalls.Select(row => Text(row, "run_id"))).OfType<string>().ToHashSet();
        foreach (var run in snapshot.Rows("runs").Where(row => OnDay(row, "started_at")))
        {
            runIds.Add(Text(run, "id")!);
        }

        var workItems = snapshot.Rows("run_work").Where(row => runIds.Contains(Text(row, "run_id")!)).ToList();

        var createdMemories = memories.Values.Where(row => OnDay(row, "created_at")).ToList();
        var memoryIds = createdMemories.Select(row => Text(row, "id")!).ToHashSet();
        foreach (var work in workItems)
        {
            if (Text(work, "memory_id") is { } seed)
            {
                memoryIds.Add(seed);
            }
        }
        foreach (var row in linkedOperations)
        {
            var details = row["details"] as JsonObject;
            foreach (var key in new[] { "created_ids", "returned_ids", "candidate_ids" })
            {
                foreach (var id in Ids(details, key))
                {
                    memoryIds.Add(id);
                }
            }

            if (Text(details, "seed_id") is { } seed)
            {
                memoryIds.Add(seed);
            }
        }

        foreach (var row in outcomes)
        {
            foreach (var key in new[] { "memory_id", "related_memory_id" })
            {
                if (Text(row, key) is { } id)
                {
                    memoryIds.Add(id);
                }
            }
        }

        var parents = snapshot.Rows("derived_from");
        do
        {
            added = false;
            foreach (var parent in parents.Where(row => memoryIds.Contains(Text(row, "child_id")!)))
            {
                added |= memoryIds.Add(Text(parent, "parent_id")!);
            }
        } while (added);

        var referencedMemories = memories.Values.Where(row => memoryIds.Contains(Text(row, "id")!)).ToList();
        snapshot.LoadContents(memoryIds);
        // These mutable fields cannot recreate historical Recall context.
        referencedMemories = referencedMemories.Select(row =>
        {
            var copy = (JsonObject)row.DeepClone();
            copy.Remove("last_recalled_at");
            copy["derived_from"] = Strings(parents.Where(p => Text(p, "child_id") == Text(row, "id")).Select(p => Text(p, "parent_id")!));
            return copy;
        }).ToList();
        var sourceIds = referencedMemories.Select(row => Text(row, "source_ref"))
            .Concat(linkedOperations.Select(row => Text(row, "source_id"))).OfType<string>().ToHashSet();
        var sources = snapshot.Rows("sources").Where(row => sourceIds.Contains(Text(row, "id")!) || OnDay(row, "created_at")).Select(SourceDetail).ToList();
        var coverage = snapshot.CoverageStart is null || !snapshot.HasTable("activity_operations") ? "unknown_legacy"
            : LocalDate(snapshot.CoverageStart.Value) > date ? "unknown_legacy"
            : LocalDate(snapshot.CoverageStart.Value) == date ? "partial_from_instrumentation_start" : "instrumented";
        JsonObject report = new()
        {
            ["schema_version"] = 1,
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["time_zone_id"] = _zone.Id,
            ["provisional"] = date >= LocalDate(snapshot.AsOf),
            ["coverage"] = new JsonObject
            {
                ["status"] = coverage,
                ["instrumentation_started_at"] = snapshot.CoverageStart?.ToString("O"),
                ["call_count_boundary"] = "MemoryEngine method entry; transport/parser failures excluded",
                ["historical_recall_context"] = "ordered candidate/returned IDs only; mutable relations excluded",
                ["unknown_history"] = "Before instrumentation, call counts, empty responses, retries and append outcomes cannot be recovered."
            },
            ["attribution"] = new JsonObject
            {
                ["operations"] = "started_at local date",
                ["api_costs"] = "API created_at local date, latest settlement",
                ["relation_summary"] = "result at local date; full work outcomes also included for attempts started this day",
                ["dream_period"] = "runs period_start/period_end; independent of execution date"
            },
            ["summary"] = Summarize(operations, apiCalls, sources.Where(row => OnDay(row, "created_at")).ToList(), createdMemories, outcomes.Where(row => OnDay(row, "at")).ToList(), memories, workItems, allOperations, coverage),
            ["operations"] = Rows(operations),
            ["linked_operations"] = Rows(linkedOperations.Where(row => !operations.Contains(row))),
            ["api_calls"] = Rows(apiCalls),
            ["relation_results"] = Rows(outcomes),
            ["sources"] = Rows(sources),
            ["memories"] = Rows(referencedMemories),
            ["runs"] = Rows(snapshot.Rows("runs").Where(row => runIds.Contains(Text(row, "id")!))),
            ["work_items"] = Rows(workItems)
        };
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(report.ToJsonString())));
        report["data_fingerprint"] = fingerprint;
        report["as_of"] = snapshot.AsOf.ToString("O");
        report["snapshot_id"] = Guid.NewGuid().ToString("N");
        report["snapshot_scope"] = "single read-only SQLite transaction; immutable source files inspected without recovery";
        return report;
    }

    private JsonObject SourceDetail(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        var relative = Text(source, "relative_path");
        result["raw_characters"] = null;
        result["raw_bytes"] = null;
        result["artifact_status"] = "missing";
        result["source_status_scope"] = "current status at report snapshot, not status at historical call";
        if (relative is null)
        {
            return result;
        }

        var path = Path.GetFullPath(Path.Combine(_directory, relative));
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            result["artifact_status"] = "outside_corpus";
            return result;
        }

        result["absolute_path"] = path;
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var text = File.ReadAllText(path, new UTF8Encoding(false, true));
            var end = text.IndexOf("\n---\n\n", StringComparison.Ordinal);
            if (!text.StartsWith("---\n", StringComparison.Ordinal) || end < 0)
            {
                result["artifact_status"] = "invalid_header";
                return result;
            }

            var raw = text[(end + 6)..];
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            if (hash != Text(source, "content_hash"))
            {
                result["artifact_status"] = "hash_mismatch";
                return result;
            }

            result["raw_characters"] = raw.Length;
            result["raw_bytes"] = Encoding.UTF8.GetByteCount(raw);
            result["artifact_status"] = "available";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            result["artifact_status"] = error.GetType().Name;
        }

        return result;
    }

    private DailyReportExport Write(JsonObject report)
    {
        var date = DateOnly.ParseExact(Text(report, "date")!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var directory = Path.Combine(_directory, "reports", "daily");
        Directory.CreateDirectory(directory);
        using var lease = new FileStream(Path.Combine(directory, ".export.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var jsonPath = Path.Combine(directory, $"{date:yyyy-MM-dd}.json");
        var markdownPath = Path.Combine(directory, $"{date:yyyy-MM-dd}.md");
        var suffix = "." + Text(report, "snapshot_id") + ".tmp";
        try
        {
            File.WriteAllText(jsonPath + suffix, report.ToJsonString(ReportJson), new UTF8Encoding(false));
            File.WriteAllText(markdownPath + suffix, RenderMarkdown(report), new UTF8Encoding(false));
            // Each replacement is atomic. Both files carry the same snapshot ID; a interrupted
            // pair is detectable and rebuilt on the next catch-up or manual export.
            File.Move(markdownPath + suffix, markdownPath, overwrite: true);
            File.Move(jsonPath + suffix, jsonPath, overwrite: true);
        }
        finally
        {
            File.Delete(jsonPath + suffix);
            File.Delete(markdownPath + suffix);
        }

        return new DailyReportExport(date, markdownPath, jsonPath, Text(report, "snapshot_id")!);
    }

    private DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, _zone).DateTime);
    private static string WorkKey(JsonObject row) => Text(row, "run_id") + "/" + Text(row, "work_key");
    private static string? Text(JsonObject? row, string key) => row?[key]?.ToString();
    private static DateTimeOffset? Timestamp(JsonObject? row, string key) => DateTimeOffset.TryParse(Text(row, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
    private static IEnumerable<string> Ids(JsonObject? row, string key) => row?[key] is JsonArray ids ? ids.Select(id => id!.ToString()) : [];
    private static JsonArray Rows(IEnumerable<JsonObject> rows) => new(rows.Select(row => row.DeepClone()).ToArray());
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private sealed record Snapshot(DateTimeOffset AsOf, Dictionary<string, List<JsonObject>> Tables,
        DateTimeOffset? CoverageStart, SqliteConnection Connection, SqliteTransaction Transaction) : IDisposable
    {
        public List<JsonObject> Rows(string table) => Tables.GetValueOrDefault(table, []);
        public bool HasTable(string table) => Tables.ContainsKey(table);
        public void LoadContents(HashSet<string> ids)
        {
            var missing = Rows("memories").Where(row => ids.Contains(Text(row, "id")!) && !row.ContainsKey("content"))
                .ToDictionary(row => Text(row, "id")!);
            foreach (var chunk in missing.Keys.Chunk(200))
            {
                using var command = Connection.CreateCommand();
                command.Transaction = Transaction;
                var names = chunk.Select((id, index) => "$id" + index).ToArray();
                command.CommandText = $"SELECT id, content FROM memories WHERE id IN ({string.Join(',', names)})";
                for (var i = 0; i < chunk.Length; i++)
                {
                    command.Parameters.AddWithValue(names[i], chunk[i]);
                }

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    missing[reader.GetString(0)]["content"] = reader.GetString(1);
                }
            }
        }

        public void Dispose()
        {
            Transaction.Dispose();
            Connection.Dispose();
        }
    }
}
