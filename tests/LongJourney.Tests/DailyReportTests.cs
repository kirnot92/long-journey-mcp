using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Tests;

public sealed class DailyReportTests
{
    [Fact]
    public void MissingCorpusDoesNotCreateAnything()
    {
        var directory = Path.Combine(Path.GetTempPath(), "daily-report-missing-" + Guid.NewGuid().ToString("N"));
        var reports = new DailyReportService(directory, "UTC");
        Assert.Throws<InputException>(() => reports.Export(new DateOnly(2026, 9, 5)));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void LegacyReportPreservesKnownCorpusAndCostsButMarksCallsUnknown()
    {
        using var fixture = new Fixture(instrumented: false);
        fixture.Source("source", "old raw", "2026-09-04T01:00:00Z");
        fixture.Memory("d0", "source", "legacy content", "2026-09-04T01:00:00Z");
        fixture.Execute("INSERT INTO api_calls VALUES('api',NULL,'legacy-model','recall','1','0.3',NULL,'2026-09-04T02:00:00Z','2026-09-04T02:01:00Z')");
        var report = fixture.Service.Export(new DateOnly(2026, 9, 4));
        var json = Parse(report);
        Assert.Equal("unknown_legacy", json["coverage"]!["status"]!.GetValue<string>());
        Assert.Null(json["summary"]!["remember"]!["calls"]);
        Assert.Null(json["summary"]!["recall"]!["returned_d0_total"]);
        Assert.Equal(1, json["summary"]!["remember"]!["created_d0"]!.GetValue<int>());
        Assert.Equal(.3m, json["summary"]!["costs"]!["actual_usd"]!.GetValue<decimal>());
        Assert.Equal(1, json["summary"]!["costs"]!["unattributed_calls"]!.GetValue<int>());
        Assert.Empty(fixture.Service.ExportClosedDays());
    }

    [Fact]
    public void ReportLinksRawObservationsOrderedRecallAndThinkAndAtomicRelationResults()
    {
        using var fixture = new Fixture();
        var raw = "한글🙂\nraw | <tag>";
        var content = "observation | <script>\n" + new string('x', 20000);
        fixture.Source("source", raw, "2026-09-04T01:00:00Z");
        fixture.Memory("d0a", "source", content, "2026-09-04T01:00:01Z");
        fixture.Memory("d0b", "source", "second", "2026-09-04T01:00:01Z");
        fixture.Operation("remember", "remember", "agent", "2026-09-04T01:00:00Z", "complete",
            new { raw_characters = raw.Length, raw_bytes = Encoding.UTF8.GetByteCount(raw), new_source = true, created_ids = new[] { "d0a", "d0b" }, returned_ids = new[] { "d0a", "d0b" } }, "source");
        fixture.Operation("extract", "extraction", "agent", "2026-09-04T01:00:00Z", "complete", new { created_ids = new[] { "d0a", "d0b" } }, "source", parent: "remember");
        fixture.Operation("duplicate", "remember", "agent", "2026-09-04T02:00:00Z", "complete",
            new { raw_characters = raw.Length, raw_bytes = Encoding.UTF8.GetByteCount(raw), new_source = false, returned_ids = new[] { "d0a", "d0b" } }, "source");
        fixture.Operation("recall1", "recall", "agent", "2026-09-04T03:00:00Z", "complete",
            new { tool = "recall", query = "| <query>\n", context = "context", candidate_ids = new[] { "d0b", "d0a" }, returned_ids = new[] { "d0a", "d0b" } });
        fixture.Operation("recall2", "recall", "agent", "2026-09-04T03:01:00Z", "complete",
            new { tool = "think", query = "accumulated design principles", context = (string?)null, candidate_ids = new[] { "d0a" }, returned_ids = new[] { "d0a" } });
        fixture.Execute("INSERT INTO runs VALUES(1,'dream','2026-09-01T00:00:00Z','2026-09-02T00:00:00Z','2026-09-04T04:00:00Z','complete')");
        fixture.Operation("assim", "assimilation", "dream", "2026-09-04T04:00:00Z", "complete",
            new { seed_id = "d0b", candidate_ids = new[] { "d0a" }, model_invoked = true, proposal_reused = false, relations = new[] { new { memory_id = "d0a", related_memory_id = "d0b", kind = "positive" } } }, run: 1, work: "assimilate:d0b");
        fixture.Execute("INSERT INTO activity_relation_results VALUES(1,'assimilate:d0b',0,'assim','2026-09-04T04:01:00Z','appended','d0a','d0b','positive',NULL)");
        fixture.Execute("INSERT INTO api_calls VALUES('api',1,'model','assimilation','2','0.1','{\"input_tokens\":50,\"output_tokens\":10}','2026-09-04T04:00:00Z','2026-09-04T04:01:00Z')");
        fixture.Execute("INSERT INTO activity_api_calls VALUES('api','assim','{\"reasoning_effort\":\"high\"}')");
        var export = fixture.Service.Export(new DateOnly(2026, 9, 4));
        var json = Parse(export);
        var summary = json["summary"]!;
        Assert.Equal(2, summary["remember"]!["calls"]!.GetValue<int>());
        Assert.Equal(Encoding.UTF8.GetByteCount(raw) * 2, summary["remember"]!["submitted_raw_bytes"]!.GetValue<int>());
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), summary["remember"]!["new_stored_raw_bytes_known"]!.GetValue<int>());
        Assert.Equal(2, summary["remember"]!["created_d0"]!.GetValue<int>());
        Assert.Equal(2, summary["recall"]!["calls"]!.GetValue<int>());
        Assert.Equal(3, summary["recall"]!["returned_d0_total"]!.GetValue<int>());
        Assert.Equal(2, summary["recall"]!["returned_d0_distinct"]!.GetValue<int>());
        Assert.Equal(1, summary["assimilation"]!["appended_categories"]!["same_source_d0"]!.GetValue<int>());
        var recalled = json["operations"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "recall1")!;
        Assert.Equal("d0b", recalled["details"]!["candidate_ids"]![0]!.GetValue<string>());
        Assert.Equal("| <query>\n", recalled["details"]!["query"]!.GetValue<string>());
        Assert.Equal("recall", recalled["details"]!["tool"]!.GetValue<string>());
        var thought = json["operations"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "recall2")!;
        Assert.Equal("recall", thought["kind"]!.GetValue<string>());
        Assert.Equal("think", thought["details"]!["tool"]!.GetValue<string>());
        Assert.Equal("accumulated design principles", thought["details"]!["query"]!.GetValue<string>());
        Assert.Null(thought["details"]!["context"]);
        Assert.Equal("d0a", thought["details"]!["returned_ids"]![0]!.GetValue<string>());
        Assert.Equal(content, json["memories"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == "d0a")!["content"]!.GetValue<string>());
        Assert.Equal("high", json["api_calls"]![0]!["settings"]!["reasoning_effort"]!.GetValue<string>());
        Assert.DoesNotContain("last_recalled_at", File.ReadAllText(export.JsonPath));
        Assert.Contains(export.SnapshotId, File.ReadAllText(export.MarkdownPath));
        Assert.Contains("| Recall / Think calls | 2 |", File.ReadAllText(export.MarkdownPath));
        Assert.DoesNotContain("<script>", File.ReadAllText(export.MarkdownPath));
    }

    [Fact]
    public void MidnightCallsAndLateSettlementRefreshTheOriginalDayWithoutCorpusMutation()
    {
        using var fixture = new Fixture();
        fixture.Operation("late", "recall", "agent", "2026-09-04T23:59:00Z", "pending", new { query = "late", candidate_ids = Array.Empty<string>() });
        fixture.Execute("INSERT INTO api_calls VALUES('api',NULL,'model','recall','2',NULL,NULL,'2026-09-04T23:59:30Z',NULL)");
        fixture.Execute("INSERT INTO activity_api_calls VALUES('api','late','{}')");
        var exports = fixture.Service.ExportClosedDays();
        Assert.Equal(2, exports.Count);
        Assert.Empty(fixture.Service.ExportClosedDays());
        var first = exports.Single(row => row.Date == new DateOnly(2026, 9, 4));
        Assert.Equal(2m, Parse(first)["summary"]!["costs"]!["unsettled_reserved_usd"]!.GetValue<decimal>());
        fixture.Execute("UPDATE api_calls SET actual_usd='0.25',completed_at='2026-09-05T00:01:00Z' WHERE id='api'");
        fixture.Execute("UPDATE activity_operations SET status='complete',completed_at='2026-09-05T00:01:00Z',details_json='{\"returned_ids\":[],\"candidate_ids\":[]}' WHERE id='late'");
        var refreshed = fixture.Service.ExportClosedDays();
        Assert.Single(refreshed);
        var json = Parse(refreshed[0]);
        Assert.Equal(1, json["summary"]!["recall"]!["calls"]!.GetValue<int>());
        Assert.Equal(.25m, json["summary"]!["costs"]!["actual_usd"]!.GetValue<decimal>());
        Assert.Equal("2026-09-05T00:01:00Z", json["operations"]![0]!["completed_at"]!.GetValue<string>());
        var today = Parse(fixture.Service.Export(new DateOnly(2026, 9, 5)));
        Assert.True(today["provisional"]!.GetValue<bool>());
        Assert.Equal(0, today["summary"]!["recall"]!["calls"]!.GetValue<int>());
        var before = File.ReadAllBytes(Path.Combine(fixture.Directory, "memory.db"));
        fixture.Service.Export(new DateOnly(2026, 9, 4));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(fixture.Directory, "memory.db")));
        File.Delete(refreshed[0].MarkdownPath);
        Assert.Single(fixture.Service.ExportClosedDays());
    }

    [Fact]
    public void TimeZoneBoundaryAndMissingSourceRemainExplicit()
    {
        using var fixture = new Fixture();
        fixture.Source("source", "raw", "2026-09-03T16:00:00Z");
        File.Delete(Path.Combine(fixture.Directory, "sources", "source.md"));
        fixture.Memory("d0", "source", "content", "2026-09-03T16:00:00Z");
        fixture.Operation("remember", "remember", "agent", "2026-09-03T16:00:00Z", "failed", new { raw_bytes = 3, raw_characters = 3 }, "source");
        var service = new DailyReportService(fixture.Directory, "Asia/Seoul", new FrozenClock());
        var json = Parse(service.Export(new DateOnly(2026, 9, 4)));
        Assert.Equal(1, json["summary"]!["remember"]!["calls"]!.GetValue<int>());
        Assert.Equal("missing", json["sources"]![0]!["artifact_status"]!.GetValue<string>());
        Assert.Null(json["sources"]![0]!["raw_bytes"]);
        Assert.Equal(1, json["summary"]!["remember"]!["new_source_size_unknown"]!.GetValue<int>());
    }

    [Fact]
    public void UnattributedApiReservationsHaveStableNullFieldsAndDoNotBreakExport()
    {
        using var fixture = new Fixture();
        fixture.Execute("INSERT INTO api_calls VALUES('api',NULL,'model','embedding','2',NULL,NULL,'2026-09-04T04:00:00Z',NULL)");
        fixture.Execute("INSERT INTO activity_api_calls VALUES('api',NULL,NULL)");
        var report = Parse(fixture.Service.Export(new DateOnly(2026, 9, 4)));
        var call = report["api_calls"]![0]!.AsObject();
        Assert.True(call.ContainsKey("usage"));
        Assert.True(call.ContainsKey("settings"));
        Assert.Null(call["usage"]);
        Assert.Null(call["settings"]);
        Assert.Null(call["activity_id"]);
        Assert.Equal(1, report["summary"]!["costs"]!["unattributed_calls"]!.GetValue<int>());
        Assert.Equal(2m, report["summary"]!["costs"]!["unsettled_reserved_usd"]!.GetValue<decimal>());
    }

    [Fact]
    public void LateSourceExtractionAndUnattemptedWorkAreVisibleWithoutMixingRecallCost()
    {
        using var fixture = new Fixture();
        fixture.Source("source", "experience", "2026-09-04T23:59:00Z");
        fixture.Memory("d0", "source", "observation", "2026-09-05T00:01:00Z");
        fixture.Execute("INSERT INTO runs VALUES(1,'dream','2026-09-01T00:00:00Z','2026-09-02T00:00:00Z','2026-09-04T04:00:00Z','running')");
        fixture.Execute("INSERT INTO run_work VALUES(1,'pending','assimilation','d0',0,'pending',NULL,NULL)");
        fixture.Operation("assim", "assimilation", "dream", "2026-09-04T04:00:00Z", "complete",
            new { relations = Array.Empty<object>() }, run: 1, work: "attempted");
        fixture.Operation("recall", "recall", "agent", "2026-09-04T04:00:00Z", "complete", new { });
        fixture.Execute("INSERT INTO api_calls VALUES('assim_api',1,'model','assimilation','1','0.1',NULL,'2026-09-04T04:00:00Z','2026-09-04T04:01:00Z')");
        fixture.Execute("INSERT INTO api_calls VALUES('recall_api',NULL,'model','recall','1','0.9',NULL,'2026-09-04T04:00:00Z','2026-09-04T04:01:00Z')");
        fixture.Execute("INSERT INTO activity_api_calls VALUES('assim_api','assim','{}'),('recall_api','recall','{}')");
        var report = Parse(fixture.Service.Export(new DateOnly(2026, 9, 4)));
        Assert.Equal(0, report["summary"]!["remember"]!["created_d0"]!.GetValue<int>());
        Assert.Equal(1, report["summary"]!["remember"]!["created_d0_per_new_source"]!["max"]!.GetValue<int>());
        Assert.Equal(1, report["summary"]!["assimilation"]!["referenced_run_work_without_recorded_attempt_as_of"]!.GetValue<int>());
        Assert.Single(report["work_items"]!.AsArray());
        Assert.Equal(1m, report["summary"]!["costs"]!["actual_usd"]!.GetValue<decimal>());
        Assert.Equal(.1m, report["summary"]!["costs"]!["actual_usd_per_assimilation_work"]!.GetValue<decimal>());
    }

    [Fact]
    public void ExportLeasePreventsTwoWritersFromMixingReportPairs()
    {
        using var fixture = new Fixture();
        var first = fixture.Service.Export(new DateOnly(2026, 9, 4));
        using (var lease = new FileStream(Path.Combine(fixture.Directory, "reports", "daily", ".export.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => new DailyReportService(fixture.Directory, "UTC", new FrozenClock())
                .Export(new DateOnly(2026, 9, 4)));
            Assert.Equal(first.SnapshotId, Parse(first)["snapshot_id"]!.GetValue<string>());
        }
        var exported = fixture.Service.Export(new DateOnly(2026, 9, 4));
        Assert.Contains(exported.SnapshotId, File.ReadAllText(exported.MarkdownPath));
        Assert.Equal(exported.SnapshotId, Parse(exported)["snapshot_id"]!.GetValue<string>());
    }

    [Fact]
    public void AutomaticPollRepairsAnInterruptedExternalExportWithoutDatabaseChanges()
    {
        using var fixture = new Fixture();
        var original = fixture.Service.ExportClosedDays().Single(row => row.Date == new DateOnly(2026, 9, 4));
        var interruptedId = Guid.NewGuid().ToString("N");
        File.WriteAllText(original.MarkdownPath, File.ReadAllText(original.MarkdownPath).Replace(original.SnapshotId, interruptedId));
        File.SetLastWriteTimeUtc(original.MarkdownPath, DateTime.UtcNow.AddSeconds(5));
        var repaired = Assert.Single(fixture.Service.ExportClosedDays());
        Assert.Equal(original.Date, repaired.Date);
        Assert.Contains(repaired.SnapshotId, File.ReadAllText(repaired.MarkdownPath));
        Assert.Equal(repaired.SnapshotId, Parse(repaired)["snapshot_id"]!.GetValue<string>());
    }

    private static JsonObject Parse(DailyReportExport report) => JsonNode.Parse(File.ReadAllText(report.JsonPath))!.AsObject();
    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-05T04:00:00Z");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "daily-report-tests-" + Guid.NewGuid().ToString("N"));
        public DailyReportService Service { get; }

        public Fixture(bool instrumented = true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Execute("""
                CREATE TABLE state(key TEXT PRIMARY KEY,value TEXT);
                CREATE TABLE sources(id TEXT PRIMARY KEY,content_hash TEXT,relative_path TEXT,created_at TEXT,status TEXT);
                CREATE TABLE memories(id TEXT PRIMARY KEY,depth INTEGER,content TEXT,source_ref TEXT,created_at TEXT,dream_revision INTEGER,created_by_model TEXT,origin_key TEXT,seq INTEGER,sealed INTEGER,last_recalled_at TEXT);
                CREATE TABLE derived_from(child_id TEXT,parent_id TEXT);
                CREATE TABLE api_calls(id TEXT PRIMARY KEY,run_id INTEGER,model TEXT,operation TEXT,reserved_usd TEXT,actual_usd TEXT,usage_json TEXT,created_at TEXT,completed_at TEXT);
                CREATE TABLE runs(id INTEGER PRIMARY KEY,kind TEXT,period_start TEXT,period_end TEXT,started_at TEXT,status TEXT);
                CREATE TABLE run_work(run_id INTEGER,work_key TEXT,phase TEXT,memory_id TEXT,ordinal INTEGER,status TEXT,proposal_json TEXT,model TEXT);
                """);
            if (instrumented)
            {
                Execute("""
                    INSERT INTO state VALUES('activity.started_at','2026-09-03T00:00:00Z');
                    CREATE TABLE activity_operations(id TEXT PRIMARY KEY,kind TEXT,origin TEXT,parent_id TEXT,source_id TEXT,run_id INTEGER,work_key TEXT,charged_run_id INTEGER,started_at TEXT,completed_at TEXT,status TEXT,error_type TEXT,details_json TEXT);
                    CREATE TABLE activity_relation_results(run_id INTEGER,work_key TEXT,proposal_index INTEGER,activity_id TEXT,at TEXT,outcome TEXT,memory_id TEXT,related_memory_id TEXT,kind TEXT,reason TEXT,PRIMARY KEY(run_id,work_key,proposal_index));
                    CREATE TABLE activity_api_calls(api_call_id TEXT PRIMARY KEY,activity_id TEXT,settings_json TEXT);
                    """);
            }

            Service = new DailyReportService(Directory, "UTC", new FrozenClock());
        }

        public void Source(string id, string raw, string at)
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "sources"));
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            File.WriteAllText(Path.Combine(Directory, "sources", id + ".md"), "---\nid: " + id + "\n---\n\n" + raw);
            Execute("INSERT INTO sources VALUES($id,$hash,$path,$at,'complete')", ("$id", id), ("$hash", hash), ("$path", "sources/" + id + ".md"), ("$at", at));
        }

        public void Memory(string id, string source, string content, string at) =>
            Execute("INSERT INTO memories VALUES($id,0,$content,$source,$at,0,'model',$id,1,1,NULL)", ("$id", id), ("$content", content), ("$source", source), ("$at", at));

        public void Operation(string id, string kind, string origin, string at, string status, object details,
            string? source = null, string? parent = null, int? run = null, string? work = null) =>
            Execute("INSERT INTO activity_operations VALUES($id,$kind,$origin,$parent,$source,$run,$work,$run,$at,NULL,$status,NULL,$details)",
                ("$id", id), ("$kind", kind), ("$origin", origin), ("$parent", parent), ("$source", source), ("$run", run), ("$work", work), ("$at", at), ("$status", status), ("$details", System.Text.Json.JsonSerializer.Serialize(details)));

        public void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Directory, "memory.db"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
