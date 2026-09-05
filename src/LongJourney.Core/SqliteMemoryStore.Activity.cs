using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

public sealed partial class SqliteMemoryStore : IActivityRecorder
{
    /// <summary>Marks live instrumentation coverage even on days with no calls.</summary>
    public void ActivateActivityRecording(DateTimeOffset now)
    {
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "INSERT OR IGNORE INTO state(key,value) VALUES('activity.started_at',$at)",
            ("$at", FormatTimestamp(now)));
    }

    public void BeginActivity(string id, string kind, string origin, string? parentId, string? sourceId,
        long? runId, string? workKey, long? chargedRunId, DateTimeOffset now, string detailsJson)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        ExecuteNonQuery(db, tx, "INSERT OR IGNORE INTO state(key,value) VALUES('activity.started_at',$at)",
            ("$at", FormatTimestamp(now)));
        ExecuteNonQuery(db, tx, """
            INSERT INTO activity_operations(id,kind,origin,parent_id,source_id,run_id,work_key,charged_run_id,started_at,status,details_json)
            VALUES($id,$kind,$origin,$parent,$source,$run,$work,$charged,$at,'pending',$details)
            """, ("$id", id), ("$kind", kind), ("$origin", origin), ("$parent", parentId),
            ("$source", sourceId), ("$run", runId), ("$work", workKey), ("$charged", chargedRunId),
            ("$at", FormatTimestamp(now)), ("$details", detailsJson));
        tx.Commit();
    }

    public void UpdateActivity(string id, string detailsJson)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        MergeActivityDetails(db, tx, id, detailsJson);
        tx.Commit();
    }

    private static void MergeActivityDetails(SqliteConnection db, SqliteTransaction tx, string? id,
        object details, string? sourceId = null)
    {
        if (id is null)
        {
            return;
        }

        var existing = ExecuteScalar(db, tx, "SELECT details_json FROM activity_operations WHERE id=$id", ("$id", id)) as string;
        if (existing is null)
        {
            return;
        }

        var merged = JsonNode.Parse(existing)!.AsObject();
        var update = JsonNode.Parse(details is string json ? json : JsonSerializer.Serialize(details, JsonDefaults.Options))!.AsObject();
        foreach (var property in update)
        {
            merged[property.Key] = property.Value?.DeepClone();
        }

        ExecuteNonQuery(db, tx, "UPDATE activity_operations SET details_json=$json,source_id=COALESCE($source,source_id) WHERE id=$id",
            ("$json", merged.ToJsonString(JsonDefaults.Options)), ("$source", sourceId), ("$id", id));
    }

    public void FinishActivity(string id, string status, string? errorType, DateTimeOffset now)
    {
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "UPDATE activity_operations SET status=$status,error_type=$error,completed_at=$at WHERE id=$id AND completed_at IS NULL",
            ("$status", status), ("$error", errorType), ("$at", FormatTimestamp(now)), ("$id", id));
    }

    public void ApplyActivityRelation(RelationProposal proposal, RunRecord run, string workKey, int index,
        DateTimeOffset now, string? rejectionReason = null)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        if (ExecuteScalar(db, tx, "SELECT outcome FROM activity_relation_results WHERE run_id=$run AND work_key=$work AND proposal_index=$index",
            ("$run", run.Id), ("$work", workKey), ("$index", index)) is not null)
        {
            return;
        }

        var outcome = "rejected";
        if (rejectionReason is null)
        {
            try
            {
                outcome = AddRelationCore(db, tx, proposal, run, now) ? "appended" : "already_exists";
            }
            catch (InvariantException error)
            {
                rejectionReason = error.Message;
            }
        }

        if (rejectionReason is not null)
        {
            ExecuteNonQuery(db, tx, "INSERT OR IGNORE INTO rejected_proposals(run_id,work_key,proposal_index,reason) VALUES($run,$work,$index,$reason)",
                ("$run", run.Id), ("$work", workKey), ("$index", index), ("$reason", rejectionReason));
        }

        ExecuteNonQuery(db, tx, """
            INSERT INTO activity_relation_results(run_id,work_key,proposal_index,activity_id,at,outcome,memory_id,related_memory_id,kind,reason)
            VALUES($run,$work,$index,$activity,$at,$outcome,$memory,$related,$kind,$reason)
            """, ("$run", run.Id), ("$work", workKey), ("$index", index), ("$activity", ActivityScope.CurrentId),
            ("$at", FormatTimestamp(now)), ("$outcome", outcome), ("$memory", proposal.MemoryId),
            ("$related", proposal.RelatedMemoryId), ("$kind", proposal.Kind.ToString().ToLowerInvariant()), ("$reason", rejectionReason));
        tx.Commit();
    }
}
