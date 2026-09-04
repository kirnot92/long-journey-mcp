using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

public sealed partial class SqliteMemoryStore : IInspectionReader
{
    public InspectionOverview BrowseMemories(InspectionMemoryQuery query)
    {
        ValidateInspectionPage(query.Page, query.Snapshot);
        if (query.Depth < 0 || query.Revision < 0 || query.Search?.Length > 200)
        {
            throw new InputException("Invalid inspection filter.");
        }

        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var memories = ReadInspectionMemories(db, tx, query);
        var depths = new List<InspectionDepth>();
        using (var command = CreateCommand(db, tx, """
            SELECT depth, COUNT(*) FROM memories WHERE sealed = 1 GROUP BY depth ORDER BY depth
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                depths.Add(new InspectionDepth(reader.GetInt32(0), reader.GetInt64(1)));
            }
        }

        var statistics = new InspectionStatistics(
            InspectionCount(db, tx, "SELECT COUNT(*) FROM memories WHERE sealed = 1"),
            InspectionCount(db, tx, "SELECT COUNT(*) FROM sources"),
            InspectionCount(db, tx, "SELECT COUNT(*) FROM relations"), depths);
        var recent = new List<InspectionRelation>();
        using (var command = CreateCommand(db, tx, """
            SELECT memory_id, related_memory_id, kind, related_at
            FROM relations
            ORDER BY related_at DESC, seq DESC
            LIMIT 12
            """))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                recent.Add(new InspectionRelation(
                    reader.GetString(0), reader.GetString(1),
                    Enum.Parse<RelationKind>(reader.GetString(2), true),
                    ParseTimestamp(reader.GetString(3))));
            }
        }

        tx.Commit();
        return new InspectionOverview(memories, statistics, recent);
    }

    private static InspectionPage<InspectionMemory> ReadInspectionMemories(
        SqliteConnection db, SqliteTransaction tx, InspectionMemoryQuery query, string? sourceId = null)
    {
        var snapshot = query.Snapshot ?? InspectionCount(db, tx, "SELECT COALESCE(MAX(seq), 0) FROM memories");
        const string selection = """
            FROM memories
            WHERE sealed = 1 AND seq <= $snapshot
                AND ($depth IS NULL OR depth = $depth)
                AND ($search IS NULL OR instr(content, $search) > 0 OR instr(id, $search) > 0)
                AND ($source IS NULL OR source_ref = $source)
                AND ($revision IS NULL OR dream_revision = $revision)
            """;
        var parameters = new (string, object?)[]
        {
            ("$snapshot", snapshot), ("$depth", query.Depth), ("$search", query.Search),
            ("$source", sourceId), ("$revision", query.Revision),
            ("$limit", InspectionPage<InspectionMemory>.PageSize),
            ("$offset", (long)(query.Page - 1) * InspectionPage<InspectionMemory>.PageSize)
        };
        var total = InspectionCount(db, tx, "SELECT COUNT(*) " + selection, parameters);
        using var command = CreateCommand(db, tx, """
            SELECT id, depth, substr(content, 1, 240), created_at
            """ + "\n" + selection + """

            ORDER BY created_at DESC, seq DESC
            LIMIT $limit OFFSET $offset
            """, parameters);
        using var reader = command.ExecuteReader();
        var items = new List<InspectionMemory>();
        while (reader.Read())
        {
            items.Add(new InspectionMemory(reader.GetString(0), reader.GetInt32(1),
                reader.GetString(2), ParseTimestamp(reader.GetString(3))));
        }

        return new InspectionPage<InspectionMemory>(items, total, query.Page, snapshot);
    }

    // UNION visits shared ancestors once. Each returned memory still retains every direct parent edge.
    // LIMIT bounds the recursive work as well as materialization, even for a very large ancestor DAG.
    private const string InspectionAncestry = """
        WITH RECURSIVE ancestry(id) AS (
            SELECT id FROM memories WHERE id = $selected AND sealed = 1
            UNION
            SELECT d.parent_id FROM derived_from d JOIN ancestry a ON d.child_id = a.id
            ORDER BY 1
            LIMIT 201
        )
        SELECT id FROM ancestry
        """;

    public InspectionTrace? ReadTrace(string id)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var count = InspectionCount(db, tx,
            "SELECT COUNT(*) FROM (" + InspectionAncestry + ")", ("$selected", id));
        if (count == 0)
        {
            return null;
        }

        var scope = new MemoryReadScope(
            "m.id IN (" + InspectionAncestry + " LIMIT 200)", id);
        var memories = ReadMemories(db, tx, scope);
        memories.Sort((left, right) =>
        {
            var depth = right.Depth.CompareTo(left.Depth);
            return depth != 0 ? depth : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });
        tx.Commit();
        return new InspectionTrace(id, memories, count > InspectionTrace.NodeLimit);
    }

    public InspectionSource? InspectSource(string id, int page = 1, long? snapshot = null)
    {
        ValidateInspectionPage(page, snapshot);
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var source = ReadSourceRow(db, tx, "id = $value", id);
        if (source is null)
        {
            return null;
        }

        var observations = ReadInspectionMemories(db, tx, new InspectionMemoryQuery(Page: page, Snapshot: snapshot), id);
        string? raw = null;
        string? error = null;
        try
        {
            raw = _sourceArchive.Read(source).Raw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvariantException or FormatException or KeyNotFoundException or ArgumentException)
        {
            // Metadata and observations remain inspectable; filesystem paths and parser details stay private.
            error = "저장된 원문을 읽을 수 없습니다. 파일이 없거나 무결성을 확인하지 못했습니다.";
        }

        tx.Commit();
        return new InspectionSource(source, raw, error, observations);
    }

    private const string InspectionRunSelect = """
        SELECT id, kind, period_start, period_end, started_at,
            memory_high_water, relation_high_water, recall_high_water, status, budget_usd,
            finished_at, work_initialized
        FROM runs
        """;

    private static InspectionRun InspectionRunFrom(SqliteDataReader reader)
    {
        var run = RunFrom(reader);
        DateTimeOffset? finished = reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10));
        return new InspectionRun(run, finished, reader.GetInt32(11) != 0);
    }

    public InspectionPage<InspectionRun> BrowseRuns(int page = 1, long? snapshot = null)
    {
        ValidateInspectionPage(page, snapshot);
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        var upper = snapshot ?? InspectionCount(db, tx, "SELECT COALESCE(MAX(id), 0) FROM runs");
        var total = InspectionCount(db, tx, "SELECT COUNT(*) FROM runs WHERE id <= $max", ("$max", upper));
        using var command = CreateCommand(db, tx, InspectionRunSelect + """

            WHERE id <= $max ORDER BY started_at DESC, id DESC LIMIT 25 OFFSET $offset
            """, ("$max", upper), ("$offset", (long)(page - 1) * 25));
        using var reader = command.ExecuteReader();
        var runs = new List<InspectionRun>();
        while (reader.Read())
        {
            runs.Add(InspectionRunFrom(reader));
        }

        reader.Close();
        tx.Commit();
        return new InspectionPage<InspectionRun>(runs, total, page, upper);
    }

    public InspectionRunDetail? InspectRun(long id, int page = 1)
    {
        ValidateInspectionPage(page, null);
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        InspectionRun run;
        using (var command = CreateCommand(db, tx, InspectionRunSelect + " WHERE id = $id", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            run = InspectionRunFrom(reader);
        }

        var work = new List<InspectionWorkSummary>();
        using (var command = CreateCommand(db, tx, """
            SELECT run_id, work_key, phase, memory_id, ordinal, status,
                proposal_json IS NOT NULL, model
            FROM run_work
            WHERE run_id = $id ORDER BY ordinal, work_key LIMIT 25 OFFSET $offset
            """, ("$id", id), ("$offset", (long)(page - 1) * 25)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                work.Add(new InspectionWorkSummary(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                    reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        var total = InspectionCount(db, tx, "SELECT COUNT(*) FROM run_work WHERE run_id = $id", ("$id", id));
        var completed = InspectionCount(db, tx,
            "SELECT COUNT(*) FROM run_work WHERE run_id = $id AND status = 'complete'", ("$id", id));
        var outputs = InspectionCount(db, tx,
            "SELECT COUNT(*) FROM memories WHERE sealed = 1 AND dream_revision = $id", ("$id", id));
        var cost = ReadInspectionCost(db, tx, id);
        tx.Commit();
        return new InspectionRunDetail(run, cost,
            new InspectionPage<InspectionWorkSummary>(work, total, page, id), completed, outputs);
    }

    private static InspectionCost ReadInspectionCost(SqliteConnection db, SqliteTransaction tx, long id)
    {
        // Money is decimal text. SQLite's floating point SUM would lose the ledger's exact precision.
        using var command = CreateCommand(db, tx,
            "SELECT actual_usd, reserved_usd FROM api_calls WHERE run_id = $id", ("$id", id));
        using var reader = command.ExecuteReader();
        decimal actual = 0;
        decimal reserved = 0;
        long unsettled = 0;
        while (reader.Read())
        {
            if (reader.IsDBNull(0))
            {
                reserved += decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                unsettled++;
            }
            else
            {
                actual += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            }
        }

        return new InspectionCost(actual, reserved, unsettled);
    }

    public InspectionWorkDetail? InspectWork(long runId, string key)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction(deferred: true);
        RunWorkItem work;
        using (var command = CreateCommand(db, tx, """
            SELECT run_id, work_key, phase, memory_id, ordinal, status, proposal_json, model
            FROM run_work WHERE run_id = $id AND work_key = $key
            """, ("$id", runId), ("$key", key)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            work = new RunWorkItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        var rejections = new List<InspectionRejection>();
        using (var command = CreateCommand(db, tx, """
            SELECT proposal_index, reason FROM rejected_proposals
            WHERE run_id = $id AND work_key = $key ORDER BY proposal_index
            """, ("$id", runId), ("$key", key)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rejections.Add(new InspectionRejection(reader.GetInt32(0), reader.GetString(1)));
            }
        }

        InspectionWorkOrigin? origin = null;
        if (work.Phase == "carry" && work.Key.StartsWith("carry:", StringComparison.Ordinal))
        {
            var separator = work.Key.IndexOf(':', 6);
            if (separator > 6 && long.TryParse(work.Key.AsSpan(6, separator - 6),
                NumberStyles.None, CultureInfo.InvariantCulture, out var originRunId))
            {
                origin = new InspectionWorkOrigin(originRunId, work.Key[(separator + 1)..]);
            }
        }

        tx.Commit();
        return new InspectionWorkDetail(work, rejections, origin);
    }

    private static long InspectionCount(
        SqliteConnection db, SqliteTransaction tx, string sql, params (string, object?)[] parameters)
    {
        return Convert.ToInt64(ExecuteScalar(db, tx, sql, parameters), CultureInfo.InvariantCulture);
    }

    private static void ValidateInspectionPage(int page, long? snapshot)
    {
        if (page < 1 || snapshot < 0)
        {
            throw new InputException("Invalid inspection page.");
        }
    }
}
