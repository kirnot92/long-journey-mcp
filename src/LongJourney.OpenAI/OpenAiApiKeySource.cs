using LongJourney.Core;

namespace LongJourney.OpenAI;

/// <summary>Reads API credentials without copying secrets into configuration or process environment.</summary>
public sealed class OpenAiApiKeySource
{
    private readonly string _keyFilePath;
    private readonly Func<string?> _readEnvironmentKey;

    public OpenAiApiKeySource(
        string contentRoot,
        string? configuredFile = null,
        Func<string?>? readEnvironmentKey = null)
    {
        _keyFilePath = ResolveKeyFile(contentRoot, configuredFile);
        _readEnvironmentKey = readEnvironmentKey ?? (() => Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    }

    public string? Read()
    {
        var environmentKey = _readEnvironmentKey();
        if (!string.IsNullOrWhiteSpace(environmentKey))
        {
            return NormalizeKey(environmentKey);
        }

        try
        {
            return NormalizeKey(File.ReadAllText(_keyFilePath));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The original exception may contain local paths. Report only the actionable setting.
            throw new InputException("Cannot read the OpenAI key file. Check key.txt or OpenAI:ApiKeyFile.");
        }
    }

    private static string? NormalizeKey(string text)
    {
        var key = text.Trim();
        if (key.Length == 0)
        {
            return null;
        }

        foreach (var character in key)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw new InputException("The OpenAI API key must contain one token without internal whitespace.");
            }
        }

        return key;
    }

    private static string ResolveKeyFile(string contentRoot, string? configuredFile)
    {
        if (configuredFile is not null)
        {
            if (string.IsNullOrWhiteSpace(configuredFile))
            {
                throw new InputException("OpenAI:ApiKeyFile must not be empty.");
            }

            return Path.GetFullPath(configuredFile, contentRoot);
        }

        var localFile = Path.Combine(contentRoot, "key.txt");
        if (File.Exists(localFile))
        {
            return localFile;
        }

        // Project launches use their project directory. Recognize only this repository layout.
        var repositoryRoot = Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
        if (File.Exists(Path.Combine(contentRoot, "LongJourney.Server.csproj")) &&
            File.Exists(Path.Combine(repositoryRoot, "LongJourney.slnx")))
        {
            return Path.Combine(repositoryRoot, "key.txt");
        }

        return localFile;
    }
}
