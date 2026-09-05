namespace LongJourney.Core;

internal static class SqliteSchema
{
    internal const string Create = """
        CREATE TABLE IF NOT EXISTS state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        INSERT OR IGNORE INTO state (key, value)
        VALUES ('schema_version', '1');

        CREATE TABLE IF NOT EXISTS sources (
            id TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL UNIQUE,
            relative_path TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('pending', 'processing', 'complete', 'failed'))
        );

        CREATE TABLE IF NOT EXISTS memories (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            id TEXT NOT NULL UNIQUE,
            depth INTEGER NOT NULL CHECK (depth >= 0),
            content TEXT NOT NULL CHECK (length(content) > 0),
            source_ref TEXT REFERENCES sources (id),
            created_at TEXT NOT NULL,
            dream_revision INTEGER NOT NULL CHECK (dream_revision >= 0),
            last_recalled_at TEXT,
            created_by_model TEXT NOT NULL,
            origin_key TEXT NOT NULL UNIQUE,
            sealed INTEGER NOT NULL DEFAULT 0 CHECK (sealed IN (0, 1)),
            CHECK (
                (depth = 0 AND source_ref IS NOT NULL AND dream_revision = 0)
                OR (depth > 0 AND source_ref IS NULL AND dream_revision > 0)
            )
        );

        CREATE TABLE IF NOT EXISTS derived_from (
            child_id TEXT REFERENCES memories (id),
            parent_id TEXT REFERENCES memories (id),
            PRIMARY KEY (child_id, parent_id),
            CHECK (child_id <> parent_id)
        );

        CREATE TABLE IF NOT EXISTS memory_roots (
            memory_id TEXT REFERENCES memories (id),
            source_id TEXT REFERENCES sources (id),
            PRIMARY KEY (memory_id, source_id)
        );

        CREATE TABLE IF NOT EXISTS runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            kind TEXT NOT NULL CHECK (kind IN ('dream', 'meditation')),
            period_start TEXT NOT NULL,
            period_end TEXT NOT NULL,
            started_at TEXT NOT NULL,
            memory_high_water INTEGER NOT NULL,
            relation_high_water INTEGER NOT NULL,
            recall_high_water INTEGER NOT NULL,
            status TEXT NOT NULL,
            budget_usd TEXT,
            finished_at TEXT,
            work_initialized INTEGER NOT NULL DEFAULT 0,
            UNIQUE (kind, period_start, period_end)
        );

        CREATE TABLE IF NOT EXISTS relations (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            memory_id TEXT NOT NULL REFERENCES memories (id),
            related_memory_id TEXT NOT NULL REFERENCES memories (id),
            kind TEXT NOT NULL CHECK (kind IN ('positive', 'negative')),
            related_at TEXT NOT NULL,
            run_id INTEGER NOT NULL REFERENCES runs (id),
            UNIQUE (memory_id, related_memory_id, kind),
            CHECK (memory_id <> related_memory_id)
        );

        CREATE INDEX IF NOT EXISTS relations_owner_time
            ON relations (memory_id, related_at);

        CREATE TABLE IF NOT EXISTS recall_events (
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            memory_id TEXT NOT NULL REFERENCES memories (id),
            recalled_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS recall_time
            ON recall_events (recalled_at);

        CREATE TABLE IF NOT EXISTS embeddings (
            memory_id TEXT REFERENCES memories (id),
            space TEXT NOT NULL,
            dimensions INTEGER NOT NULL,
            vector_json TEXT NOT NULL,
            PRIMARY KEY (memory_id, space)
        );

        CREATE TABLE IF NOT EXISTS run_work (
            run_id INTEGER REFERENCES runs (id),
            work_key TEXT,
            phase TEXT NOT NULL,
            memory_id TEXT NOT NULL REFERENCES memories (id),
            ordinal INTEGER NOT NULL,
            status TEXT NOT NULL,
            proposal_json TEXT,
            model TEXT,
            PRIMARY KEY (run_id, work_key)
        );

        CREATE TABLE IF NOT EXISTS rejected_proposals (
            run_id INTEGER REFERENCES runs (id),
            work_key TEXT,
            proposal_index INTEGER,
            reason TEXT NOT NULL,
            PRIMARY KEY (run_id, work_key, proposal_index)
        );

        CREATE TABLE IF NOT EXISTS api_calls (
            id TEXT PRIMARY KEY,
            run_id INTEGER REFERENCES runs (id),
            model TEXT NOT NULL,
            operation TEXT NOT NULL,
            reserved_usd TEXT NOT NULL,
            actual_usd TEXT,
            usage_json TEXT,
            created_at TEXT NOT NULL,
            completed_at TEXT
        );

        CREATE TABLE IF NOT EXISTS activity_operations (
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            origin TEXT NOT NULL,
            parent_id TEXT,
            source_id TEXT,
            run_id INTEGER,
            work_key TEXT,
            charged_run_id INTEGER,
            started_at TEXT NOT NULL,
            completed_at TEXT,
            status TEXT NOT NULL,
            error_type TEXT,
            details_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS activity_operations_started ON activity_operations(started_at);
        CREATE TABLE IF NOT EXISTS activity_relation_results (
            run_id INTEGER NOT NULL,
            work_key TEXT NOT NULL,
            proposal_index INTEGER NOT NULL,
            activity_id TEXT,
            at TEXT NOT NULL,
            outcome TEXT NOT NULL,
            memory_id TEXT NOT NULL,
            related_memory_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            reason TEXT,
            PRIMARY KEY(run_id,work_key,proposal_index)
        );
        CREATE TABLE IF NOT EXISTS activity_api_calls (
            api_call_id TEXT PRIMARY KEY REFERENCES api_calls(id),
            activity_id TEXT,
            settings_json TEXT
        );
        CREATE TRIGGER IF NOT EXISTS immutable_activity_relation_result
        BEFORE UPDATE ON activity_relation_results
        BEGIN
            SELECT RAISE(ABORT, 'Relation application results are immutable');
        END;
        CREATE TRIGGER IF NOT EXISTS no_activity_relation_result_delete
        BEFORE DELETE ON activity_relation_results
        BEGIN
            SELECT RAISE(ABORT, 'Relation application results are immutable');
        END;

        CREATE INDEX IF NOT EXISTS memories_created ON memories (created_at DESC, seq DESC);
        CREATE INDEX IF NOT EXISTS memories_depth_created ON memories (depth, created_at DESC, seq DESC);
        CREATE INDEX IF NOT EXISTS memories_source ON memories (source_ref, created_at DESC, seq DESC);
        CREATE INDEX IF NOT EXISTS memories_revision ON memories (dream_revision, created_at DESC, seq DESC);
        CREATE INDEX IF NOT EXISTS relations_recent ON relations (related_at DESC, seq DESC);
        CREATE INDEX IF NOT EXISTS api_calls_run ON api_calls (run_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5 (
            id,
            content,
            content = 'memories',
            content_rowid = 'seq',
            tokenize = 'unicode61'
        );

        CREATE TRIGGER IF NOT EXISTS memories_search
        AFTER INSERT ON memories
        BEGIN
            INSERT INTO memory_fts (rowid, id, content)
            VALUES (new.seq, new.id, new.content);
        END;

        CREATE TRIGGER IF NOT EXISTS immutable_memory
        BEFORE UPDATE ON memories
        WHEN old.id IS NOT new.id
            OR old.depth IS NOT new.depth
            OR old.content IS NOT new.content
            OR old.source_ref IS NOT new.source_ref
            OR old.created_at IS NOT new.created_at
            OR old.dream_revision IS NOT new.dream_revision
            OR old.created_by_model IS NOT new.created_by_model
            OR old.origin_key IS NOT new.origin_key
            OR old.seq IS NOT new.seq
            OR (old.sealed = 1 AND new.sealed <> 1)
        BEGIN
            SELECT RAISE(ABORT, 'Memory content and provenance are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_memory_delete
        BEFORE DELETE ON memories
        BEGIN
            SELECT RAISE(ABORT, 'Memory history is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS immutable_source
        BEFORE UPDATE ON sources
        WHEN old.id IS NOT new.id
            OR old.content_hash IS NOT new.content_hash
            OR old.relative_path IS NOT new.relative_path
            OR old.created_at IS NOT new.created_at
        BEGIN
            SELECT RAISE(ABORT, 'Source metadata is immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_source_delete
        BEFORE DELETE ON sources
        BEGIN
            SELECT RAISE(ABORT, 'Sources are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS parent_layer
        BEFORE INSERT ON derived_from
        WHEN (SELECT sealed FROM memories WHERE id = new.child_id) <> 0
            OR (SELECT depth FROM memories WHERE id = new.child_id)
                <> (SELECT depth + 1 FROM memories WHERE id = new.parent_id)
        BEGIN
            SELECT RAISE(ABORT, 'Parents must be fixed at birth and exactly one depth below');
        END;

        CREATE TRIGGER IF NOT EXISTS no_parent_update
        BEFORE UPDATE ON derived_from
        BEGIN
            SELECT RAISE(ABORT, 'Parents are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_parent_delete
        BEFORE DELETE ON derived_from
        BEGIN
            SELECT RAISE(ABORT, 'Parents are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS roots_birth
        BEFORE INSERT ON memory_roots
        WHEN (SELECT sealed FROM memories WHERE id = new.memory_id) <> 0
        BEGIN
            SELECT RAISE(ABORT, 'Roots are fixed at birth');
        END;

        CREATE TRIGGER IF NOT EXISTS no_root_update
        BEFORE UPDATE ON memory_roots
        BEGIN
            SELECT RAISE(ABORT, 'Roots are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_root_delete
        BEFORE DELETE ON memory_roots
        BEGIN
            SELECT RAISE(ABORT, 'Roots are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_relation_update
        BEFORE UPDATE ON relations
        BEGIN
            SELECT RAISE(ABORT, 'Relation records are immutable');
        END;

        CREATE TRIGGER IF NOT EXISTS no_relation_delete
        BEFORE DELETE ON relations
        BEGIN
            SELECT RAISE(ABORT, 'Relation records are immutable');
        END;
        """;
}
