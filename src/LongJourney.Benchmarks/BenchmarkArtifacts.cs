using System.Text.Json;
using LongJourney.Core;

namespace LongJourney.Benchmarks;

public sealed record BenchmarkUnit(string QuestionId, BenchmarkVariant Variant, string Directory)
{
    public string CorpusDirectory => Path.Combine(Directory, "corpus");
    public string DatabasePath => Path.Combine(CorpusDirectory, "memory.db");
}

public sealed record ExperimentManifest(
    string Fingerprint, string DatasetHash, string ProtocolVersion,
    string PromptVersion, DateTimeOffset StartedAt, BenchmarkOptions Options,
    IReadOnlyList<BenchmarkUnit> Units);

public static class BenchmarkArtifacts
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, JsonDefaults.Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException("A benchmark artifact is empty or invalid.");
    }

    public static IReadOnlyList<BenchmarkUnit> Units(
        BenchmarkOptions options, IReadOnlyList<BenchmarkCase> cases)
    {
        var units = new List<BenchmarkUnit>();
        foreach (var item in cases)
        {
            foreach (var variant in options.Variants)
            {
                var directory = Path.Combine(options.OutputDirectory,
                    BenchmarkOptions.CaseDirectoryName(item.Id), variant.ToString());
                units.Add(new BenchmarkUnit(item.Id, variant, directory));
            }
        }
        return units;
    }

    public static IReadOnlyList<string> DatabasePaths(IReadOnlyList<BenchmarkUnit> units)
    {
        var paths = new List<string>();
        foreach (var unit in units)
        {
            paths.Add(unit.DatabasePath);
        }
        return paths;
    }

    public static FileStream AcquireExperiment(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            return new FileStream(Path.Combine(outputDirectory, ".experiment.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InputException("This experiment is already running, or its lock file is unavailable.");
        }
    }
}
