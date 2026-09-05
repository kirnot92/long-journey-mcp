using LongJourney.Core;
using Microsoft.Extensions.DependencyInjection;

namespace LongJourney.Tests;

public sealed partial class ServerTests
{
    [Theory]
    [InlineData(null, null, 4000, 3)]
    [InlineData(12, 2, 12, 2)]
    public async Task RememberAdvertisesConfiguredBoundsAndRejectsOversizedRaw(
        int? configuredRawLimit, int? configuredObservationLimit, int rawLimit, int observationLimit)
    {
        await using var host = await RunningHost.StartAsync(builder =>
        {
            if (configuredRawLimit is not null)
            {
                builder.Configuration["Engine:MaxRawCharacters"] = configuredRawLimit.Value.ToString();
            }
            if (configuredObservationLimit is not null)
            {
                builder.Configuration["Engine:MaxObservations"] = configuredObservationLimit.Value.ToString();
            }
        });
        var listed = await host.RpcAsync("tools/list", new { });
        var remember = Assert.Single(listed.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "remember");
        var description = remember.GetProperty("description").GetString();
        Assert.Contains($"{rawLimit} UTF-16 code units", description);
        Assert.Contains($"0 to {observationLimit} observations", description);
        Assert.Equal(0, host.Cognition.Calls);

        // The emoji occupies two UTF-16 code units, taking raw exactly one unit over the advertised bound.
        var raw = new string('a', rawLimit - 1) + "😀";
        var rejected = await host.RpcAsync("tools/call", new
        {
            name = "remember",
            arguments = new { raw }
        });
        var result = rejected.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var message = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains($"{raw.Length} UTF-16", message);
        Assert.Contains(rawLimit.ToString(), message);
        Assert.Equal(0, host.Cognition.Calls);
        var overview = host.App.Services.GetRequiredService<IInspectionReader>().BrowseMemories(new());
        Assert.Equal(0, overview.Statistics.Sources);
    }
}
