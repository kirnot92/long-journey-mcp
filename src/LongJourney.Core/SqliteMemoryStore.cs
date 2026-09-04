using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LongJourney.Core;

/// <summary>Owns all graph mutations. SQL is never supplied by cognition.</summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly EngineOptions _options;
    private readonly string _directory;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string DatabasePath { get; }

    public SqliteMemoryStore(EngineOptions options)
    {
        options.Validate();
        _options = options;
        _directory = Path.GetFullPath(options.DataDirectory);
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "sources"));
        DatabasePath = Path.Combine(_directory, "memory.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, ForeignKeys = true, DefaultTimeout = 30, Pooling = false
        }.ToString();
        using var db = Open();
        Exec(db, null, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        Exec(db, null, Schema);
        var existingBase = Scalar(db, null, "SELECT value FROM state WHERE key='root_base'")?.ToString();
        if (existingBase is not null && existingBase != options.RootBase.ToString(CultureInfo.InvariantCulture))
            throw new InvariantException("RootBase differs from this corpus. It cannot be changed without validating existing memories.");
        Exec(db, null, "INSERT OR IGNORE INTO state(key,value) VALUES('root_base',$base)", ("$base", options.RootBase));
        RecoverSourceFiles(db);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private static int Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(db, tx, sql, parameters);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(raw)));
    private static string Id(string prefix) => prefix + Guid.NewGuid().ToString("N");

    public SourceArtifact SaveSource(string raw, DateTimeOffset now)
    {
        if (raw.Length > _options.MaxRawCharacters) throw new InputException($"raw exceeds {_options.MaxRawCharacters} characters; submit one observation at a time.");
        var hash = Hash(raw);
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var found = ReadSourceRow(db, tx, "content_hash=$value", hash);
            if (found is not null)
            {
                var artifact = ReadArtifact(found);
                if (!string.Equals(artifact.Raw, raw, StringComparison.Ordinal)) throw new InvariantException("Source hash collision or corrupt source archive.");
                tx.Commit();
                return artifact;
            }

            // File first: a crash before the DB commit leaves a recoverable immutable artifact.
            var sourceId = "src_" + hash;
            var relativePath = $"sources/{now.UtcDateTime:yyyy/MM/dd}/{sourceId}.md";
            var source = new SourceRecord(sourceId, hash, relativePath, now.ToUniversalTime(), "pending");
            var path = ResolveSourcePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var header = $"---\nid: {source.Id}\ncreated_at: {Stamp(source.CreatedAt)}\ncontent_sha256: {hash}\n---\n\n";
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Utf8.GetBytes(header + raw));
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                {
                    var parsed = ParseArtifact(path);
                    if (parsed.Raw != raw) throw new InvariantException("Existing source artifact differs from its content hash.");
                    source = parsed.Source;
                }
                else File.Move(temporaryPath, path);
                InsertSource(db, tx, source);
                tx.Commit();
                return new SourceArtifact(source, raw);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }

    private string ResolveSourcePath(string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_directory, relative));
        if (!fullPath.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvariantException("Source path is outside the corpus directory.");
        return fullPath;
    }

    private SourceArtifact ParseArtifact(string path)
    {
        var text = File.ReadAllText(path, Utf8);
        var boundary = text.IndexOf("\n---\n\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || boundary < 0)
            throw new InvariantException($"Invalid source header: {Path.GetFileName(path)}");
        var fields = text[4..boundary].Split('\n').Select(line => line.Split(": ", 2, StringSplitOptions.None))
            .ToDictionary(pair => pair[0], pair => pair.Length == 2 ? pair[1] : "", StringComparer.Ordinal);
        var raw = text[(boundary + 6)..];
        var hash = Hash(raw);
        if (!fields.TryGetValue("content_sha256", out var expected) || expected != hash || fields["id"] != "src_" + hash)
            throw new InvariantException("Source archive integrity check failed.");
        return new SourceArtifact(new SourceRecord(fields["id"], hash, Path.GetRelativePath(_directory, path).Replace('\\', '/'),
            Time(fields["created_at"]), "pending"), raw);
    }

    private SourceArtifact ReadArtifact(SourceRecord source)
    {
        var artifact = ParseArtifact(ResolveSourcePath(source.RelativePath));
        if (artifact.Source.Id != source.Id || artifact.Source.ContentHash != source.ContentHash || artifact.Source.CreatedAt != source.CreatedAt)
            throw new InvariantException("Source metadata does not match its immutable artifact.");
        return new SourceArtifact(source, artifact.Raw);
    }

    private void RecoverSourceFiles(SqliteConnection db)
    {
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_directory, "sources"), "src_*.md", SearchOption.AllDirectories))
        {
            var artifact = ParseArtifact(path);
            InsertSource(db, null, artifact.Source);
        }
        // A single server owns a corpus. On startup it can resume work interrupted by its previous process.
        Exec(db, null, "UPDATE sources SET status='pending' WHERE status='processing'");
    }

    private static void InsertSource(SqliteConnection db, SqliteTransaction? tx, SourceRecord source)
    {
        Exec(db, tx, "INSERT OR IGNORE INTO sources(id,content_hash,relative_path,created_at,status) VALUES($id,$hash,$path,$at,'pending')",
            ("$id", source.Id), ("$hash", source.ContentHash), ("$path", source.RelativePath), ("$at", Stamp(source.CreatedAt)));
        Exec(db, tx, "INSERT INTO state(key,value) VALUES('corpus.first_source_at',$at) ON CONFLICT(key) DO UPDATE SET value=MIN(state.value,excluded.value)", ("$at", Stamp(source.CreatedAt)));
    }

    private static SourceRecord? ReadSourceRow(SqliteConnection db, SqliteTransaction? tx, string condition, string value)
    {
        using var command = Command(db, tx, $"SELECT id,content_hash,relative_path,created_at,status FROM sources WHERE {condition}", ("$value", value));
        using var reader = command.ExecuteReader();
        return reader.Read() ? SourceFrom(reader) : null;
    }

    private static SourceRecord SourceFrom(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Time(reader.GetString(3)), reader.GetString(4));

    public SourceArtifact ReadSource(string sourceId)
    {
        using var db = Open();
        var source = ReadSourceRow(db, null, "id=$value", sourceId) ?? throw new InputException("Source not found.");
        return ReadArtifact(source);
    }

    public RememberResult ReadRememberResult(string sourceId, bool duplicate)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var source = ReadSourceRow(db, tx, "id=$value", sourceId) ?? throw new InputException("Source not found.");
        var memories = ReadMemories(db, tx, long.MaxValue, long.MaxValue).Where(m => m.SourceRef == sourceId).ToArray();
        tx.Commit();
        return new RememberResult(sourceId, duplicate, memories, source.Status);
    }

    public bool ClaimSource(string sourceId)
    {
        using var db = Open();
        return Exec(db, null, "UPDATE sources SET status='processing' WHERE id=$id AND status IN ('pending','failed')", ("$id", sourceId)) == 1;
    }

    public void FailSource(string sourceId)
    {
        using var db = Open();
        Exec(db, null, "UPDATE sources SET status='failed' WHERE id=$id AND status='processing'", ("$id", sourceId));
    }

    public IReadOnlyList<SourceRecord> GetIncompleteSources()
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT id,content_hash,relative_path,created_at,status FROM sources WHERE status IN ('pending','failed') ORDER BY created_at,id");
        using var reader = command.ExecuteReader();
        var result = new List<SourceRecord>();
        while (reader.Read()) result.Add(SourceFrom(reader));
        return result;
    }

    public void CompleteSource(string sourceId, IReadOnlyList<NewObservation> observations, DateTimeOffset now)
    {
        if (observations.Count > _options.MaxObservations) throw new InvariantException("Observation count exceeds configured limit.");
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var source = ReadSourceRow(db, tx, "id=$value", sourceId) ?? throw new InvariantException("Missing source.");
            if (source.Status == "complete") return;
            if (source.Status != "processing") throw new InvariantException("Source is not claimed for extraction.");
            _ = ReadArtifact(source);
            for (var index = 0; index < observations.Count; index++)
            {
                var observation = observations[index];
                CheckContent(observation.Content);
                CheckEmbedding(observation.Embedding);
                var id = Id("mem_");
                InsertMemory(db, tx, id, 0, observation.Content, sourceId, now, 0, observation.Model, $"source:{sourceId}:{index}");
                Exec(db, tx, "INSERT INTO memory_roots(memory_id,source_id) VALUES($id,$src)", ("$id", id), ("$src", sourceId));
                Exec(db, tx, "UPDATE memories SET sealed=1 WHERE id=$id", ("$id", id));
                SaveEmbedding(db, tx, id, observation.Embedding);
            }
            Exec(db, tx, "UPDATE sources SET status='complete' WHERE id=$id", ("$id", sourceId));
            tx.Commit();
        }
    }

    private void CheckContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > _options.MaxMemoryCharacters)
            throw new InvariantException("Memory content is empty or exceeds the configured character limit.");
    }

    private static void InsertMemory(SqliteConnection db, SqliteTransaction tx, string id, int depth, string content, string? source,
        DateTimeOffset now, long revision, string model, string originKey)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new InvariantException("Creation model must be recorded.");
        Exec(db, tx, """
            INSERT INTO memories(id,depth,content,source_ref,created_at,dream_revision,created_by_model,origin_key)
            VALUES($id,$depth,$content,$source,$at,$revision,$model,$origin)
            """, ("$id", id), ("$depth", depth), ("$content", content), ("$source", source),
            ("$at", Stamp(now)), ("$revision", revision), ("$model", model), ("$origin", originKey));
    }

    public GraphSnapshot ReadSnapshot(RunRecord? run = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: true);
        var memories = ReadMemories(db, tx, run?.MemoryHighWater ?? long.MaxValue, run?.RelationHighWater ?? long.MaxValue);
        using var command = Command(db, tx, "SELECT memory_id,recalled_at,seq FROM recall_events WHERE seq <= $max ORDER BY seq", ("$max", run?.RecallHighWater ?? long.MaxValue));
        using var reader = command.ExecuteReader();
        var recalls = new List<RecallEvent>();
        while (reader.Read()) recalls.Add(new RecallEvent(reader.GetString(0), Time(reader.GetString(1)), reader.GetInt64(2)));
        reader.Close();
        if (run is not null)
        {
            var recalled = recalls.GroupBy(x => x.MemoryId).ToDictionary(x => x.Key, x => x.Max(e => e.RecalledAt));
            memories = memories.Select(m => m with { LastRecalledAt = recalled.TryGetValue(m.Id, out var at) ? at : null }).ToList();
        }
        tx.Commit();
        return new GraphSnapshot(memories, recalls);
    }

    private static List<MemoryRecord> ReadMemories(SqliteConnection db, SqliteTransaction? tx, long memoryMax, long relationMax)
    {
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var command = Command(db, tx, "SELECT child_id,parent_id FROM derived_from ORDER BY child_id,parent_id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (!parents.TryGetValue(reader.GetString(0), out var values)) parents[reader.GetString(0)] = values = [];
                values.Add(reader.GetString(1));
            }
        var relations = new Dictionary<string, List<MemoryRelation>>(StringComparer.Ordinal);
        using (var command = Command(db, tx, "SELECT r.memory_id,r.related_memory_id,r.kind,r.related_at,r.seq FROM relations r JOIN memories m ON m.id=r.related_memory_id WHERE r.seq<=$max AND m.seq<=$mem ORDER BY r.seq", ("$max", relationMax), ("$mem", memoryMax)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (!relations.TryGetValue(reader.GetString(0), out var values)) relations[reader.GetString(0)] = values = [];
                values.Add(new MemoryRelation(reader.GetString(1), Enum.Parse<RelationKind>(reader.GetString(2), true), Time(reader.GetString(3)), reader.GetInt64(4)));
            }
        var result = new List<MemoryRecord>();
        using (var command = Command(db, tx, """
            SELECT m.id,m.depth,m.content,m.source_ref,m.created_at,m.dream_revision,m.last_recalled_at,m.created_by_model,m.seq,
                (SELECT COUNT(*) FROM memory_roots r WHERE r.memory_id=m.id)
            FROM memories m WHERE m.sealed=1 AND m.seq<=$max ORDER BY m.seq
            """, ("$max", memoryMax)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetString(0);
                result.Add(new MemoryRecord(id, reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    parents.TryGetValue(id, out var p) ? p : [], relations.TryGetValue(id, out var r) ? r : [],
                    Time(reader.GetString(4)), reader.GetInt64(5), reader.IsDBNull(6) ? null : Time(reader.GetString(6)),
                    reader.GetString(7), reader.GetInt32(9), reader.GetInt64(8)));
            }
        return result;
    }

    public MemoryRecord? GetMemory(string id) => ReadSnapshot().Memories.FirstOrDefault(m => m.Id == id);
    public IReadOnlyList<MemoryRecord> GetSourceMemories(string sourceId) => ReadSnapshot().Memories.Where(m => m.SourceRef == sourceId).ToArray();

    public IReadOnlyList<string> LexicalSearch(string query, int limit, int? depth = null, long? memoryHighWater = null)
    {
        if (limit < 1) return [];
        // User queries are text, never executable FTS syntax.
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)
            .Cast<Match>().Select(m => m.Value).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (tokens.Length == 0) return [];
        var match = string.Join(" OR ", tokens.Select(t => '"' + t.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'));
        using var db = Open();
        using var command = Command(db, null, """
            SELECT m.id FROM memory_fts JOIN memories m ON m.seq=memory_fts.rowid
            WHERE memory_fts MATCH $query AND m.sealed=1 AND m.seq <= $max AND ($depth IS NULL OR m.depth=$depth)
            ORDER BY bm25(memory_fts),m.id LIMIT $limit
            """, ("$query", match), ("$max", memoryHighWater ?? long.MaxValue), ("$depth", depth), ("$limit", limit));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static void CheckEmbedding(EmbeddingVector vector)
    {
        if (string.IsNullOrWhiteSpace(vector.Space) || vector.Values.Length == 0 || vector.Values.Any(v => !float.IsFinite(v)) || vector.Values.All(v => v == 0))
            throw new InvariantException("Embedding must be finite, nonzero and identify its model/dimensions.");
    }

    private static void SaveEmbedding(SqliteConnection db, SqliteTransaction? tx, string id, EmbeddingVector vector)
    {
        CheckEmbedding(vector);
        var knownDimension = Scalar(db, tx, "SELECT dimensions FROM embeddings WHERE space=$space LIMIT 1", ("$space", vector.Space));
        if (knownDimension is not null && Convert.ToInt32(knownDimension, CultureInfo.InvariantCulture) != vector.Values.Length)
            throw new InvariantException("Embedding dimensions changed within the same model space.");
        Exec(db, tx, "INSERT INTO embeddings(memory_id,space,dimensions,vector_json) VALUES($id,$space,$dims,$vector) ON CONFLICT(memory_id,space) DO UPDATE SET vector_json=excluded.vector_json",
            ("$id", id), ("$space", vector.Space), ("$dims", vector.Values.Length), ("$vector", JsonSerializer.Serialize(vector.Values)));
    }

    public void SaveEmbedding(string memoryId, EmbeddingVector embedding)
    {
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            SaveEmbedding(db, tx, memoryId, embedding);
            tx.Commit();
        }
    }

    public EmbeddingVector? GetEmbedding(string memoryId, string space)
    {
        using var db = Open();
        var json = Scalar(db, null, "SELECT vector_json FROM embeddings WHERE memory_id=$id AND space=$space", ("$id", memoryId), ("$space", space)) as string;
        return json is null ? null : new EmbeddingVector(space, JsonSerializer.Deserialize<float[]>(json)!);
    }

    public IReadOnlyList<StoredEmbedding> GetEmbeddings(string space)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT memory_id,vector_json FROM embeddings WHERE space=$space ORDER BY memory_id", ("$space", space));
        using var reader = command.ExecuteReader();
        var result = new List<StoredEmbedding>();
        while (reader.Read()) result.Add(new StoredEmbedding(reader.GetString(0), new EmbeddingVector(space, JsonSerializer.Deserialize<float[]>(reader.GetString(1))!)));
        return result;
    }

    public void RecordRecall(IReadOnlyList<string> ids, DateTimeOffset now)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            Exec(db, tx, "INSERT INTO recall_events(memory_id,recalled_at) VALUES($id,$at)", ("$id", id), ("$at", Stamp(now)));
            Exec(db, tx, "UPDATE memories SET last_recalled_at=CASE WHEN last_recalled_at IS NULL OR last_recalled_at<$at THEN $at ELSE last_recalled_at END WHERE id=$id", ("$id", id), ("$at", Stamp(now)));
        }
        tx.Commit();
    }

    public RunRecord GetOrCreateRun(RunKind kind, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now, decimal? budgetUsd)
    {
        if (start >= end) throw new InputException("Run period must have positive duration.");
        if (kind == RunKind.Meditation && budgetUsd is not > 0) throw new InputException("Set MeditationBudgetUsd before running meditation.");
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            Exec(db, tx, """
                INSERT OR IGNORE INTO runs(kind,period_start,period_end,started_at,memory_high_water,relation_high_water,recall_high_water,status,budget_usd)
                SELECT $kind,$start,$end,$now,COALESCE((SELECT MAX(seq) FROM memories),0),COALESCE((SELECT MAX(seq) FROM relations),0),
                COALESCE((SELECT MAX(seq) FROM recall_events),0),'running',$budget
                """, ("$kind", kind.ToString().ToLowerInvariant()), ("$start", Stamp(start)), ("$end", Stamp(end)), ("$now", Stamp(now)),
                ("$budget", kind == RunKind.Dream ? null : budgetUsd?.ToString(CultureInfo.InvariantCulture)));
            using var command = Command(db, tx, RunSelect + " WHERE kind=$kind AND period_start=$start AND period_end=$end", ("$kind", kind.ToString().ToLowerInvariant()), ("$start", Stamp(start)), ("$end", Stamp(end)));
            using var reader = command.ExecuteReader();
            reader.Read();
            var run = RunFrom(reader);
            reader.Close();
            tx.Commit();
            return run;
        }
    }

    private const string RunSelect = "SELECT id,kind,period_start,period_end,started_at,memory_high_water,relation_high_water,recall_high_water,status,budget_usd FROM runs";
    private static RunRecord RunFrom(SqliteDataReader reader) => new(reader.GetInt64(0), Enum.Parse<RunKind>(reader.GetString(1), true),
        Time(reader.GetString(2)), Time(reader.GetString(3)), Time(reader.GetString(4)), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
        reader.GetString(8), reader.IsDBNull(9) ? null : decimal.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    public IReadOnlyList<RunRecord> GetRuns()
    {
        using var db = Open();
        using var command = Command(db, null, RunSelect + " ORDER BY id");
        using var reader = command.ExecuteReader();
        var result = new List<RunRecord>();
        while (reader.Read()) result.Add(RunFrom(reader));
        return result;
    }

    public void EnsureWorkItems(long runId, IReadOnlyList<WorkSeed> items)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (Convert.ToInt32(Scalar(db, tx, "SELECT work_initialized FROM runs WHERE id=$id", ("$id", runId)), CultureInfo.InvariantCulture) != 0) return;
        foreach (var item in items)
            Exec(db, tx, "INSERT INTO run_work(run_id,work_key,phase,memory_id,ordinal,status) VALUES($run,$key,$phase,$memory,$ordinal,'pending')",
                ("$run", runId), ("$key", item.Key), ("$phase", item.Phase), ("$memory", item.MemoryId), ("$ordinal", item.Ordinal));
        Exec(db, tx, "UPDATE runs SET work_initialized=1 WHERE id=$id", ("$id", runId));
        tx.Commit();
    }

    public IReadOnlyList<RunWorkItem> GetWorkItems(long runId)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT run_id,work_key,phase,memory_id,ordinal,status,proposal_json,model FROM run_work WHERE run_id=$id ORDER BY ordinal,work_key", ("$id", runId));
        using var reader = command.ExecuteReader();
        var result = new List<RunWorkItem>();
        while (reader.Read()) result.Add(new RunWorkItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result;
    }

    public void SaveWorkProposal(long runId, string key, string proposalJson, string model)
    {
        using var db = Open();
        Exec(db, null, "UPDATE run_work SET proposal_json=$json,model=$model WHERE run_id=$run AND work_key=$key AND proposal_json IS NULL",
            ("$json", proposalJson), ("$model", model), ("$run", runId), ("$key", key));
    }

    public void CompleteWork(long runId, string key)
    {
        using var db = Open();
        Exec(db, null, "UPDATE run_work SET status='complete' WHERE run_id=$run AND work_key=$key", ("$run", runId), ("$key", key));
    }

    public void RejectProposal(long runId, string key, int index, string reason)
    {
        using var db = Open();
        Exec(db, null, "INSERT OR IGNORE INTO rejected_proposals(run_id,work_key,proposal_index,reason) VALUES($run,$key,$index,$reason)",
            ("$run", runId), ("$key", key), ("$index", index), ("$reason", reason));
    }

    public int GetRejectedProposalCount(long runId)
    {
        using var db = Open();
        return Convert.ToInt32(Scalar(db, null, "SELECT COUNT(*) FROM rejected_proposals WHERE run_id=$id", ("$id", runId)), CultureInfo.InvariantCulture);
    }

    private (int Depth, HashSet<string> Roots) CheckAbstraction(SqliteConnection db, SqliteTransaction? tx, AbstractionProposal proposal, RunRecord run, IReadOnlyCollection<string> allowedParents)
    {
        CheckContent(proposal.Content);
        if (proposal.DerivedFrom.Count < _options.RootBase || proposal.DerivedFrom.Distinct(StringComparer.Ordinal).Count() != proposal.DerivedFrom.Count)
            throw new InvariantException("Abstraction requires at least B distinct parents.");
        var allowed = allowedParents.ToHashSet(StringComparer.Ordinal);
        int? parentDepth = null;
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parent in proposal.DerivedFrom)
        {
            if (!allowed.Contains(parent)) throw new InvariantException("Parent was not provided to the model.");
            using (var command = Command(db, tx, "SELECT depth,dream_revision,seq FROM memories WHERE id=$id AND sealed=1", ("$id", parent)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvariantException("Parent does not exist.");
                if (reader.GetInt64(1) >= run.Id || reader.GetInt64(2) > run.MemoryHighWater) throw new InvariantException("Parent violates the run generation barrier.");
                var depth = reader.GetInt32(0);
                if (parentDepth is not null && parentDepth != depth) throw new InvariantException("Parents must have the same depth.");
                parentDepth = depth;
            }
            using var rootCommand = Command(db, tx, "SELECT source_id FROM memory_roots WHERE memory_id=$id", ("$id", parent));
            using var rootReader = rootCommand.ExecuteReader();
            while (rootReader.Read()) roots.Add(rootReader.GetString(0));
        }
        var childDepth = checked(parentDepth!.Value + 1);
        if (new BigInteger(roots.Count) < BigInteger.Pow(_options.RootBase, childDepth))
            throw new InvariantException("Insufficient distinct Source roots for B^depth.");
        // New IDs and strictly decreasing parent depth make cycles impossible.
        return (childDepth, roots);
    }

    public void ValidateAbstraction(AbstractionProposal proposal, RunRecord run, IReadOnlyCollection<string> allowedParents)
    {
        using var db = Open();
        _ = CheckAbstraction(db, null, proposal, run, allowedParents);
    }

    public MemoryRecord AddAbstraction(AbstractionProposal proposal, string model, RunRecord run, string workKey, int proposalIndex,
        IReadOnlyCollection<string> allowedParents, EmbeddingVector embedding, DateTimeOffset now)
    {
        var origin = $"run:{run.Id}:{workKey}:{proposalIndex}";
        string id;
        lock (_gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var existing = Scalar(db, tx, "SELECT id FROM memories WHERE origin_key=$origin", ("$origin", origin)) as string;
            if (existing is not null) { tx.Commit(); return GetMemory(existing)!; }
            if (Scalar(db, tx, "SELECT status FROM runs WHERE id=$id", ("$id", run.Id)) as string is not ("running" or "budget_exhausted"))
                throw new InvariantException("Cannot add a memory to a finished run.");
            var (depth, roots) = CheckAbstraction(db, tx, proposal, run, allowedParents);
            CheckEmbedding(embedding);
            id = Id("mem_");
            InsertMemory(db, tx, id, depth, proposal.Content, null, now, run.Id, model, origin);
            foreach (var parent in proposal.DerivedFrom)
                Exec(db, tx, "INSERT INTO derived_from(child_id,parent_id) VALUES($child,$parent)", ("$child", id), ("$parent", parent));
            foreach (var root in roots)
                Exec(db, tx, "INSERT INTO memory_roots(memory_id,source_id) VALUES($id,$source)", ("$id", id), ("$source", root));
            Exec(db, tx, "UPDATE memories SET sealed=1 WHERE id=$id", ("$id", id));
            SaveEmbedding(db, tx, id, embedding);
            tx.Commit();
        }
        return GetMemory(id)!;
    }

    public MemoryRecord? GetAppliedAbstraction(long runId, string workKey, int proposalIndex)
    {
        using var db = Open();
        var id = Scalar(db, null, "SELECT id FROM memories WHERE origin_key=$origin", ("$origin", $"run:{runId}:{workKey}:{proposalIndex}")) as string;
        return id is null ? null : GetMemory(id);
    }

    public void AddRelation(RelationProposal proposal, RunRecord run, DateTimeOffset now)
    {
        if (proposal.MemoryId == proposal.RelatedMemoryId || !Enum.IsDefined(proposal.Kind)) throw new InvariantException("Invalid relation.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var id in new[] { proposal.MemoryId, proposal.RelatedMemoryId })
        {
            var count = Convert.ToInt32(Scalar(db, tx, "SELECT COUNT(*) FROM memories WHERE id=$id AND seq<=$max AND dream_revision<$revision AND sealed=1",
                ("$id", id), ("$max", run.MemoryHighWater), ("$revision", run.Id)), CultureInfo.InvariantCulture);
            if (count != 1) throw new InvariantException("Relation references a memory outside the run snapshot.");
        }
        Exec(db, tx, "INSERT OR IGNORE INTO relations(memory_id,related_memory_id,kind,related_at,run_id) VALUES($a,$b,$kind,$at,$run)",
            ("$a", proposal.MemoryId), ("$b", proposal.RelatedMemoryId), ("$kind", proposal.Kind.ToString().ToLowerInvariant()), ("$at", Stamp(now)), ("$run", run.Id));
        tx.Commit();
    }

    public void FinishRun(long runId, string status, DateTimeOffset now)
    {
        if (status is not ("complete" or "budget_exhausted")) throw new InputException("Unsupported terminal run status.");
        using var db = Open();
        Exec(db, null, "UPDATE runs SET status=$status,finished_at=$at WHERE id=$id AND status='running'", ("$status", status), ("$at", Stamp(now)), ("$id", runId));
    }

    public UsageReservation ReserveUsage(long? runId, string model, string operation, decimal maximumUsd, DateTimeOffset now)
    {
        if (maximumUsd < 0) throw new InputException("Usage reservation must not be negative.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (runId is not null)
        {
            var budgetText = Scalar(db, tx, "SELECT budget_usd FROM runs WHERE id=$id AND status='running'", ("$id", runId.Value)) as string;
            if (Convert.ToInt32(Scalar(db, tx, "SELECT COUNT(*) FROM runs WHERE id=$id AND status='running'", ("$id", runId.Value)), CultureInfo.InvariantCulture) != 1)
                throw new InvariantException("Cannot charge an inactive run.");
            if (budgetText is not null && Accounted(db, tx, runId.Value) + maximumUsd > decimal.Parse(budgetText, CultureInfo.InvariantCulture))
                throw new BudgetExceededException("Next API request's maximum cost exceeds the remaining meditation budget.");
        }
        var reservation = new UsageReservation(Id("call_"), runId, model, operation, maximumUsd);
        Exec(db, tx, "INSERT INTO api_calls(id,run_id,model,operation,reserved_usd,created_at) VALUES($id,$run,$model,$op,$cost,$at)",
            ("$id", reservation.Id), ("$run", runId), ("$model", model), ("$op", operation), ("$cost", maximumUsd.ToString(CultureInfo.InvariantCulture)), ("$at", Stamp(now)));
        tx.Commit();
        return reservation;
    }

    public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now)
    {
        if (usage.InputTokens < 0 || usage.CachedInputTokens < 0 || usage.CacheWriteTokens < 0 || usage.OutputTokens < 0 || usage.CostUsd < 0 || usage.CachedInputTokens + usage.CacheWriteTokens > usage.InputTokens)
            throw new InvariantException("Invalid API usage.");
        using var db = Open();
        Exec(db, null, "UPDATE api_calls SET actual_usd=$cost,usage_json=$json,completed_at=$at WHERE id=$id AND completed_at IS NULL",
            ("$cost", usage.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$json", JsonSerializer.Serialize(usage, JsonDefaults.Options)), ("$at", Stamp(now)), ("$id", reservationId));
    }

    private static decimal Accounted(SqliteConnection db, SqliteTransaction? tx, long runId)
    {
        using var command = Command(db, tx, "SELECT COALESCE(actual_usd,reserved_usd) FROM api_calls WHERE run_id=$id", ("$id", runId));
        using var reader = command.ExecuteReader();
        decimal sum = 0;
        while (reader.Read()) sum += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
        return sum;
    }

    public decimal GetRunAccountedUsd(long runId) { using var db = Open(); return Accounted(db, null, runId); }
    public string? GetState(string key) { using var db = Open(); return Scalar(db, null, "SELECT value FROM state WHERE key=$key", ("$key", key)) as string; }
    public void SetState(string key, string value)
    {
        if (key == "root_base") throw new InvariantException("Root base is an immutable corpus setting.");
        using var db = Open();
        Exec(db, null, "INSERT INTO state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        INSERT OR IGNORE INTO state(key,value) VALUES('schema_version','1');
        CREATE TABLE IF NOT EXISTS sources(
            id TEXT PRIMARY KEY,content_hash TEXT NOT NULL UNIQUE,relative_path TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('pending','processing','complete','failed')));
        CREATE TABLE IF NOT EXISTS memories(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,depth INTEGER NOT NULL CHECK(depth>=0),
            content TEXT NOT NULL CHECK(length(content)>0),source_ref TEXT REFERENCES sources(id),created_at TEXT NOT NULL,
            dream_revision INTEGER NOT NULL CHECK(dream_revision>=0),last_recalled_at TEXT,created_by_model TEXT NOT NULL,
            origin_key TEXT NOT NULL UNIQUE,sealed INTEGER NOT NULL DEFAULT 0 CHECK(sealed IN (0,1)),
            CHECK((depth=0 AND source_ref IS NOT NULL AND dream_revision=0) OR (depth>0 AND source_ref IS NULL AND dream_revision>0)));
        CREATE TABLE IF NOT EXISTS derived_from(child_id TEXT REFERENCES memories(id),parent_id TEXT REFERENCES memories(id),PRIMARY KEY(child_id,parent_id),CHECK(child_id<>parent_id));
        CREATE TABLE IF NOT EXISTS memory_roots(memory_id TEXT REFERENCES memories(id),source_id TEXT REFERENCES sources(id),PRIMARY KEY(memory_id,source_id));
        CREATE TABLE IF NOT EXISTS runs(
            id INTEGER PRIMARY KEY AUTOINCREMENT,kind TEXT NOT NULL CHECK(kind IN ('dream','meditation')),period_start TEXT NOT NULL,period_end TEXT NOT NULL,
            started_at TEXT NOT NULL,memory_high_water INTEGER NOT NULL,relation_high_water INTEGER NOT NULL,recall_high_water INTEGER NOT NULL,
            status TEXT NOT NULL, budget_usd TEXT,finished_at TEXT,work_initialized INTEGER NOT NULL DEFAULT 0,UNIQUE(kind,period_start,period_end));
        CREATE TABLE IF NOT EXISTS relations(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,memory_id TEXT NOT NULL REFERENCES memories(id),related_memory_id TEXT NOT NULL REFERENCES memories(id),
            kind TEXT NOT NULL CHECK(kind IN ('positive','negative')),related_at TEXT NOT NULL,run_id INTEGER NOT NULL REFERENCES runs(id),
            UNIQUE(memory_id,related_memory_id,kind),CHECK(memory_id<>related_memory_id));
        CREATE INDEX IF NOT EXISTS relations_owner_time ON relations(memory_id,related_at);
        CREATE TABLE IF NOT EXISTS recall_events(seq INTEGER PRIMARY KEY AUTOINCREMENT,memory_id TEXT NOT NULL REFERENCES memories(id),recalled_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS recall_time ON recall_events(recalled_at);
        CREATE TABLE IF NOT EXISTS embeddings(memory_id TEXT REFERENCES memories(id),space TEXT NOT NULL,dimensions INTEGER NOT NULL,vector_json TEXT NOT NULL,PRIMARY KEY(memory_id,space));
        CREATE TABLE IF NOT EXISTS run_work(run_id INTEGER REFERENCES runs(id),work_key TEXT,phase TEXT NOT NULL,memory_id TEXT NOT NULL REFERENCES memories(id),ordinal INTEGER NOT NULL,
            status TEXT NOT NULL,proposal_json TEXT,model TEXT,PRIMARY KEY(run_id,work_key));
        CREATE TABLE IF NOT EXISTS rejected_proposals(run_id INTEGER REFERENCES runs(id),work_key TEXT,proposal_index INTEGER,reason TEXT NOT NULL,PRIMARY KEY(run_id,work_key,proposal_index));
        CREATE TABLE IF NOT EXISTS api_calls(id TEXT PRIMARY KEY,run_id INTEGER REFERENCES runs(id),model TEXT NOT NULL,operation TEXT NOT NULL,
            reserved_usd TEXT NOT NULL,actual_usd TEXT,usage_json TEXT,created_at TEXT NOT NULL,completed_at TEXT);
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(id,content,content='memories',content_rowid='seq',tokenize='unicode61');
        CREATE TRIGGER IF NOT EXISTS memories_search AFTER INSERT ON memories BEGIN
            INSERT INTO memory_fts(rowid,id,content) VALUES(new.seq,new.id,new.content);
        END;
        CREATE TRIGGER IF NOT EXISTS immutable_memory BEFORE UPDATE ON memories
        WHEN old.id IS NOT new.id OR old.depth IS NOT new.depth OR old.content IS NOT new.content OR old.source_ref IS NOT new.source_ref
            OR old.created_at IS NOT new.created_at OR old.dream_revision IS NOT new.dream_revision OR old.created_by_model IS NOT new.created_by_model
            OR old.origin_key IS NOT new.origin_key OR old.seq IS NOT new.seq OR (old.sealed=1 AND new.sealed<>1)
        BEGIN SELECT RAISE(ABORT,'Memory content and provenance are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_memory_delete BEFORE DELETE ON memories BEGIN SELECT RAISE(ABORT,'Memory history is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS immutable_source BEFORE UPDATE ON sources
        WHEN old.id IS NOT new.id OR old.content_hash IS NOT new.content_hash OR old.relative_path IS NOT new.relative_path OR old.created_at IS NOT new.created_at
        BEGIN SELECT RAISE(ABORT,'Source metadata is immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_source_delete BEFORE DELETE ON sources BEGIN SELECT RAISE(ABORT,'Sources are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS parent_layer BEFORE INSERT ON derived_from
        WHEN (SELECT sealed FROM memories WHERE id=new.child_id)<>0 OR
             (SELECT depth FROM memories WHERE id=new.child_id)<>(SELECT depth+1 FROM memories WHERE id=new.parent_id)
        BEGIN SELECT RAISE(ABORT,'Parents must be fixed at birth and exactly one depth below'); END;
        CREATE TRIGGER IF NOT EXISTS no_parent_update BEFORE UPDATE ON derived_from BEGIN SELECT RAISE(ABORT,'Parents are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_parent_delete BEFORE DELETE ON derived_from BEGIN SELECT RAISE(ABORT,'Parents are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS roots_birth BEFORE INSERT ON memory_roots WHEN (SELECT sealed FROM memories WHERE id=new.memory_id)<>0
        BEGIN SELECT RAISE(ABORT,'Roots are fixed at birth'); END;
        CREATE TRIGGER IF NOT EXISTS no_root_update BEFORE UPDATE ON memory_roots BEGIN SELECT RAISE(ABORT,'Roots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_root_delete BEFORE DELETE ON memory_roots BEGIN SELECT RAISE(ABORT,'Roots are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_relation_update BEFORE UPDATE ON relations BEGIN SELECT RAISE(ABORT,'Relation records are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS no_relation_delete BEFORE DELETE ON relations BEGIN SELECT RAISE(ABORT,'Relation records are immutable'); END;
        """;
}
