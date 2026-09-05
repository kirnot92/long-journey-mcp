using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

/// <summary>Owns all graph mutations. SQL is never supplied by cognition.</summary>
public sealed partial class SqliteMemoryStore : IMemoryStore
{
    private readonly EngineOptions _options;
    private readonly SourceArchive _sourceArchive;
    private readonly string _connectionString;
    private readonly object _mutationGate = new();
    public string DatabasePath { get; }

    public SqliteMemoryStore(EngineOptions options)
    {
        options.Validate();
        _options = options;
        var corpusDirectory = Path.GetFullPath(options.DataDirectory);
        _sourceArchive = new SourceArchive(corpusDirectory);
        DatabasePath = Path.Combine(corpusDirectory, "memory.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = false
        }.ToString();
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        ExecuteNonQuery(db, null, SqliteSchema.Create);
        var existingBase = ExecuteScalar(db, null, "SELECT value FROM state WHERE key='root_base'")?.ToString();
        if (existingBase is not null && existingBase != options.RootBase.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvariantException("RootBase differs from this corpus. It cannot be changed without validating existing memories.");
        }

        ExecuteNonQuery(db, null, "INSERT OR IGNORE INTO state(key,value) VALUES('root_base',$base)", ("$base", options.RootBase));
        RecoverSourceFiles(db);
    }

