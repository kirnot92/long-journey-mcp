using LongJourney.Core;
using LongJourney.Server;

namespace LongJourney.Tests;

public sealed partial class OpenAiCognitionTests
{
    [Fact]
    public async Task FileKeyReachesBearerHeaderAndReplacementAppliesToTheNextRequest()
    {
        using var files = new CredentialFiles();
        File.WriteAllText(files.KeyFile, "\uFEFF  first-test-key\r\n");
        var source = new OpenAiApiKeySource(files.DirectoryPath, readEnvironmentKey: () => null);
        var ledger = new Ledger();
        var expectedKey = "first-test-key";
        using var handler = new Handler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(expectedKey, request.Headers.Authorization?.Parameter);
            return Task.FromResult(Response("""{"observations":[]}"""));
        });
        using var http = new HttpClient(handler);
        var client = new OpenAI.OpenAiCognition(http, new(), new(), ledger, TimeProvider.System, source.Read);

        await client.ExtractAsync("a test observation", new(), default);
        expectedKey = "replacement-test-key";
        File.WriteAllText(files.KeyFile, "\t" + expectedKey + "\n");
        await client.ExtractAsync("another test observation", new(), default);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void EnvironmentKeyTakesPriorityWithoutReadingTheFile()
    {
        using var files = new CredentialFiles();
        Directory.CreateDirectory(files.KeyFile);
        var source = new OpenAiApiKeySource(files.DirectoryPath, readEnvironmentKey: () => "environment-test-key");
        Assert.Equal("environment-test-key", source.Read());
    }

    [Fact]
    public void ProjectLaunchFindsTheRepositoryKeyAndPrefersAnExistingProjectKey()
    {
        using var files = new CredentialFiles();
        var project = Path.Combine(files.DirectoryPath, "src", "LongJourney.Server");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(files.DirectoryPath, "LongJourney.slnx"), "");
        File.WriteAllText(Path.Combine(project, "LongJourney.Server.csproj"), "");
        File.WriteAllText(files.KeyFile, "repository-test-key");
        Assert.Equal("repository-test-key", new OpenAiApiKeySource(project, readEnvironmentKey: () => null).Read());

        File.WriteAllText(Path.Combine(project, "key.txt"), "project-test-key");
        Assert.Equal("project-test-key", new OpenAiApiKeySource(project, readEnvironmentKey: () => null).Read());
    }

    [Fact]
    public void ExplicitRelativeAndAbsolutePathsOverrideTheDefaultFile()
    {
        using var files = new CredentialFiles();
        File.WriteAllText(files.KeyFile, "default-test-key");
        var selected = Path.Combine(files.DirectoryPath, "selected.txt");
        File.WriteAllText(selected, "selected-test-key");
        Assert.Equal("selected-test-key", new OpenAiApiKeySource(files.DirectoryPath, "selected.txt", () => null).Read());
        Assert.Equal("selected-test-key", new OpenAiApiKeySource(files.DirectoryPath, selected, () => null).Read());
        Assert.Throws<InputException>(() => new OpenAiApiKeySource(files.DirectoryPath, " "));
    }

    [Fact]
    public void MissingAndEmptyFilesReturnNoKeyAndCanBeFilledLater()
    {
        using var files = new CredentialFiles();
        var source = new OpenAiApiKeySource(files.DirectoryPath, readEnvironmentKey: () => " ");
        Assert.Null(source.Read());
        File.WriteAllText(files.KeyFile, " \r\n\t");
        Assert.Null(source.Read());
        File.WriteAllText(files.KeyFile, "later-test-key");
        Assert.Equal("later-test-key", source.Read());
    }

    [Fact]
    public void InvalidKeyAndFileReadErrorsDoNotExposeContentsOrPaths()
    {
        using var files = new CredentialFiles();
        var source = new OpenAiApiKeySource(files.DirectoryPath, readEnvironmentKey: () => null);
        const string invalidKey = "first-private-token\nsecond-private-token";
        File.WriteAllText(files.KeyFile, invalidKey);
        var invalid = Assert.Throws<InputException>(() => source.Read());
        Assert.DoesNotContain("first-private-token", invalid.ToString());
        Assert.DoesNotContain("second-private-token", invalid.ToString());

        File.Delete(files.KeyFile);
        Directory.CreateDirectory(files.KeyFile);
        var unreadable = Assert.Throws<InputException>(() => source.Read());
        Assert.DoesNotContain(files.DirectoryPath, unreadable.ToString());
        Assert.Null(unreadable.InnerException);
    }

    private sealed class CredentialFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), "long-journey-credentials-" + Guid.NewGuid().ToString("N"));
        public string KeyFile => Path.Combine(DirectoryPath, "key.txt");

        public CredentialFiles()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            // This fixture owns the uniquely named absolute temporary directory created above.
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
