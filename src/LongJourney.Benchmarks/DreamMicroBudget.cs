using System.Globalization;
using System.Text.Json;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Benchmarks;

// This accounting database covers every condition and evaluator, without scheduling a consolidation run.
public sealed class DreamMicroBudget
{
    private readonly decimal capUsd;
    public string DatabasePath { get; }

    public DreamMicroBudget(string directory, decimal capUsd = 20m)
    {
        if (capUsd <= 0 || capUsd > 20m)
        {
            throw new InputException("Dream microbenchmark budget must be positive and at most USD 20.");
        }
        this.capUsd = capUsd;
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(Path.GetFullPath(directory), "budget.db");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS api_calls (
                id TEXT PRIMARY KEY, run_id INTEGER, model TEXT NOT NULL, operation TEXT NOT NULL,
                reserved_usd TEXT NOT NULL, actual_usd TEXT, usage_json TEXT,
                created_at TEXT NOT NULL, completed_at TEXT);
            INSERT OR IGNORE INTO settings(key, value) VALUES ('cap_usd', $cap);
            """;
        command.Parameters.AddWithValue("$cap", Format(capUsd));
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM settings WHERE key = 'cap_usd'";
        command.Parameters.Clear();
        var savedCap = decimal.Parse((string)command.ExecuteScalar()!, CultureInfo.InvariantCulture);
        if (savedCap != capUsd)
        {
            throw new InputException("The existing microbenchmark budget differs from the requested cap.");
        }
        command.CommandText = "SELECT COUNT(*) FROM api_calls WHERE completed_at IS NULL";
        command.Parameters.Clear();
        if ((long)command.ExecuteScalar()! > 0)
        {
            throw new InvariantException("Unresolved API usage exists; automatic microbenchmark resume is blocked.");
        }
        if (ReadTotal().SettledUsd > capUsd)
        {
            throw new BudgetExceededException("The recorded microbenchmark cost exceeds its budget.");
        }
    }

    public IUsageLedger Scope(SqliteMemoryStore localStore, string scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.EndsWith('/'))
        {
            throw new InputException("A nonempty microbenchmark usage scope is required.");
        }
        return new ScopedLedger(this, localStore, scope);
    }

    public UsageTotals ReadTotal() => BenchmarkUsage.ReadPath(DatabasePath);

    public IReadOnlyDictionary<string, UsageTotals> ReadUsageByOperation()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT DISTINCT operation FROM api_calls ORDER BY operation";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var scopedOperation = reader.GetString(0);
            var operation = scopedOperation[(scopedOperation.LastIndexOf('/') + 1)..];
            var usage = BenchmarkUsage.ReadPath(DatabasePath, scopedOperation);
            if (result.TryGetValue(operation, out var previous))
            {
                usage = new UsageTotals(previous.SettledUsd + usage.SettledUsd,
                    previous.ReservedUsd + usage.ReservedUsd, previous.InputTokens + usage.InputTokens,
                    previous.OutputTokens + usage.OutputTokens, previous.Calls + usage.Calls);
            }
            result[operation] = usage;
        }
        return result;
    }

    public void ExportCalls(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var db = Open();
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

    private string Reserve(string model, string operation, decimal maximumUsd, DateTimeOffset now)
    {
        if (maximumUsd < 0)
        {
            throw new InputException("Usage reservation must not be negative.");
        }
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(actual_usd, reserved_usd) FROM api_calls";
        decimal accounted = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                accounted += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            }
        }
        if (accounted + maximumUsd > capUsd)
        {
            throw new BudgetExceededException("Next API request's maximum cost exceeds the shared Dream microbenchmark budget.");
        }
        var id = "micro_call_" + Guid.NewGuid().ToString("N");
        command.CommandText = """
            INSERT INTO api_calls(id, model, operation, reserved_usd, created_at)
            VALUES ($id, $model, $operation, $reserved, $now)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$reserved", Format(maximumUsd));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        transaction.Commit();
        return id;
    }

    private void Complete(string globalId, ApiUsage usage, DateTimeOffset now)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE api_calls SET actual_usd = $cost, usage_json = $usage, completed_at = $now
            WHERE id = $id AND completed_at IS NULL
            """;
        command.Parameters.AddWithValue("$id", globalId);
        command.Parameters.AddWithValue("$cost", Format(usage.CostUsd));
        command.Parameters.AddWithValue("$usage", JsonSerializer.Serialize(usage, JsonDefaults.Options));
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        db.Open();
        return db;
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed record Mapping(string GlobalId, string BudgetPath, string Scope, decimal ReservedUsd);

    private sealed class ScopedLedger(DreamMicroBudget budget, SqliteMemoryStore localStore, string scope) : IUsageLedger
    {
        public UsageReservation ReserveUsage(long? runId, string model, string operation, decimal maximumUsd, DateTimeOffset now)
        {
            // A crash between these writes keeps the global maximum charged, including orphan reservations.
            var globalId = budget.Reserve(model, scope + "/" + operation, maximumUsd, now);
            var local = localStore.ReserveUsage(runId, model, operation, maximumUsd, now);
            localStore.SetState("micro_budget/" + local.Id,
                JsonSerializer.Serialize(new Mapping(globalId, budget.DatabasePath, scope, maximumUsd), JsonDefaults.Options));
            return local;
        }

        public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now)
        {
            var mappingJson = localStore.GetState("micro_budget/" + reservationId)
                ?? throw new InvariantException("Microbenchmark usage reservation mapping is missing.");
            var mapping = JsonSerializer.Deserialize<Mapping>(mappingJson, JsonDefaults.Options)
                ?? throw new InvariantException("Microbenchmark usage reservation mapping is invalid.");
            if (mapping.Scope != scope || mapping.BudgetPath != budget.DatabasePath)
            {
                throw new InvariantException("Microbenchmark usage reservation belongs to a different scope or budget.");
            }
            // Core validates usage before releasing its reservation. A crash before global settlement is conservative.
            localStore.CompleteUsage(reservationId, usage, now);
            budget.Complete(mapping.GlobalId, usage, now);
            if (usage.CostUsd > mapping.ReservedUsd)
            {
                throw new InvariantException("Known API cost exceeded its reserved maximum; microbenchmark execution must stop.");
            }
        }
    }
}