    private SqliteConnection OpenConnection()
    {
        var db = new SqliteConnection(_connectionString);
        try
        {
            db.Open();
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SqliteCommand CreateCommand(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (key, value) in parameters)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        return command;
    }

    private static int ExecuteNonQuery(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = CreateCommand(db, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = CreateCommand(db, tx, sql, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string CreateId(string prefix) => prefix + Guid.NewGuid().ToString("N");

    public SourceArtifact SaveSource(string raw, DateTimeOffset now)
    {
        if (raw.Length > _options.MaxRawCharacters)
        {
            throw new InputException(
                $"raw has {raw.Length} UTF-16 characters, exceeding MaxRawCharacters={_options.MaxRawCharacters}. " +
                "Select one coherent experience and remove unrelated material while preserving its necessary context. " +
                "Do not mechanically split the same experience to fit the limit.");
        }

        var contentHash = SourceArchive.ComputeContentHash(raw);
        lock (_mutationGate)
        {
            using var db = OpenConnection();
            using var tx = db.BeginTransaction();
            var existingSource = ReadSourceRow(db, tx, "content_hash=$value", contentHash);
            if (existingSource is not null)
            {
                var existingArtifact = _sourceArchive.Read(existingSource);
                if (!string.Equals(existingArtifact.Raw, raw, StringComparison.Ordinal))
                {
                    throw new InvariantException("Source hash collision or corrupt source archive.");
                }

                MergeActivityDetails(db, tx, ActivityScope.CurrentId,
                    new { new_source = false, source_status = existingSource.Status }, existingSource.Id);
                tx.Commit();
                return existingArtifact;
            }

            // Persist the file first: a crash before commit leaves an artifact startup can recover.
            using var archiveWrite = _sourceArchive.WriteImmutable(raw, contentHash, now);
            InsertSource(db, tx, archiveWrite.Artifact.Source);
            MergeActivityDetails(db, tx, ActivityScope.CurrentId,
                new { new_source = true, source_status = "pending" }, archiveWrite.Artifact.Source.Id);
            tx.Commit();
            return archiveWrite.Artifact;
        }
    }

    private void RecoverSourceFiles(SqliteConnection db)
    {
        foreach (var artifact in _sourceArchive.EnumerateArtifacts())
        {
            InsertSource(db, null, artifact.Source);
        }

        // The host acquires CorpusLease before constructing this store.
        // Only that owner may make interrupted extraction available for retry.
        ExecuteNonQuery(db, null, "UPDATE sources SET status='pending' WHERE status='processing'");
    }

    private static void InsertSource(SqliteConnection db, SqliteTransaction? tx, SourceRecord source)
    {
        ExecuteNonQuery(db, tx, "INSERT OR IGNORE INTO sources(id,content_hash,relative_path,created_at,status) VALUES($id,$hash,$path,$at,'pending')",
            ("$id", source.Id), ("$hash", source.ContentHash), ("$path", source.RelativePath), ("$at", FormatTimestamp(source.CreatedAt)));
        ExecuteNonQuery(db, tx, "INSERT INTO state(key,value) VALUES('corpus.first_source_at',$at) ON CONFLICT(key) DO UPDATE SET value=MIN(state.value,excluded.value)", ("$at", FormatTimestamp(source.CreatedAt)));
    }

    private static SourceRecord? ReadSourceRow(SqliteConnection db, SqliteTransaction? tx, string condition, string value)
    {
        using var command = CreateCommand(db, tx, $"SELECT id,content_hash,relative_path,created_at,status FROM sources WHERE {condition}", ("$value", value));
        using var reader = command.ExecuteReader();
        return reader.Read() ? SourceFrom(reader) : null;
    }

    private static SourceRecord SourceFrom(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var contentHash = reader.GetString(1);
        var relativePath = reader.GetString(2);
        var createdAt = ParseTimestamp(reader.GetString(3));
        var status = reader.GetString(4);
        return new SourceRecord(id, contentHash, relativePath, createdAt, status);
    }

    public SourceArtifact ReadSource(string sourceId)
    {
        using var db = OpenConnection();
        var source = ReadSourceRow(db, null, "id=$value", sourceId) ?? throw new InputException("Source not found.");
        return _sourceArchive.Read(source);
    }

    public bool ClaimSource(string sourceId)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        var claimed = ExecuteNonQuery(db, tx, "UPDATE sources SET status='processing' WHERE id=$id AND status IN ('pending','failed')", ("$id", sourceId)) == 1;
        if (claimed)
        {
            MergeActivityDetails(db, tx, ActivityScope.CurrentId, new { source_status = "processing" }, sourceId);
        }
        tx.Commit();
        return claimed;
    }

    public void FailSource(string sourceId)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        var failed = ExecuteNonQuery(db, tx, "UPDATE sources SET status='failed' WHERE id=$id AND status='processing'", ("$id", sourceId)) == 1;
        if (failed)
        {
            MergeActivityDetails(db, tx, ActivityScope.CurrentId, new { source_status = "failed" }, sourceId);
        }
        tx.Commit();
    }

    public IReadOnlyList<SourceRecord> GetIncompleteSources()
    {
        using var db = OpenConnection();
        using var command = CreateCommand(db, null, "SELECT id,content_hash,relative_path,created_at,status FROM sources WHERE status IN ('pending','failed') ORDER BY created_at,id");
        using var reader = command.ExecuteReader();
        var result = new List<SourceRecord>();
        while (reader.Read())
        {
            result.Add(SourceFrom(reader));
        }

        return result;
    }

    public void CompleteSource(string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now)
    {
        CompleteSourceCore(sourceId, observations, now, null);
    }

    /// <summary>Imports shared observations while preserving their identities in an independent corpus.</summary>
    public void CompleteSource(
        string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now,
        IReadOnlyList<string> memoryIds)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        if (memoryIds.Count != observations.Count)
        {
            throw new InvariantException("Shared observation IDs must match the observation count.");
        }

        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var memoryId in memoryIds)
        {
            if (string.IsNullOrWhiteSpace(memoryId) || !uniqueIds.Add(memoryId))
            {
                throw new InvariantException("Shared observation IDs must be nonempty and unique.");
            }
        }

        CompleteSourceCore(sourceId, observations, now, memoryIds);
    }

    private void CompleteSourceCore(
        string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now,
        IReadOnlyList<string>? memoryIds)
    {
        if (observations.Count > _options.MaxObservations)
        {
            throw new InvariantException("Observation count exceeds configured limit.");
        }

        lock (_mutationGate)
        {
            using var db = OpenConnection();
            using var tx = db.BeginTransaction();
            var source = ReadSourceRow(db, tx, "id=$value", sourceId) ?? throw new InvariantException("Missing source.");
            if (source.Status == "complete")
            {
                return;
            }

            if (source.Status != "processing")
            {
                throw new InvariantException("Source is not claimed for extraction.");
            }

            _ = _sourceArchive.Read(source);
            var createdIds = new List<string>();
            for (var index = 0; index < observations.Count; index++)
            {
                var observation = observations[index];
                CheckContent(observation.Content);
                CheckEmbedding(observation.Embedding);
                var id = memoryIds is null ? CreateId("mem_") : memoryIds[index];
                createdIds.Add(id);
                InsertMemory(db, tx, id, 0, observation.Content, sourceId, now, 0, observation.Model, $"source:{sourceId}:{index}");
                ExecuteNonQuery(db, tx, "INSERT INTO memory_roots(memory_id,source_id) VALUES($id,$src)", ("$id", id), ("$src", sourceId));
                ExecuteNonQuery(db, tx, "UPDATE memories SET sealed=1 WHERE id=$id", ("$id", id));
                SaveEmbedding(db, tx, id, observation.Embedding);
            }
            ExecuteNonQuery(db, tx, "UPDATE sources SET status='complete' WHERE id=$id", ("$id", sourceId));
            var activityDetails = new { created_ids = createdIds, returned_ids = createdIds, source_status = "complete" };
            MergeActivityDetails(db, tx, ActivityScope.CurrentId, activityDetails, sourceId);
            MergeActivityDetails(db, tx, ActivityScope.ParentId, new { created_ids = createdIds, source_status = "complete" }, sourceId);
            tx.Commit();
        }
    }

