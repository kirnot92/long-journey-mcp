using System.Reflection;
using LongJourney.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LongJourney.Tests;

public sealed partial class ServerTests
{
    [Fact]
    public async Task InitializeProvidesMemoryGuidanceBeforeToolDiscoveryWithoutCognitionOrSources()
    {
        await using var host = await RunningHost.StartAsync();
        var initialized = await host.RpcAsync("initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "discovery-tests", version = "1.0" }
        });

        var result = initialized.GetProperty("result");
        var serverInfo = result.GetProperty("serverInfo");
        var entryAssembly = Assembly.GetEntryAssembly()!.GetName();
        Assert.Equal(entryAssembly.Name, serverInfo.GetProperty("name").GetString());
        Assert.Equal(entryAssembly.Version!.ToString(), serverInfo.GetProperty("version").GetString());
        var description = serverInfo.GetProperty("description").GetString();
        Assert.Contains("Shared long-term memory across agents and sessions", description);
        Assert.Contains("preferences, constraints, decisions, and outcomes", description);
        Assert.Contains("corrections", description);
        Assert.Contains("original evidence", description);
        Assert.Contains("Use recall to find concrete experiences", description);
        Assert.Contains("use think to find accumulated philosophy", description);

        var instructions = result.GetProperty("instructions").GetString();
        Assert.Contains("Use recall", instructions);
        Assert.Contains("write a concrete query", instructions);
        Assert.Contains("Use think", instructions);
        Assert.Contains("write topic around the broader idea or tension", instructions);
        Assert.Contains("without depth filtering or preference", instructions);
        Assert.Contains("Use remember", instructions);
        Assert.Contains("Use trace", instructions);
        Assert.Contains("Before a context reset or session end", instructions);
        Assert.Contains("detailed tool definitions before calling", instructions);
        Assert.Equal(0, host.Cognition.Calls);
        var overview = host.App.Services.GetRequiredService<IInspectionReader>().BrowseMemories(new());
        Assert.Equal(0, overview.Statistics.Sources);
    }
}
