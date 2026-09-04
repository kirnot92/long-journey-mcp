namespace LongJourney.Core;

/// <summary>A process lifetime OS lock, acquired before SQLite/source recovery.</summary>
public sealed class CorpusLease : IDisposable
{
    private readonly FileStream _lockStream;

    public CorpusLease(EngineOptions options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var lockPath = Path.Combine(options.DataDirectory, ".server.lock");
        try
        {
            _lockStream = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InputException("The corpus is already owned by another server, or its lock file is unavailable. Connect clients to the running server.");
        }
    }

    public void Dispose()
    {
        _lockStream.Dispose();
    }
}
