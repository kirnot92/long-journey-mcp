using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LongJourney.Core;

/// <summary>Preserves exact raw text in immutable files. The store owns database registration and locking.</summary>
internal sealed class SourceArchive
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const string HeaderEnd = "\n---\n\n";
    private readonly string _corpusDirectory;
    private readonly string _sourcesDirectory;

    public SourceArchive(string corpusDirectory)
    {
        _corpusDirectory = corpusDirectory;
        _sourcesDirectory = Path.Combine(corpusDirectory, "sources");
        Directory.CreateDirectory(_sourcesDirectory);
    }

    public static string ComputeContentHash(string raw)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(raw)));
    }

    public WriteOperation WriteImmutable(string raw, string contentHash, DateTimeOffset createdAt)
    {
        var sourceId = "src_" + contentHash;
        var relativePath = $"sources/{createdAt.UtcDateTime:yyyy/MM/dd}/{sourceId}.md";
        var source = new SourceRecord(
            sourceId, contentHash, relativePath, createdAt.ToUniversalTime(), "pending");

        var path = ResolvePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timestamp = source.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        var header = $"---\nid: {source.Id}\ncreated_at: {timestamp}\ncontent_sha256: {contentHash}\n---\n\n";
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Utf8.GetBytes(header + raw));
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                var existingArtifact = ParseFile(path);
                if (existingArtifact.Raw != raw)
                {
                    throw new InvariantException("Existing source artifact differs from its content hash.");
                }

                // The immutable file may predate this attempt; keep its original created_at.
                source = existingArtifact.Source;
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return new WriteOperation(new SourceArtifact(source, raw), temporaryPath);
        }
        catch
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    public SourceArtifact Read(SourceRecord source)
    {
        var artifact = ParseFile(ResolvePath(source.RelativePath));
        if (artifact.Source.Id != source.Id ||
            artifact.Source.ContentHash != source.ContentHash ||
            artifact.Source.CreatedAt != source.CreatedAt)
        {
            throw new InvariantException("Source metadata does not match its immutable artifact.");
        }

        return new SourceArtifact(source, artifact.Raw);
    }

    public IEnumerable<SourceArtifact> EnumerateArtifacts()
    {
        var paths = Directory.EnumerateFiles(
            _sourcesDirectory, "src_*.md", SearchOption.AllDirectories);
        foreach (var path in paths)
        {
            yield return ParseFile(path);
        }
    }

    private string ResolvePath(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_corpusDirectory, relativePath));
        var corpusPrefix = _corpusDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(corpusPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvariantException("Source path is outside the corpus directory.");
        }

        return fullPath;
    }

    private SourceArtifact ParseFile(string path)
    {
        var text = File.ReadAllText(path, Utf8);
        var headerEnd = text.IndexOf(HeaderEnd, StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal) || headerEnd < 0)
        {
            throw new InvariantException($"Invalid source header: {Path.GetFileName(path)}");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text[4..headerEnd].Split('\n'))
        {
            var field = line.Split(": ", 2, StringSplitOptions.None);
            var value = field.Length == 2 ? field[1] : "";
            fields.Add(field[0], value);
        }

        // Do not trim or normalize the raw text: its exact bytes define source identity.
        var raw = text[(headerEnd + HeaderEnd.Length)..];
        var hash = ComputeContentHash(raw);
        if (!fields.TryGetValue("content_sha256", out var expectedHash) ||
            expectedHash != hash ||
            fields["id"] != "src_" + hash)
        {
            throw new InvariantException("Source archive integrity check failed.");
        }

        var relativePath = Path.GetRelativePath(_corpusDirectory, path).Replace('\\', '/');
        var createdAt = DateTimeOffset.Parse(
            fields["created_at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var source = new SourceRecord(fields["id"], hash, relativePath, createdAt, "pending");
        return new SourceArtifact(source, raw);
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Keeps temporary-file cleanup after the store's registration transaction.</summary>
    internal sealed class WriteOperation(SourceArtifact artifact, string temporaryPath) : IDisposable
    {
        public SourceArtifact Artifact { get; } = artifact;

        public void Dispose()
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }
}