    private void CheckContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > _options.MaxMemoryCharacters)
        {
            throw new InvariantException("Memory content is empty or exceeds the configured character limit.");
        }
    }

    private static void InsertMemory(SqliteConnection db, SqliteTransaction tx, string id, int depth, string content, string? source,
        DateTimeOffset now, long revision, string model, string originKey)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvariantException("Creation model must be recorded.");
        }

        ExecuteNonQuery(db, tx, """
            INSERT INTO memories(id,depth,content,source_ref,created_at,dream_revision,created_by_model,origin_key)
            VALUES($id,$depth,$content,$source,$at,$revision,$model,$origin)
            """, ("$id", id), ("$depth", depth), ("$content", content), ("$source", source),
            ("$at", FormatTimestamp(now)), ("$revision", revision), ("$model", model), ("$origin", originKey));
    }

    public IReadOnlyList<string> LexicalSearch(
        string query,
        int limit,
        int? depth = null,
        long? memoryHighWater = null)
    {
        if (limit < 1)
        {
            return [];
        }

        var searchExpression = BuildLexicalSearchExpression(query);
        if (searchExpression.Length == 0)
        {
            return [];
        }

        using var db = OpenConnection();
        using var command = CreateCommand(db, null, """
            SELECT m.id FROM memory_fts JOIN memories m ON m.seq=memory_fts.rowid
            WHERE memory_fts MATCH $query AND m.sealed=1 AND m.seq <= $max AND ($depth IS NULL OR m.depth=$depth)
            ORDER BY bm25(memory_fts),m.id LIMIT $limit
            """,
            ("$query", searchExpression),
            ("$max", memoryHighWater ?? long.MaxValue),
            ("$depth", depth),
            ("$limit", limit));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static string BuildLexicalSearchExpression(string query)
    {
        // User queries are text, never executable FTS syntax.
        const int maximumTokens = 64;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var quotedTokens = new List<string>();
        var matches = Regex.Matches(query, @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant);
        foreach (Match match in matches)
        {
            var token = match.Value;
            if (!seenTokens.Add(token))
            {
                continue;
            }

            var escapedToken = token.Replace("\"", "\"\"", StringComparison.Ordinal);
            quotedTokens.Add('"' + escapedToken + '"');
            if (quotedTokens.Count == maximumTokens)
            {
                break;
            }
        }

        return string.Join(" OR ", quotedTokens);
    }

    private static void CheckEmbedding(EmbeddingVector vector)
    {
        const string invalidEmbedding = "Embedding must be finite, nonzero and identify its model/dimensions.";
        if (string.IsNullOrWhiteSpace(vector.Space) || vector.Values.Length == 0)
        {
            throw new InvariantException(invalidEmbedding);
        }

        var hasNonzeroValue = false;
        foreach (var value in vector.Values)
        {
            if (!float.IsFinite(value))
            {
                throw new InvariantException(invalidEmbedding);
            }

            if (value != 0)
            {
                hasNonzeroValue = true;
            }
        }

        if (!hasNonzeroValue)
        {
            throw new InvariantException(invalidEmbedding);
        }
    }

    private static void SaveEmbedding(SqliteConnection db, SqliteTransaction? tx, string id, EmbeddingVector vector)
    {
        CheckEmbedding(vector);
        var knownDimension = ExecuteScalar(db, tx, "SELECT dimensions FROM embeddings WHERE space=$space LIMIT 1", ("$space", vector.Space));
        if (knownDimension is not null && Convert.ToInt32(knownDimension, CultureInfo.InvariantCulture) != vector.Values.Length)
        {
            throw new InvariantException("Embedding dimensions changed within the same model space.");
        }

        ExecuteNonQuery(db, tx, "INSERT INTO embeddings(memory_id,space,dimensions,vector_json) VALUES($id,$space,$dims,$vector) ON CONFLICT(memory_id,space) DO UPDATE SET vector_json=excluded.vector_json",
            ("$id", id), ("$space", vector.Space), ("$dims", vector.Values.Length), ("$vector", JsonSerializer.Serialize(vector.Values)));
    }

    public void SaveEmbedding(string memoryId, EmbeddingVector embedding)
    {
        lock (_mutationGate)
        {
            using var db = OpenConnection();
            using var tx = db.BeginTransaction();
            SaveEmbedding(db, tx, memoryId, embedding);
            tx.Commit();
        }
    }

    public EmbeddingVector? GetEmbedding(string memoryId, string space)
    {
        using var db = OpenConnection();
        var json = ExecuteScalar(db, null, "SELECT vector_json FROM embeddings WHERE memory_id=$id AND space=$space", ("$id", memoryId), ("$space", space)) as string;
        return json is null ? null : new EmbeddingVector(space, JsonSerializer.Deserialize<float[]>(json)!);
    }

    public IReadOnlyList<StoredEmbedding> GetEmbeddings(string space)
    {
        using var db = OpenConnection();
        using var command = CreateCommand(db, null, "SELECT memory_id,vector_json FROM embeddings WHERE space=$space ORDER BY memory_id", ("$space", space));
        using var reader = command.ExecuteReader();
        var result = new List<StoredEmbedding>();
        while (reader.Read())
        {
            var memoryId = reader.GetString(0);
            var vectorJson = reader.GetString(1);
            var values = JsonSerializer.Deserialize<float[]>(vectorJson)!;
            var embedding = new EmbeddingVector(space, values);
            result.Add(new StoredEmbedding(memoryId, embedding));
        }

        return result;
    }

    // Recall history changes timestamps only; it never adds evidence or changes retrieval scores.
    public void RecordRecall(IReadOnlyList<string> ids, DateTimeOffset now)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        var recalledIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!recalledIds.Add(id))
            {
                continue;
            }

            ExecuteNonQuery(db, tx, "INSERT INTO recall_events(memory_id,recalled_at) VALUES($id,$at)", ("$id", id), ("$at", FormatTimestamp(now)));
            ExecuteNonQuery(db, tx, "UPDATE memories SET last_recalled_at=CASE WHEN last_recalled_at IS NULL OR last_recalled_at<$at THEN $at ELSE last_recalled_at END WHERE id=$id", ("$id", id), ("$at", FormatTimestamp(now)));
        }
        MergeActivityDetails(db, tx, ActivityScope.CurrentId, new { returned_ids = ids });
        tx.Commit();
    }

    public RunRecord GetOrCreateRun(
        RunKind kind,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset now,
        decimal? budgetUsd)
    {
        if (start >= end)
        {
            throw new InputException("Run period must have positive duration.");
        }

        if (kind == RunKind.Meditation && budgetUsd is not > 0)
        {
            throw new InputException("Set MeditationBudgetUsd before running meditation.");
        }

        lock (_mutationGate)
        {
            using var db = OpenConnection();
            using var tx = db.BeginTransaction();
            ExecuteNonQuery(db, tx, """
                INSERT OR IGNORE INTO runs(kind,period_start,period_end,started_at,memory_high_water,relation_high_water,recall_high_water,status,budget_usd)
                SELECT $kind,$start,$end,$now,COALESCE((SELECT MAX(seq) FROM memories),0),COALESCE((SELECT MAX(seq) FROM relations),0),
                COALESCE((SELECT MAX(seq) FROM recall_events),0),'running',$budget
                """, ("$kind", kind.ToString().ToLowerInvariant()), ("$start", FormatTimestamp(start)), ("$end", FormatTimestamp(end)), ("$now", FormatTimestamp(now)),
                ("$budget", kind == RunKind.Dream ? null : budgetUsd?.ToString(CultureInfo.InvariantCulture)));
            using var command = CreateCommand(db, tx, RunSelect + " WHERE kind=$kind AND period_start=$start AND period_end=$end", ("$kind", kind.ToString().ToLowerInvariant()), ("$start", FormatTimestamp(start)), ("$end", FormatTimestamp(end)));
            using var reader = command.ExecuteReader();
            reader.Read();
            var run = RunFrom(reader);
            reader.Close();
            tx.Commit();
            return run;
        }
    }

    private const string RunSelect = "SELECT id,kind,period_start,period_end,started_at,memory_high_water,relation_high_water,recall_high_water,status,budget_usd FROM runs";
    private static RunRecord RunFrom(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var kind = Enum.Parse<RunKind>(reader.GetString(1), true);
        var periodStart = ParseTimestamp(reader.GetString(2));
        var periodEnd = ParseTimestamp(reader.GetString(3));
        var startedAt = ParseTimestamp(reader.GetString(4));
        var memoryHighWater = reader.GetInt64(5);
        var relationHighWater = reader.GetInt64(6);
        var recallHighWater = reader.GetInt64(7);
        var status = reader.GetString(8);
        decimal? budgetUsd = reader.IsDBNull(9)
            ? null
            : decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture);

        return new RunRecord(
            id, kind, periodStart, periodEnd, startedAt,
            memoryHighWater, relationHighWater, recallHighWater, status, budgetUsd);
    }

    public IReadOnlyList<RunRecord> GetRuns()
    {
        using var db = OpenConnection();
        using var command = CreateCommand(db, null, RunSelect + " ORDER BY id");
        using var reader = command.ExecuteReader();
        var result = new List<RunRecord>();
        while (reader.Read())
        {
            result.Add(RunFrom(reader));
        }

        return result;
    }

    public bool AreWorkItemsInitialized(long runId)
    {
        using var db = OpenConnection();
        return ReadWorkInitialized(db, null, runId);
    }

    private static bool ReadWorkInitialized(SqliteConnection db, SqliteTransaction? tx, long runId)
    {
        var initializedValue = ExecuteScalar(db, tx,
            "SELECT work_initialized FROM runs WHERE id=$id", ("$id", runId));
        if (initializedValue is null)
        {
            throw new InvariantException("Work refers to a missing run.");
        }

        return Convert.ToInt32(initializedValue, CultureInfo.InvariantCulture) != 0;
    }

    public void EnsureWorkItems(long runId, IReadOnlyList<WorkSeed> items)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        if (ReadWorkInitialized(db, tx, runId))
        {
            return;
        }

        InsertWorkItems(db, tx, runId, items);
        tx.Commit();
    }

    public void FinishUnprioritizedMeditation(long runId, IReadOnlyList<WorkSeed> items, DateTimeOffset now)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        var eligible = ExecuteScalar(db, tx, """
            SELECT COUNT(*) FROM runs
            WHERE id=$id AND kind='meditation' AND status='running' AND work_initialized=0
            """, ("$id", runId));
        if (Convert.ToInt32(eligible, CultureInfo.InvariantCulture) != 1)
        {
            throw new InvariantException("Only an uninitialized, running Meditation can finish without priority.");
        }

        // These ordinals identify pending carry work, never a processing order for this closed run.
        InsertWorkItems(db, tx, runId, items);
        ExecuteNonQuery(db, tx, """
            UPDATE runs SET status='budget_exhausted', finished_at=$at WHERE id=$id
            """, ("$at", FormatTimestamp(now)), ("$id", runId));
        tx.Commit();
    }

    private static void InsertWorkItems(
        SqliteConnection db, SqliteTransaction tx, long runId, IReadOnlyList<WorkSeed> items)
    {
        foreach (var item in items)
        {
            ExecuteNonQuery(db, tx, "INSERT INTO run_work(run_id,work_key,phase,memory_id,ordinal,status) VALUES($run,$key,$phase,$memory,$ordinal,'pending')",
                ("$run", runId), ("$key", item.Key), ("$phase", item.Phase), ("$memory", item.MemoryId), ("$ordinal", item.Ordinal));
        }

        ExecuteNonQuery(db, tx, "UPDATE runs SET work_initialized=1 WHERE id=$id", ("$id", runId));
    }

    public IReadOnlyList<RunWorkItem> GetWorkItems(long runId)
    {
        using var db = OpenConnection();
        using var command = CreateCommand(db, null, "SELECT run_id,work_key,phase,memory_id,ordinal,status,proposal_json,model FROM run_work WHERE run_id=$id ORDER BY ordinal,work_key", ("$id", runId));
        using var reader = command.ExecuteReader();
        var result = new List<RunWorkItem>();
        while (reader.Read())
        {
            var itemRunId = reader.GetInt64(0);
            var key = reader.GetString(1);
            var phase = reader.GetString(2);
            var memoryId = reader.GetString(3);
            var ordinal = reader.GetInt32(4);
            var status = reader.GetString(5);
            var proposalJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var model = reader.IsDBNull(7) ? null : reader.GetString(7);
            result.Add(new RunWorkItem(
                itemRunId, key, phase, memoryId, ordinal, status, proposalJson, model));
        }

        return result;
    }

    public void SaveWorkProposal(long runId, string key, string proposalJson, string model)
    {
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "UPDATE run_work SET proposal_json=$json,model=$model WHERE run_id=$run AND work_key=$key AND proposal_json IS NULL",
            ("$json", proposalJson), ("$model", model), ("$run", runId), ("$key", key));
    }

    public void CompleteWork(long runId, string key)
    {
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "UPDATE run_work SET status='complete' WHERE run_id=$run AND work_key=$key", ("$run", runId), ("$key", key));
    }

    public void RejectProposal(long runId, string key, int index, string reason)
    {
        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "INSERT OR IGNORE INTO rejected_proposals(run_id,work_key,proposal_index,reason) VALUES($run,$key,$index,$reason)",
            ("$run", runId), ("$key", key), ("$index", index), ("$reason", reason));
    }

    public int GetRejectedProposalCount(long runId)
    {
        using var db = OpenConnection();
        return Convert.ToInt32(ExecuteScalar(db, null, "SELECT COUNT(*) FROM rejected_proposals WHERE run_id=$id", ("$id", runId)), CultureInfo.InvariantCulture);
    }

    private (int Depth, HashSet<string> Roots) ValidateParentsAndCollectRoots(
        SqliteConnection db,
        SqliteTransaction? tx,
        AbstractionProposal proposal,
        RunRecord evidenceRun,
        IReadOnlyCollection<string> allowedParents)
    {
        CheckContent(proposal.Content);
        var distinctParents = new HashSet<string>(proposal.DerivedFrom, StringComparer.Ordinal);
        if (proposal.DerivedFrom.Count < _options.RootBase ||
            distinctParents.Count != proposal.DerivedFrom.Count)
        {
            throw new InvariantException("Abstraction requires at least B distinct parents.");
        }

        var allowedParentIds = new HashSet<string>(allowedParents, StringComparer.Ordinal);
        int? parentDepth = null;
        var sourceRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parentId in proposal.DerivedFrom)
        {
            if (!allowedParentIds.Contains(parentId))
            {
                throw new InvariantException("Parent was not provided to the model.");
            }

            using (var command = CreateCommand(db, tx, """
                SELECT depth,dream_revision,seq FROM memories WHERE id=$id AND sealed=1
                """, ("$id", parentId)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvariantException("Parent does not exist.");
                }

                var parentRevision = reader.GetInt64(1);
                var parentSequence = reader.GetInt64(2);
                if (parentRevision >= evidenceRun.Id || parentSequence > evidenceRun.MemoryHighWater)
                {
                    throw new InvariantException("Parent violates the run generation barrier.");
                }

                var depth = reader.GetInt32(0);
                if (parentDepth is not null && parentDepth != depth)
                {
                    throw new InvariantException("Parents must have the same depth.");
                }

                parentDepth = depth;
            }

            // Count distinct sources across all parents, including parents that share evidence.
            using var rootCommand = CreateCommand(db, tx,
                "SELECT source_id FROM memory_roots WHERE memory_id=$id", ("$id", parentId));
            using var rootReader = rootCommand.ExecuteReader();
            while (rootReader.Read())
            {
                sourceRoots.Add(rootReader.GetString(0));
            }
        }

        var childDepth = checked(parentDepth!.Value + 1);
        var minimumSourceRoots = BigInteger.Pow(_options.RootBase, childDepth);
        if (new BigInteger(sourceRoots.Count) < minimumSourceRoots)
        {
            throw new InvariantException("Insufficient distinct Source roots for B^depth.");
        }

        // New IDs and strictly decreasing parent depth make cycles impossible.
        return (childDepth, sourceRoots);
    }

    public void ValidateAbstraction(
        AbstractionProposal proposal,
        RunRecord run,
        IReadOnlyCollection<string> allowedParents)
    {
        using var db = OpenConnection();
        _ = ValidateParentsAndCollectRoots(db, null, proposal, run, allowedParents);
    }

    public MemoryRecord AddAbstraction(
        AbstractionProposal proposal,
        string model,
        RunRecord run,
        string workKey,
        int proposalIndex,
        IReadOnlyCollection<string> allowedParents,
        EmbeddingVector embedding,
        DateTimeOffset now)
    {
        var originKey = $"run:{run.Id}:{workKey}:{proposalIndex}";
        string memoryId;
        lock (_mutationGate)
        {
            using var db = OpenConnection();
            using var tx = db.BeginTransaction();
            var existingMemoryId = ExecuteScalar(db, tx,
                "SELECT id FROM memories WHERE origin_key=$origin", ("$origin", originKey)) as string;
            if (existingMemoryId is not null)
            {
                tx.Commit();
                return GetMemory(existingMemoryId)!;
            }

            // A later week may finish work owned by a budget-exhausted run.
            // Its origin and evidence run stay fixed even though API charges go to the later week.
            var runStatus = ExecuteScalar(db, tx,
                "SELECT status FROM runs WHERE id=$id", ("$id", run.Id)) as string;
            if (runStatus is not ("running" or "budget_exhausted"))
            {
                throw new InvariantException("Cannot add a memory to a finished run.");
            }

            // Revalidate inside the transaction, even if the caller checked before paying for embedding.
            var (depth, sourceRoots) = ValidateParentsAndCollectRoots(db, tx, proposal, run, allowedParents);
            CheckEmbedding(embedding);
            memoryId = CreateId("mem_");
            InsertMemory(db, tx, memoryId, depth, proposal.Content, null, now, run.Id, model, originKey);

            foreach (var parentId in proposal.DerivedFrom)
            {
                ExecuteNonQuery(db, tx,
                    "INSERT INTO derived_from(child_id,parent_id) VALUES($child,$parent)",
                    ("$child", memoryId), ("$parent", parentId));
            }

            foreach (var sourceId in sourceRoots)
            {
                ExecuteNonQuery(db, tx,
                    "INSERT INTO memory_roots(memory_id,source_id) VALUES($id,$source)",
                    ("$id", memoryId), ("$source", sourceId));
            }

            // Sealing fixes provenance; publish it together with its embedding in the same commit.
            ExecuteNonQuery(db, tx, "UPDATE memories SET sealed=1 WHERE id=$id", ("$id", memoryId));
            SaveEmbedding(db, tx, memoryId, embedding);
            tx.Commit();
        }

        return GetMemory(memoryId)!;
    }

    public void AddRelation(RelationProposal proposal, RunRecord run, DateTimeOffset now)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        AddRelationCore(db, tx, proposal, run, now);
        tx.Commit();
    }

    private static bool AddRelationCore(SqliteConnection db, SqliteTransaction tx,
        RelationProposal proposal, RunRecord run, DateTimeOffset now)
    {
        if (proposal.MemoryId == proposal.RelatedMemoryId || !Enum.IsDefined(proposal.Kind))
        {
            throw new InvariantException("Invalid relation.");
        }

        foreach (var id in new[] { proposal.MemoryId, proposal.RelatedMemoryId })
        {
            var count = Convert.ToInt32(ExecuteScalar(db, tx, "SELECT COUNT(*) FROM memories WHERE id=$id AND seq<=$max AND dream_revision<$revision AND sealed=1",
                ("$id", id), ("$max", run.MemoryHighWater), ("$revision", run.Id)), CultureInfo.InvariantCulture);
            if (count != 1)
            {
                throw new InvariantException("Relation references a memory outside the run snapshot.");
            }
        }
        return ExecuteNonQuery(db, tx, "INSERT OR IGNORE INTO relations(memory_id,related_memory_id,kind,related_at,run_id) VALUES($a,$b,$kind,$at,$run)",
            ("$a", proposal.MemoryId), ("$b", proposal.RelatedMemoryId), ("$kind", proposal.Kind.ToString().ToLowerInvariant()), ("$at", FormatTimestamp(now)), ("$run", run.Id)) == 1;
    }

    public void FinishRun(long runId, string status, DateTimeOffset now)
    {
        if (status is not ("complete" or "budget_exhausted"))
        {
            throw new InputException("Unsupported terminal run status.");
        }

        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "UPDATE runs SET status=$status,finished_at=$at WHERE id=$id AND status='running'", ("$status", status), ("$at", FormatTimestamp(now)), ("$id", runId));
    }

    // Budget check and reservation share a transaction so concurrent requests cannot overspend.
    public UsageReservation ReserveUsage(
        long? runId,
        string model,
        string operation,
        decimal maximumUsd,
        DateTimeOffset now)
    {
        if (maximumUsd < 0)
        {
            throw new InputException("Usage reservation must not be negative.");
        }

        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        if (runId is not null)
        {
            var budgetUsd = ReadActiveRunBudgetUsd(db, tx, runId.Value);
            if (budgetUsd is not null)
            {
                var accountedUsd = ReadAccountedUsageUsd(db, tx, runId.Value);
                if (accountedUsd + maximumUsd > budgetUsd.Value)
                {
                    throw new BudgetExceededException(
                        "Next API request's maximum cost exceeds the remaining meditation budget.");
                }
            }
        }
        var reservation = new UsageReservation(CreateId("call_"), runId, model, operation, maximumUsd);
        ExecuteNonQuery(db, tx, "INSERT INTO api_calls(id,run_id,model,operation,reserved_usd,created_at) VALUES($id,$run,$model,$op,$cost,$at)",
            ("$id", reservation.Id), ("$run", runId), ("$model", model), ("$op", operation), ("$cost", maximumUsd.ToString(CultureInfo.InvariantCulture)), ("$at", FormatTimestamp(now)));
        ExecuteNonQuery(db, tx, "INSERT INTO activity_api_calls(api_call_id,activity_id,settings_json) VALUES($call,$activity,$settings)",
            ("$call", reservation.Id), ("$activity", ActivityScope.CurrentId), ("$settings", ActivityScope.ApiSettingsJson));
        tx.Commit();
        return reservation;
    }

    private static decimal? ReadActiveRunBudgetUsd(
        SqliteConnection db,
        SqliteTransaction tx,
        long runId)
    {
        using var command = CreateCommand(db, tx,
            "SELECT budget_usd FROM runs WHERE id = $id AND status = 'running'",
            ("$id", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvariantException("Cannot charge an inactive run.");
        }

        return reader.IsDBNull(0)
            ? null
            : decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
    }

    public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now)
    {
        if (usage.InputTokens < 0 ||
            usage.CachedInputTokens < 0 ||
            usage.CacheWriteTokens < 0 ||
            usage.OutputTokens < 0 ||
            usage.CostUsd < 0 ||
            usage.CachedInputTokens + usage.CacheWriteTokens > usage.InputTokens)
        {
            throw new InvariantException("Invalid API usage.");
        }

        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "UPDATE api_calls SET actual_usd=$cost,usage_json=$json,completed_at=$at WHERE id=$id AND completed_at IS NULL",
            ("$cost", usage.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$json", JsonSerializer.Serialize(usage, JsonDefaults.Options)), ("$at", FormatTimestamp(now)), ("$id", reservationId));
    }

    private static decimal ReadAccountedUsageUsd(SqliteConnection db, SqliteTransaction? tx, long runId)
    {
        using var command = CreateCommand(db, tx, "SELECT COALESCE(actual_usd,reserved_usd) FROM api_calls WHERE run_id=$id", ("$id", runId));
        using var reader = command.ExecuteReader();
        decimal accountedUsd = 0;
        while (reader.Read())
        {
            accountedUsd += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        }

        return accountedUsd;
    }

    public decimal GetRunAccountedUsd(long runId)
    {
        using var db = OpenConnection();
        return ReadAccountedUsageUsd(db, null, runId);
    }

    public string? GetState(string key)
    {
        using var db = OpenConnection();
        return ExecuteScalar(db, null, "SELECT value FROM state WHERE key=$key", ("$key", key)) as string;
    }

    public void SetState(string key, string value)
    {
        if (key == "root_base")
        {
            throw new InvariantException("Root base is an immutable corpus setting.");
        }

        using var db = OpenConnection();
        ExecuteNonQuery(db, null, "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
    }


}
