using LongJourney.Core;

namespace LongJourney.Server;

/// <summary>A process lifetime OS lock, acquired before SQLite/source recovery.</summary>
public sealed class CorpusLease : IDisposable
{
    private readonly FileStream stream;

    public CorpusLease(EngineOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        try
        {
            stream = new FileStream(Path.Combine(options.DataDirectory, ".server.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InputException("The corpus is already owned by another server, or its lock file is unavailable. Connect clients to the running server.");
        }
    }

    public void Dispose() => stream.Dispose();
}
