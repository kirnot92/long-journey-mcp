using System.Globalization;
using System.Text.Json;
using LongJourney.Core;
using Microsoft.Data.Sqlite;

namespace LongJourney.Benchmarks;

public sealed class ExperimentBudgetExceededException() :
    Exception("The next request's maximum cost exceeds the remaining experiment budget.");

public sealed record BenchmarkUsage(
    decimal ActualUsd, decimal ReservedUsd, long InputTokens, long OutputTokens,
    long CachedInputTokens, long CacheWriteTokens, int Calls, int UnsettledCalls);

/// <summary>
/// Caches usage derived from each corpus's api_calls rows. The runner holds the exclusive
/// experiment and corpus leases, and every request uses this gate; SQLite remains the only ledger.
/// </summary>
public sealed class BenchmarkBudget
{
    private static readonly BenchmarkUsage EmptyUsage = new(0, 0, 0, 0, 0, 0, 0, 0);
    private readonly object _gate = new();
    private readonly Dictionary<string, BenchmarkUsage> _usageByDatabase = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly decimal _maximumUsd;
    private BenchmarkUsage _usage = EmptyUsage;
    private string? _pendingRefresh;

    public BenchmarkBudget(IReadOnlyList<string> databasePaths, decimal maximumUsd)
    {
        ArgumentNullException.ThrowIfNull(databasePaths);
        if (maximumUsd <= 0)
        {
            throw new InputException("Experiment budget must be greater than zero.");
        }
        _maximumUsd = maximumUsd;
        foreach (var path in databasePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InputException("Registered corpus database paths must be nonempty.");
            }
            var databasePath = Path.GetFullPath(path);
            if (!_usageByDatabase.TryAdd(databasePath, EmptyUsage))
            {
                throw new InputException("Registered corpus database paths must be unique.");
            }
            RefreshCorpusUsage(databasePath);
        }
    }

    public IUsageLedger ForCorpus(SqliteMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var databasePath = Path.GetFullPath(store.DatabasePath);
        lock (_gate)
        {
            if (!_usageByDatabase.ContainsKey(databasePath))
            {
                throw new InputException("The corpus database is not registered with this experiment budget.");
            }
            return new CorpusBudget(this, store, databasePath);
        }
    }

    public BenchmarkUsage ReadUsage()
    {
        lock (_gate)
        {
            RefreshPendingUsage();
            return _usage;
        }
    }

    public static BenchmarkUsage ReadUsage(IReadOnlyList<string> paths)
    {
        var usage = EmptyUsage;
        foreach (var path in paths)
        {
            usage = ReplaceContribution(usage, EmptyUsage, ReadCorpusUsage(path));
        }
        return usage;
    }

    private static BenchmarkUsage ReadCorpusUsage(string path)
    {
        if (!File.Exists(path))
        {
            return EmptyUsage;
        }
        decimal actual = 0;
        decimal reserved = 0;
        long input = 0;
        long output = 0;
        long cached = 0;
        long writes = 0;
        var calls = 0;
        var unsettled = 0;
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT reserved_usd, actual_usd, usage_json
            FROM api_calls
            ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            calls++;
            if (reader.IsDBNull(1))
            {
                unsettled++;
                reserved += decimal.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
                continue;
            }
            actual += decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            var usage = JsonSerializer.Deserialize<ApiUsage>(reader.GetString(2), JsonDefaults.Options)
                ?? throw new InvariantException("A completed API call has invalid usage.");
            input += usage.InputTokens;
            output += usage.OutputTokens;
            cached += usage.CachedInputTokens;
            writes += usage.CacheWriteTokens;
        }
        return new BenchmarkUsage(actual, reserved, input, output, cached, writes, calls, unsettled);
    }

    private void RefreshCorpusUsage(string databasePath)
    {
        var current = ReadCorpusUsage(databasePath);
        _usage = ReplaceContribution(_usage, _usageByDatabase[databasePath], current);
        _usageByDatabase[databasePath] = current;
    }

    private void RefreshPendingUsage()
    {
        if (_pendingRefresh is null)
        {
            return;
        }
        // A failed post-write read must never let another corpus spend against a stale aggregate.
        RefreshCorpusUsage(_pendingRefresh);
        _pendingRefresh = null;
    }

    private static BenchmarkUsage ReplaceContribution(
        BenchmarkUsage total, BenchmarkUsage previous, BenchmarkUsage current)
    {
        return new BenchmarkUsage(
            total.ActualUsd - previous.ActualUsd + current.ActualUsd,
            total.ReservedUsd - previous.ReservedUsd + current.ReservedUsd,
            total.InputTokens - previous.InputTokens + current.InputTokens,
            total.OutputTokens - previous.OutputTokens + current.OutputTokens,
            total.CachedInputTokens - previous.CachedInputTokens + current.CachedInputTokens,
            total.CacheWriteTokens - previous.CacheWriteTokens + current.CacheWriteTokens,
            total.Calls - previous.Calls + current.Calls,
            total.UnsettledCalls - previous.UnsettledCalls + current.UnsettledCalls);
    }

    private sealed class CorpusBudget(
        BenchmarkBudget budget, SqliteMemoryStore store, string databasePath) : IUsageLedger
    {
        public UsageReservation ReserveUsage(long? runId, string model, string operation,
            decimal maximumUsd, DateTimeOffset now)
        {
            lock (budget._gate)
            {
                budget.RefreshPendingUsage();
                budget.RefreshCorpusUsage(databasePath);
                if (budget._usage.ActualUsd + budget._usage.ReservedUsd + maximumUsd > budget._maximumUsd)
                {
                    throw new ExperimentBudgetExceededException();
                }
                budget._pendingRefresh = databasePath;
                try
                {
                    return store.ReserveUsage(runId, model, operation, maximumUsd, now);
                }
                finally
                {
                    budget.RefreshPendingUsage();
                }
            }
        }

        public void CompleteUsage(string reservationId, ApiUsage usage, DateTimeOffset now)
        {
            lock (budget._gate)
            {
                budget.RefreshPendingUsage();
                budget._pendingRefresh = databasePath;
                try
                {
                    store.CompleteUsage(reservationId, usage, now);
                }
                finally
                {
                    budget.RefreshPendingUsage();
                }
            }
        }
    }
}
